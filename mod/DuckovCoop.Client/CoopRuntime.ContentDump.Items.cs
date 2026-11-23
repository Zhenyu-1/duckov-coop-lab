using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// Full item-table dump for CORE (weapons / bullets / armor / melee stats).
    /// Runs once per launch when the content sentinel exists; waits for
    /// ItemAssetsCollection before enumerating every type id.
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private bool _itemsDumpStarted;
        private bool _rulesetDumpStarted;
        private bool _playerDumpStarted;

        private void TryStartItemDump()
        {
            if (_itemsDumpStarted)
                return;
            _itemsDumpStarted = true;
            StartCoroutine(DumpItemsCoroutine());
        }

        private void TryStartPlayerDump()
        {
            if (_playerDumpStarted)
                return;
            _playerDumpStarted = true;
            StartCoroutine(DumpPlayerCoroutine());
        }

        /// <summary>
        /// Dump the live main character (stats + every equipment slot) once a
        /// character exists. This is how we learn how armor/head armor aggregate.
        /// </summary>
        private IEnumerator DumpPlayerCoroutine()
        {
            const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public;
            const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.Static;
            var levelManagerType = FindTypeByName("LevelManager");
            if (levelManagerType == null)
                yield break;

            var waited = 0f;
            while (waited < 900f)
            {
                object mainCharacter = null;
                try
                {
                    var instance = levelManagerType.GetProperty("Instance", staticFlags)?.GetValue(null);
                    mainCharacter = instance != null
                        ? levelManagerType.GetProperty("MainCharacter", instanceFlags)?.GetValue(instance)
                        : null;
                }
                catch
                {
                    // level manager not ready
                }

                if (mainCharacter is UnityEngine.Object characterObject && characterObject != null)
                {
                    try
                    {
                        WritePlayerDump(mainCharacter);
                    }
                    catch (Exception ex)
                    {
                        Log("[dump] player dump failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                // Keep refreshing so equipment changes (armor/weapon/totem) are captured.
                yield return new WaitForSeconds(30f);
                waited += 30f;
            }

            Log("[dump] player dump gave up (no main character)");
        }

        private void WritePlayerDump(object mainCharacter)
        {
            const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public;
            var builder = new StringBuilder(1024 * 32);
            builder.Append("{\n  \"meta\": { \"built\": \"")
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .Append("\" },\n  \"character\": \"").Append(JsonEscape(((UnityEngine.Object)mainCharacter).name))
                .Append("\",\n  \"characterItem\": ");

            var characterItem = mainCharacter.GetType().GetProperty("CharacterItem")?.GetValue(mainCharacter) ??
                               mainCharacter.GetType().GetProperty("characterItem")?.GetValue(mainCharacter);
            if (characterItem is UnityEngine.Object itemObject)
            {
                var typeId = characterItem.GetType().GetProperty("TypeID")?.GetValue(characterItem) as int? ?? 0;
                builder.Append(DumpItemObject(itemObject, null, typeId));
            }
            else
            {
                builder.Append("null");
            }

            // Where do BodyArmor/HeadArmor come from? Stat modifiers carry their source
            // object (perk, effect, equipment). This is diagnostic only.
            if (characterItem != null)
            {
                builder.Append(",\n  \"statModifiers\": ");
                builder.Append(DumpStatModifiers(characterItem));
                builder.Append(",\n  \"playerStats\": ");
                builder.Append(DumpPlayerStats(characterItem));
                builder.Append(",\n  \"allSlots\": ");
                builder.Append(DumpAllSlots(characterItem));
            }

            var health = mainCharacter.GetType().GetProperty("Health")?.GetValue(mainCharacter);            builder.Append(",\n  \"health\": {");
            if (health != null)
            {
                var healthType = health.GetType();
                var first = true;
                foreach (var name in new[] { "CurrentHealth", "MaxHealth", "BodyArmor", "HeadArmor", "IsDead" })
                {
                    object value = null;
                    try
                    {
                        value = healthType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(health);
                    }
                    catch
                    {
                        // property may throw
                    }

                    if (value == null)
                        continue;
                    if (!first)
                        builder.Append(',');
                    first = false;
                    builder.Append("\n    \"").Append(name).Append("\": ").Append(DescribeValue(value, 1));
                }

                builder.Append("\n  }");
            }
            else
            {
                builder.Append('}');
            }

            builder.Append(",\n  \"slots\": {");
            var firstSlot = true;
            foreach (var slotName in new[]
                     {
                         "MeleeWeaponSlot", "PrimWeaponSlot", "SecWeaponSlot", "ArmorSlot", "HelmatSlot",
                         "BackpackSlot"
                     })
            {
                object slot = null;
                try
                {
                    slot = mainCharacter.GetType().GetMethod(slotName, instanceFlags, null, Type.EmptyTypes, null)
                        ?.Invoke(mainCharacter, null);
                }
                catch
                {
                    // slot may not exist on every character
                }

                if (slot == null)
                    continue;

                object content = null;
                try
                {
                    content = slot.GetType().GetProperty("Content", instanceFlags)?.GetValue(slot);
                }
                catch
                {
                    // empty slot
                }

                if (!firstSlot)
                    builder.Append(',');
                firstSlot = false;
                builder.Append("\n    \"").Append(slotName).Append("\": ");
                if (content is UnityEngine.Object contentObject && contentObject != null)
                {
                    var contentId = content.GetType().GetProperty("TypeID")?.GetValue(content) as int? ?? 0;
                    builder.Append(DumpItemObject(contentObject, null, contentId));
                }
                else
                {
                    builder.Append("null");
                }
            }

            builder.Append("\n  }\n}\n");
            var path = Path.Combine(Application.persistentDataPath, "coop_player_dump.json");
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
            Log($"[dump] player written: {path}");
        }

        private void TryStartRulesetDump()
        {
            if (_rulesetDumpStarted)
                return;
            _rulesetDumpStarted = true;
            StartCoroutine(DumpRulesetCoroutine());
        }

        private IEnumerator DumpRulesetCoroutine()
        {
            const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.Static;
            var rulesManagerType = FindTypeByName("GameRulesManager") ??
                                   FindTypeByFullName("Duckov.Rules.GameRulesManager");
            var levelManagerType = FindTypeByName("LevelManager");
            var waited = 0f;
            while (waited < 900f)
            {
                object rule = null;
                try
                {
                    rule = rulesManagerType?.GetProperty("Current", staticFlags)?.GetValue(null);
                }
                catch
                {
                    // rules manager not ready
                }

                if (rule == null)
                {
                    try
                    {
                        rule = levelManagerType?.GetProperty("Rule", staticFlags)?.GetValue(null);
                    }
                    catch
                    {
                        // level manager not ready (base menu)
                    }
                }

                if (rule != null)
                {
                    var json = "{\n  \"meta\": { \"built\": \"" +
                               DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                               "\" },\n  \"ruleset\": " + DumpPlainValue(rule, 0) + "\n}\n";
                    var path = Path.Combine(Application.persistentDataPath, "coop_ruleset_dump.json");
                    File.WriteAllText(path, json, new UTF8Encoding(false));
                    Log($"[dump] ruleset written: {path} type={rule.GetType().Name}");
                    yield break;
                }

                if (waited <= 0f)
                    Log("[dump] ruleset not ready yet; retrying every 30s");

                yield return new WaitForSeconds(30f);
                waited += 30f;
            }

            Log("[dump] ruleset gave up (enter a raid and relaunch to capture it)");
        }

        private IEnumerator DumpItemsCoroutine()
        {
            const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.Static;
            var assetsType = FindTypeByFullName("ItemStatsSystem.ItemAssetsCollection") ??
                             FindTypeByName("ItemAssetsCollection");
            var filterType = FindTypeByFullName("ItemStatsSystem.ItemFilter") ?? FindTypeByName("ItemFilter");
            if (assetsType == null || filterType == null)
            {
                Log("[dump] items: ItemAssetsCollection/ItemFilter not found");
                yield break;
            }

            var instanceProperty = assetsType.GetProperty("Instance", staticFlags);
            var getAll = assetsType.GetMethod("GetAllTypeIds", staticFlags, null, new[] { filterType }, null);
            var instantiate = assetsType.GetMethod("InstantiateSync", staticFlags, null, new[] { typeof(int) }, null);
            var getMeta = assetsType.GetMethod("GetMetaData", staticFlags, null, new[] { typeof(int) }, null);
            if (getAll == null || instantiate == null)
            {
                Log("[dump] items: methods missing");
                yield break;
            }

            int[] ids = null;
            var waited = 0f;
            while (ids == null && waited < 300f)
            {
                if (instanceProperty?.GetValue(null) != null)
                {
                    var filter = Activator.CreateInstance(filterType);
                    filterType.GetField("minQuality")?.SetValue(filter, -1);
                    filterType.GetField("maxQuality")?.SetValue(filter, 1000);
                    try
                    {
                        ids = (int[])getAll.Invoke(null, new[] { filter });
                    }
                    catch (Exception ex)
                    {
                        Log("[dump] items: search failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                if (ids == null)
                {
                    yield return new WaitForSeconds(5f);
                    waited += 5f;
                }
            }

            if (ids == null || ids.Length == 0)
            {
                Log("[dump] items: no type ids (gave up)");
                yield break;
            }

            Log($"[dump] items start ids={ids.Length}");
            var entries = new List<string>();
            var count = 0;
            foreach (var id in ids)
            {
                if (count >= 2000)
                    break;

                UnityEngine.Object item = null;
                try
                {
                    item = instantiate.Invoke(null, new object[] { id }) as UnityEngine.Object;
                }
                catch (Exception ex)
                {
                    Log($"[dump] items: instantiate {id} failed: {ex.GetType().Name}");
                }

                if (item == null)
                    continue;

                try
                {
                    object meta = null;
                    try
                    {
                        meta = getMeta?.Invoke(null, new object[] { id });
                    }
                    catch
                    {
                        // metadata is best effort
                    }

                    entries.Add(DumpItemObject(item, meta, id));
                }
                catch (Exception ex)
                {
                    Log($"[dump] items: dump {id} failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    UnityEngine.Object.Destroy(item);
                }

                count++;
                if (count % 5 == 0)
                    yield return null;
                if (count % 100 == 0)
                    Log($"[dump] items {count}/{ids.Length}");
            }

            try
            {
                var builder = new StringBuilder(1024 * 512);
                builder.Append("{\n  \"meta\": {\n    \"built\": \"")
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                    .Append("\"\n  },\n  \"count\": ").Append(entries.Count).Append(",\n  \"items\": [");
                for (var i = 0; i < entries.Count; i++)
                {
                    if (i > 0)
                        builder.Append(',');
                    builder.Append('\n').Append("    ").Append(entries[i]);
                }

                builder.Append("\n  ]\n}\n");
                var path = Path.Combine(Application.persistentDataPath, "coop_items_dump.json");
                File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
                Log($"[dump] items written: {path} count={entries.Count}");
            }
            catch (Exception ex)
            {
                Log("[dump] items write failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string DumpItemObject(object item, object meta, int typeId)
        {
            var builder = new StringBuilder();
            var itemType = item.GetType();
            builder.Append("{\n      \"typeId\": ").Append(typeId);
            builder.Append(",\n      \"_type\": \"").Append(JsonEscape(itemType.FullName)).Append('"');
            builder.Append(",\n      \"_name\": \"").Append(JsonEscape(((UnityEngine.Object)item).name)).Append('"');

            // Display data lives on Item properties (the ItemMetaData struct keeps its
            // strings private, so a field dump alone comes back empty).
            string[] properties =
            {
                "TypeID", "DisplayNameRaw", "DisplayName", "DescriptionRaw", "Quality",
                "DisplayQuality", "Value", "MaxStackCount", "Tags"
            };
            builder.Append(",\n      \"item\": {");
            var firstProperty = true;
            foreach (var name in properties)
            {
                object value = null;
                try
                {
                    value = itemType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(item);
                }
                catch
                {
                    // property getters may throw before init
                }

                if (value == null)
                    continue;
                if (!firstProperty)
                    builder.Append(',');
                firstProperty = false;
                builder.Append("\n        \"").Append(name).Append("\": ").Append(DescribeValue(value, 1));
            }

            try
            {
                var constants = itemType.GetProperty("Constants", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item);
                var getString = constants?.GetType().GetMethod("GetString", new[] { typeof(string), typeof(string) });
                var caliber = getString?.Invoke(constants, new object[] { "Caliber", string.Empty }) as string;
                if (!string.IsNullOrEmpty(caliber))
                {
                    if (!firstProperty)
                        builder.Append(',');
                    firstProperty = false;
                    builder.Append("\n        \"Caliber\": \"").Append(JsonEscape(caliber)).Append('"');
                }
            }
            catch
            {
                // constants are best effort
            }

            builder.Append("\n      }");

            if (meta != null)
            {
                var metaType = meta.GetType();
                builder.Append(",\n      \"meta\": {");
                var first = true;
                foreach (var field in metaType.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (field.FieldType != typeof(int) && field.FieldType != typeof(float) &&
                        field.FieldType != typeof(string) && field.FieldType != typeof(bool))
                        continue;
                    if (!first)
                        builder.Append(',');
                    first = false;
                    builder.Append("\n        \"").Append(JsonEscape(field.Name)).Append("\": ")
                        .Append(DescribeValue(field.GetValue(meta), 1));
                }

                builder.Append("\n      }");
            }

            var statsProperty = itemType.GetProperty("Stats");
            var stats = statsProperty?.GetValue(item);
            if (stats is IEnumerable statList)
            {
                builder.Append(",\n      \"stats\": {");
                var first = true;
                foreach (var stat in statList)
                {
                    if (stat == null)
                        continue;
                    var statType = stat.GetType();
                    var key = statType.GetProperty("Key")?.GetValue(stat) as string;
                    if (string.IsNullOrEmpty(key))
                        continue;
                    var value = statType.GetProperty("Value")?.GetValue(stat);
                    var baseValue = statType.GetProperty("BaseValue")?.GetValue(stat);
                    if (!first)
                        builder.Append(',');
                    first = false;
                    builder.Append("\n        \"").Append(JsonEscape(key)).Append("\": ")
                        .Append(DescribeValue(value ?? baseValue, 1));
                }

                builder.Append("\n      }");
            }

            // Attachment slots: the native `Item.Slots` keys are exactly what a loadout
            // ticket's <mod slot="..."/> must use. Best effort, instances only.
            try
            {
                var slotsValue = itemType
                    .GetProperty("Slots", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(item);
                if (slotsValue is System.Collections.IEnumerable slotList)
                {
                    var slotsBuilder = new StringBuilder();
                    foreach (var slot in slotList)
                    {
                        if (slot == null)
                            continue;
                        var slotType = slot.GetType();
                        var key = slotType
                            .GetProperty("Key", BindingFlags.Instance | BindingFlags.Public)
                            ?.GetValue(slot) as string;
                        if (string.IsNullOrEmpty(key))
                            continue;
                        var content = slotType
                            .GetProperty("Content", BindingFlags.Instance | BindingFlags.Public)
                            ?.GetValue(slot) as UnityEngine.Object;
                        var contentId = content != null
                            ? content.GetType().GetProperty("TypeID")?.GetValue(content) as int? ?? 0
                            : 0;
                        if (slotsBuilder.Length > 0)
                            slotsBuilder.Append(", ");
                        slotsBuilder.Append("{\"key\": \"").Append(JsonEscape(key))
                            .Append("\", \"typeId\": ").Append(contentId).Append('}');
                    }
                    if (slotsBuilder.Length > 0)
                        builder.Append(",\n      \"slots\": [").Append(slotsBuilder).Append(']');
                }
            }
            catch
            {
                // slot enumeration is best effort
            }

            if (item is Component component)
            {
                var settingBase = FindTypeByName("ItemSettingBase");
                if (settingBase != null)
                {
                    var components = component.GetComponents(settingBase);
                    if (components != null && components.Length > 0)
                    {
                        builder.Append(",\n      \"settings\": [");
                        for (var i = 0; i < components.Length; i++)
                        {
                            if (i > 0)
                                builder.Append(',');
                            builder.Append('\n').Append("        ").Append(DumpObject("setting", components[i]));
                        }

                        builder.Append("\n      ]");
                    }
                }
            }

            // Effects carry the armor/perk stat modifiers; triggers/filters/actions
            // are nested serializable objects, so they need the recursive dumper.
            try
            {
                var effects = itemType.GetProperty("Effects")?.GetValue(item) as IEnumerable;
                if (effects != null)
                {
                    var effectEntries = new List<string>();
                    foreach (var effect in effects)
                    {
                        if (effect is UnityEngine.Object effectObject && effectObject != null)
                            effectEntries.Add(DumpEffectObject(effectObject));
                    }

                    if (effectEntries.Count > 0)
                    {
                        builder.Append(",\n      \"effects\": [");
                        for (var i = 0; i < effectEntries.Count; i++)
                        {
                            if (i > 0)
                                builder.Append(',');
                            builder.Append('\n').Append("        ").Append(effectEntries[i]);
                        }

                        builder.Append("\n      ]");
                    }
                }
            }
            catch
            {
                // effects are best effort
            }

            // Item modifiers (equipment grants BodyArmor/HeadArmor as stat modifiers).
            try
            {
                var modifiers = itemType.GetProperty("Modifiers")?.GetValue(item);
                if (modifiers is IEnumerable modifierList)
                {
                    var entries = new List<string>();
                    foreach (var modifier in modifierList)
                    {
                        if (modifier != null)
                            entries.Add(DumpPlainValue(modifier, 1));
                    }

                    if (entries.Count > 0)
                    {
                        builder.Append(",\n      \"modifiers\": [");
                        for (var i = 0; i < entries.Count; i++)
                        {
                            if (i > 0)
                                builder.Append(',');
                            builder.Append('\n').Append("        ").Append(entries[i]);
                        }

                        builder.Append("\n      ]");
                    }
                }
            }
            catch
            {
                // modifiers are best effort
            }

            // Constants/Variables are CustomDataCollection (ICollection<CustomData>):
            // bullet gains, caliber, durability etc. live there, not in Stats.
            foreach (var collectionName in new[] { "Constants", "Variables" })
            {
                IEnumerable collection = null;
                try
                {
                    collection = itemType.GetProperty(collectionName, BindingFlags.Instance | BindingFlags.Public)
                        ?.GetValue(item) as IEnumerable;
                }
                catch
                {
                    // collection unavailable
                }

                if (collection == null)
                    continue;

                builder.Append(",\n      \"").Append(collectionName).Append("\": {");
                var firstEntry = true;
                foreach (var entry in collection)
                {
                    if (entry == null)
                        continue;

                    var entryType = entry.GetType();
                    var key = entryType.GetProperty("Key")?.GetValue(entry) as string;
                    var dataType = entryType.GetProperty("DataType")?.GetValue(entry)?.ToString();
                    object value = null;
                    try
                    {
                        switch (dataType)
                        {
                            case "Float":
                                value = entryType.GetMethod("GetFloat")?.Invoke(entry, null);
                                break;
                            case "Int":
                                value = entryType.GetMethod("GetInt")?.Invoke(entry, null);
                                break;
                            case "Bool":
                                value = entryType.GetMethod("GetBool")?.Invoke(entry, null);
                                break;
                            case "String":
                                value = entryType.GetMethod("GetString")?.Invoke(entry, null);
                                break;
                        }
                    }
                    catch
                    {
                        // ignore a single malformed entry
                    }

                    if (string.IsNullOrEmpty(key) || value == null)
                        continue;
                    if (!firstEntry)
                        builder.Append(',');
                    firstEntry = false;
                    builder.Append("\n        \"").Append(JsonEscape(key)).Append("\": ")
                        .Append(DescribeValue(value, 1));
                }

                builder.Append("\n      }");
            }

            builder.Append("\n    }");
            return builder.ToString();
        }
        private static string DumpPlayerStats(object item)
        {
            var keys = new HashSet<string>
            {
                "GunDamageMultiplier", "GunCritRateGain", "GunCritDamageGain", "GunShootSpeedMultiplier",
                "GunDistanceMultiplier", "BulletSpeedMultiplier", "MeleeDamageMultiplier",
                "MeleeCritRateGain", "MeleeCritDamageGain", "MaxHealth", "MaxEnergy",
                "ElementFactor_Physics", "ElementFactor_Fire", "ElementFactor_Poison",
                "ElementFactor_Electricity", "ElementFactor_Space", "ElementFactor_Ghost",
                "ElementFactor_Ice"
            };

            var builder = new StringBuilder("{");
            var first = true;
            try
            {
                var stats = item.GetType().GetProperty("Stats")?.GetValue(item) as IEnumerable;
                if (stats != null)
                {
                    foreach (var stat in stats)
                    {
                        if (stat == null)
                            continue;
                        var statType = stat.GetType();
                        var key = statType.GetProperty("Key")?.GetValue(stat) as string;
                        if (string.IsNullOrEmpty(key) || !keys.Contains(key))
                            continue;
                        var value = statType.GetProperty("Value")?.GetValue(stat);
                        if (!first)
                            builder.Append(',');
                        first = false;
                        builder.Append('"').Append(JsonEscape(key)).Append("\":").Append(DescribeValue(value, 1));
                    }
                }
            }
            catch
            {
                // stats may be unavailable
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string DumpStatModifiers(object item)
        {
            var builder = new StringBuilder("{");
            try
            {
                var stats = item.GetType().GetProperty("Stats")?.GetValue(item) as IEnumerable;
                if (stats != null)
                {
                    var firstStat = true;
                    foreach (var stat in stats)
                    {
                        if (stat == null)
                            continue;
                        var statType = stat.GetType();
                        var key = statType.GetProperty("Key")?.GetValue(stat) as string;
                        var modifiers = statType.GetProperty("Modifiers")?.GetValue(stat) as IEnumerable;
                        if (string.IsNullOrEmpty(key) || modifiers == null)
                            continue;

                        var entries = new List<string>();
                        foreach (var modifier in modifiers)
                        {
                            if (modifier == null)
                                continue;
                            var modifierType = modifier.GetType();
                            var type = modifierType.GetProperty("Type")?.GetValue(modifier)?.ToString();
                            var value = modifierType.GetProperty("Value")?.GetValue(modifier);
                            var source = modifierType.GetProperty("Source")?.GetValue(modifier);
                            var sourceName = string.Empty;
                            var sourceType = string.Empty;
                            if (source is UnityEngine.Object sourceObject)
                            {
                                sourceName = sourceObject != null ? sourceObject.name : string.Empty;
                                sourceType = sourceObject != null ? sourceObject.GetType().Name : string.Empty;
                            }
                            else if (source != null)
                            {
                                sourceType = source.GetType().Name;
                                sourceName = source.ToString();
                            }

                            entries.Add("{\"type\":\"" + JsonEscape(type ?? string.Empty) +
                                        "\",\"value\":" + DescribeValue(value, 1) +
                                        ",\"source\":\"" + JsonEscape(sourceName) +
                                        "\",\"sourceType\":\"" + JsonEscape(sourceType) + "\"}");
                        }

                        if (entries.Count == 0)
                            continue;
                        if (!firstStat)
                            builder.Append(',');
                        firstStat = false;
                        builder.Append('"').Append(JsonEscape(key)).Append("\":[")
                            .Append(string.Join(",", entries)).Append(']');
                    }
                }
            }
            catch
            {
                // modifiers are diagnostic only
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string DumpEffectObject(UnityEngine.Object effect)
        {
            var builder = new StringBuilder();
            builder.Append("{\"_type\": \"").Append(JsonEscape(effect.GetType().FullName)).Append("\", \"fields\": {");
            var fields = new List<FieldInfo>();
            for (var type = effect.GetType(); type != null && type != typeof(object); type = type.BaseType)
                fields.AddRange(type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                               BindingFlags.NonPublic | BindingFlags.DeclaredOnly));

            var first = true;
            foreach (var field in fields)
            {
                object fieldValue;
                try
                {
                    fieldValue = field.GetValue(effect);
                }
                catch
                {
                    continue;
                }

                if (fieldValue == null)
                    continue;
                if (!first)
                    builder.Append(',');
                first = false;
                builder.Append('"').Append(JsonEscape(field.Name)).Append("\": ")
                    .Append(DumpPlainValue(fieldValue, 1));
            }

            builder.Append("}}");
            return builder.ToString();
        }

        /// <summary>Recursive JSON for serializable (non-Unity) objects and their lists.</summary>
        private static string DumpPlainValue(object value, int depth)
        {
            if (value == null)
                return "null";
            if (depth > 3)
                return "\"<deep>\"";

            var type = value.GetType();
            if (value is UnityEngine.Object unityObject)
                return "\"" + JsonEscape(unityObject != null ? unityObject.name : "<null>") + "\"";
            if (type.IsPrimitive || type.IsEnum || value is string)
                return DescribeValue(value, depth);
            if (value is IEnumerable list && !(value is string))
            {
                var builder = new StringBuilder("[");
                var first = true;
                var count = 0;
                foreach (var entry in list)
                {
                    if (count++ >= 16)
                    {
                        builder.Append(",\"<truncated>\"");
                        break;
                    }

                    if (!first)
                        builder.Append(',');
                    first = false;
                    builder.Append(DumpPlainValue(entry, depth + 1));
                }

                builder.Append(']');
                return builder.ToString();
            }

            var objectBuilder = new StringBuilder("{\"_type\":\"")
                .Append(JsonEscape(type.Name)).Append("\",\"fields\":{");
            var fields = new List<FieldInfo>();
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                fields.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                  BindingFlags.NonPublic | BindingFlags.DeclaredOnly));

            var firstField = true;
            foreach (var field in fields)
            {
                object fieldValue;
                try
                {
                    fieldValue = field.GetValue(value);
                }
                catch
                {
                    continue;
                }

                if (fieldValue == null)
                    continue;
                if (!firstField)
                    objectBuilder.Append(',');
                firstField = false;
                objectBuilder.Append('"').Append(JsonEscape(field.Name)).Append("\":")
                    .Append(DumpPlainValue(fieldValue, depth + 1));
            }

            objectBuilder.Append("}}");
            return objectBuilder.ToString();
        }
    }
}
