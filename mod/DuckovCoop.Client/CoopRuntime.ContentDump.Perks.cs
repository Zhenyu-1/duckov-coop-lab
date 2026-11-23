using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// Extra content dumps for the attribute-system design: all rulesets (survival vs
    /// challenge values) and the full perk tree (max-level base-stat aggregate).
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private bool _perksDumpStarted;
        private bool _rulesetsDumpStarted;

        private void TryStartPerkDump()
        {
            if (_perksDumpStarted)
                return;
            _perksDumpStarted = true;
            StartCoroutine(DumpPerksCoroutine());
        }

        private void TryStartRulesetsDump()
        {
            if (_rulesetsDumpStarted)
                return;
            _rulesetsDumpStarted = true;
            StartCoroutine(DumpRulesetsCoroutine());
        }

        private IEnumerator DumpRulesetsCoroutine()
        {
            const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.Static;
            const BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var managerType = FindTypeByName("GameRulesManager") ??
                              FindTypeByFullName("Duckov.Rules.GameRulesManager");
            var waited = 0f;
            while (waited < 300f)
            {
                if (managerType != null)
                {
                    var entries = new List<string>();
                    try
                    {
                        foreach (var field in managerType.GetFields(staticFlags))
                            CollectRulesets(entries, field.Name, field.GetValue(null));
                        foreach (var property in managerType.GetProperties(staticFlags))
                        {
                            if (property.GetIndexParameters().Length > 0)
                                continue;
                            try
                            {
                                CollectRulesets(entries, property.Name, property.GetValue(null));
                            }
                            catch
                            {
                                // property getters may throw
                            }
                        }

                        var manager = managerType.GetProperty("Instance", staticFlags)?.GetValue(null);
                        if (manager != null)
                        {
                            var modes = managerType.GetField("entries", instanceFlags)?.GetValue(manager) as IEnumerable;
                            if (modes != null)
                            {
                                foreach (var mode in modes)
                                {
                                    if (mode == null)
                                        continue;
                                    var modeType = mode.GetType();
                                    var index = modeType.GetField("index")?.GetValue(mode);
                                    var file = modeType.GetField("file")?.GetValue(mode);
                                    var data = file?.GetType().GetProperty("Data")?.GetValue(file);
                                    if (data != null)
                                        entries.Add(DumpObjectEntry("mode:" + index, data));
                                }
                            }

                            var custom = managerType.GetField("customRuleSet", instanceFlags)?.GetValue(manager);
                            if (custom != null)
                                entries.Add(DumpObjectEntry("custom", custom));
                        }

                        var current = managerType.GetProperty("Current", staticFlags)?.GetValue(null);
                        if (current != null)
                            entries.Add(DumpObjectEntry("current", current));
                    }
                    catch (Exception ex)
                    {
                        Log("[dump] rulesets failed: " + ex.GetType().Name + ": " + ex.Message);
                    }

                    if (entries.Count > 0)
                    {
                        var path = Path.Combine(Application.persistentDataPath, "coop_rulesets_dump.json");
                        var builder = new StringBuilder(1024 * 32);
                        builder.Append("{\n  \"built\": \"")
                            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                            .Append("\",\n  \"rulesets\": [");
                        for (var i = 0; i < entries.Count; i++)
                        {
                            if (i > 0)
                                builder.Append(',');
                            builder.Append('\n').Append("    ").Append(entries[i]);
                        }

                        builder.Append("\n  ]\n}\n");
                        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
                        Log($"[dump] rulesets written: {path} count={entries.Count}");
                        yield break;
                    }
                }

                yield return new WaitForSeconds(10f);
                waited += 10f;
            }

            Log("[dump] rulesets gave up (GameRulesManager not ready)");
        }

        private static void CollectRulesets(List<string> entries, string source, object value)
        {
            if (value == null || value is string)
                return;
            if (value is IEnumerable list)
            {
                foreach (var element in list)
                {
                    if (element == null)
                        continue;
                    if (!element.GetType().Name.Contains("Ruleset") &&
                        !element.GetType().Name.Contains("Rule"))
                        continue;
                    entries.Add(DumpObjectEntry(source, element));
                }

                return;
            }

            if (value.GetType().Name.Contains("Ruleset") || value.GetType().Name.Contains("Rule"))
                entries.Add(DumpObjectEntry(source, value));
        }

        private static string DumpObjectEntry(string source, object value)
        {
            return "{\"_source\": \"" + JsonEscape(source) + "\", \"value\": " + DumpPlainValue(value, 0) + "}";
        }

        private IEnumerator DumpPerksCoroutine()
        {
            const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.Static;
            const BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var managerType = FindTypeByName("PerkTreeManager");
            var waited = 0f;
            while (waited < 300f)
            {
                object manager = null;
                try
                {
                    manager = managerType?.GetProperty("Instance", staticFlags)?.GetValue(null);
                }
                catch
                {
                    // manager not ready
                }

                if (manager != null)
                {
                    var treeList = managerType.GetField("perkTrees", instanceFlags)?.GetValue(manager) as IEnumerable;
                    if (treeList != null)
                    {
                        var trees = new List<string>();
                        var aggregate = new Dictionary<string, float[]>(StringComparer.Ordinal);
                        foreach (var tree in treeList)
                        {
                            if (tree == null)
                                continue;
                            trees.Add(DumpPerkTree(tree, aggregate));
                        }

                        WritePerkDump(trees, aggregate);
                        yield break;
                    }
                }

                yield return new WaitForSeconds(10f);
                waited += 10f;
            }

            Log("[dump] perks gave up (PerkTreeManager not ready)");
        }

        private string DumpPerkTree(object tree, Dictionary<string, float[]> aggregate)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var treeType = tree.GetType();
            var id = treeType.GetProperty("ID", flags)?.GetValue(tree) as string ?? string.Empty;
            var display = treeType.GetProperty("DisplayName", flags)?.GetValue(tree) as string ?? string.Empty;
            var enabled = treeType.GetProperty("EnabledInCurrentLevel", flags)?.GetValue(tree);
            var perks = treeType.GetProperty("Perks", flags)?.GetValue(tree) as IEnumerable;

            var perkEntries = new List<string>();
            if (perks != null)
            {
                foreach (var perk in perks)
                {
                    if (perk == null)
                        continue;
                    perkEntries.Add(DumpPerk(perk, aggregate));
                }
            }

            var builder = new StringBuilder();
            builder.Append("{\"tree\": \"").Append(JsonEscape(id)).Append("\", \"display\": \"")
                .Append(JsonEscape(display)).Append("\", \"enabled\": ")
                .Append(enabled is bool value && value ? "true" : "false").Append(", \"perks\": [");
            for (var i = 0; i < perkEntries.Count; i++)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append('\n').Append("      ").Append(perkEntries[i]);
            }

            builder.Append("\n    ]}");
            return builder.ToString();
        }

        private string DumpPerk(object perk, Dictionary<string, float[]> aggregate)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var perkType = perk.GetType();
            var name = ((UnityEngine.Object)perk).name;
            var display = perkType.GetProperty("DisplayNameRaw", flags)?.GetValue(perk) as string ?? string.Empty;
            var unlocked = perkType.GetProperty("Unlocked", flags)?.GetValue(perk);
            var defaultUnlocked = perkType.GetProperty("DefaultUnlocked", flags)?.GetValue(perk);
            var enabled = perkType.GetProperty("EnabledInCurrentLevel", flags)?.GetValue(perk);

            var behaviours = new List<string>();
            var behaviourBase = FindTypeByName("PerkBehaviour");
            if (behaviourBase != null && perk is Component component)
            {
                var components = component.GetComponents(behaviourBase);
                foreach (var behaviour in components)
                {
                    var behaviourType = behaviour.GetType();
                    var entry = "{\"_type\": \"" + JsonEscape(behaviourType.Name) + "\"";
                    if (behaviourType.Name.Contains("ModifyCharacterStats"))
                    {
                        entry += ", \"entries\": " + DumpStatEntries(behaviour);
                    }
                    else if (behaviourType.Name.Contains("AddBuff"))
                    {
                        var prefab = behaviourType.GetField("prefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?.GetValue(behaviour);
                        if (prefab != null)
                        {
                            entry += ", \"buff\": " + DumpBuff(prefab);
                            AccumulateBuffStats(prefab, aggregate);
                        }
                    }

                    entry += "}";

                    behaviours.Add(entry);
                    AccumulatePerkStats(behaviour, aggregate);
                }
            }

            var builder = new StringBuilder();
            builder.Append("{\"name\": \"").Append(JsonEscape(name)).Append("\", \"display\": \"")
                .Append(JsonEscape(display)).Append("\", \"unlocked\": ")
                .Append(unlocked is bool u && u ? "true" : "false").Append(", \"defaultUnlocked\": ")
                .Append(defaultUnlocked is bool d && d ? "true" : "false").Append(", \"enabledInLevel\": ")
                .Append(enabled is bool e && e ? "true" : "false").Append(", \"behaviours\": [");
            for (var i = 0; i < behaviours.Count; i++)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append('\n').Append("        ").Append(behaviours[i]);
            }

            builder.Append("\n      ]}");
            return builder.ToString();
        }

        /// <summary>Sum ModifyCharacterStatsBase entries into an add / percentage aggregate.</summary>
        private static void AccumulatePerkStats(object behaviour, Dictionary<string, float[]> aggregate)
        {
            if (behaviour == null || !behaviour.GetType().Name.Contains("ModifyCharacterStats"))
                return;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var entries = behaviour.GetType().GetField("entries", flags)?.GetValue(behaviour) as IEnumerable;
            if (entries == null)
                return;

            foreach (var entry in entries)
            {
                if (entry == null)
                    continue;
                var entryType = entry.GetType();
                var key = entryType.GetField("key")?.GetValue(entry) as string;
                var rawValue = entryType.GetField("value")?.GetValue(entry);
                var percentage = entryType.GetField("percentage")?.GetValue(entry);
                if (string.IsNullOrEmpty(key) || !(rawValue is float value))
                    continue;
                if (!aggregate.TryGetValue(key, out var sums))
                {
                    sums = new float[2];
                    aggregate[key] = sums;
                }

                if (percentage is bool p && p)
                    sums[1] += value;
                else
                    sums[0] += value;
            }
        }

        /// <summary>Dump ModifyCharacterStatsBase.entries (key/value/percentage).</summary>
        private static string DumpStatEntries(object behaviour)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var entries = behaviour.GetType().GetField("entries", flags)?.GetValue(behaviour) as IEnumerable;
            var builder = new StringBuilder("[");
            var first = true;
            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (entry == null)
                        continue;
                    var entryType = entry.GetType();
                    var key = entryType.GetField("key")?.GetValue(entry) as string;
                    var value = entryType.GetField("value")?.GetValue(entry);
                    var percentage = entryType.GetField("percentage")?.GetValue(entry);
                    if (string.IsNullOrEmpty(key))
                        continue;
                    if (!first)
                        builder.Append(',');
                    first = false;
                    builder.Append("{\"key\": \"").Append(JsonEscape(key)).Append("\", \"value\": ")
                        .Append(value is float f ? f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "0")
                        .Append(", \"percentage\": ").Append(percentage is bool p && p ? "true" : "false").Append('}');
                }
            }

            builder.Append(']');
            return builder.ToString();
        }
        /// <summary>Dump a buff prefab: id/lifetime/layers and every effect action.</summary>
        private static string DumpBuff(object buff)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var buffType = buff.GetType();
            var id = buffType.GetProperty("ID", flags)?.GetValue(buff);
            var limited = buffType.GetProperty("LimitedLifeTime", flags)?.GetValue(buff);
            var total = buffType.GetProperty("TotalLifeTime", flags)?.GetValue(buff);
            var maxLayers = buffType.GetProperty("MaxLayers", flags)?.GetValue(buff);
            var effects = buffType.GetField("effects", flags)?.GetValue(buff) as IEnumerable;
            var actions = new List<string>();
            if (effects != null)
            {
                foreach (var effect in effects)
                {
                    if (effect == null)
                        continue;
                    var effectActions = effect.GetType().GetField("actions", flags)?.GetValue(effect) as IEnumerable;
                    if (effectActions == null)
                        continue;
                    foreach (var action in effectActions)
                    {
                        if (action == null)
                            continue;
                        actions.Add(DumpPlainValue(action, 2));
                    }
                }
            }

            var builder = new StringBuilder();
            builder.Append("{\"name\": \"").Append(JsonEscape(((UnityEngine.Object)buff).name)).Append("\", \"id\": ")
                .Append(id is int i ? i : 0).Append(", \"limited\": ")
                .Append(limited is bool l && l ? "true" : "false").Append(", \"totalLifeTime\": ")
                .Append(total is float f ? f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "0")
                .Append(", \"maxLayers\": ").Append(maxLayers is int m ? m : 1).Append(", \"actions\": [");
            for (var index = 0; index < actions.Count; index++)
            {
                if (index > 0)
                    builder.Append(',');
                builder.Append(actions[index]);
            }

            builder.Append("]}");
            return builder.ToString();
        }

        /// <summary>Fold permanent AddBuff perk modifiers into the base aggregate.</summary>
        private static void AccumulateBuffStats(object buff, Dictionary<string, float[]> aggregate)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var buffType = buff.GetType();
            var limited = buffType.GetProperty("LimitedLifeTime", flags)?.GetValue(buff);
            if (limited is bool l && l)
                return; // temporary buffs are not part of the permanent base

            var effects = buffType.GetField("effects", flags)?.GetValue(buff) as IEnumerable;
            if (effects == null)
                return;
            foreach (var effect in effects)
            {
                if (effect == null)
                    continue;
                var effectActions = effect.GetType().GetField("actions", flags)?.GetValue(effect) as IEnumerable;
                if (effectActions == null)
                    continue;
                foreach (var action in effectActions)
                {
                    if (action == null || !action.GetType().Name.Contains("ModifierAction"))
                        continue;
                    var actionType = action.GetType();
                    var key = actionType.GetField("targetStatKey", flags)?.GetValue(action) as string;
                    var typeValue = actionType.GetField("ModifierType", flags)?.GetValue(action);
                    var valueRaw = actionType.GetField("modifierValue", flags)?.GetValue(action);
                    if (string.IsNullOrEmpty(key) || !(valueRaw is float value))
                        continue;
                    if (!aggregate.TryGetValue(key, out var sums))
                    {
                        sums = new float[2];
                        aggregate[key] = sums;
                    }

                    var typeName = typeValue?.ToString() ?? "Add";
                    if (typeName == "PercentageAdd" || typeName == "PercentageMultiply")
                        sums[1] += value;
                    else
                        sums[0] += value;
                }
            }
        }
        private void WritePerkDump(List<string> trees, Dictionary<string, float[]> aggregate)
        {
            var builder = new StringBuilder(1024 * 64);
            builder.Append("{\n  \"built\": \"")
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\",\n  \"trees\": [");
            for (var i = 0; i < trees.Count; i++)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append("\n    ").Append(trees[i]);
            }

            builder.Append("\n  ],\n  \"aggregate\": {");
            var first = true;
            foreach (var pair in aggregate)
            {
                if (!first)
                    builder.Append(',');
                first = false;
                builder.Append("\n    \"").Append(JsonEscape(pair.Key)).Append("\": {\"add\": ")
                    .Append(pair.Value[0].ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(", \"percentageAdd\": ")
                    .Append(pair.Value[1].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
            }

            builder.Append("\n  }\n}\n");
            var path = Path.Combine(Application.persistentDataPath, "coop_perks_dump.json");
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
            Log($"[dump] perks written: {path} trees={trees.Count} keys={aggregate.Count}");
        }

        /// <summary>Dump every slot on the character item (incl. the totem slot).</summary>
        private static string DumpAllSlots(object characterItem)
        {
            var builder = new StringBuilder("{");
            try
            {
                var slots = characterItem.GetType().GetProperty("Slots")?.GetValue(characterItem) as IEnumerable;
                if (slots != null)
                {
                    var first = true;
                    foreach (var slot in slots)
                    {
                        if (slot == null)
                            continue;
                        var slotType = slot.GetType();
                        var key = slotType.GetProperty("Key")?.GetValue(slot) as string;
                        var content = slotType.GetProperty("Content")?.GetValue(slot);
                        if (!first)
                            builder.Append(',');
                        first = false;
                        builder.Append("\n    \"").Append(JsonEscape(key ?? string.Empty)).Append("\": ");
                        if (content is UnityEngine.Object contentObject && contentObject != null)
                        {
                            var typeId = content.GetType().GetProperty("TypeID")?.GetValue(content);
                            var display = content.GetType().GetProperty("DisplayNameRaw")?.GetValue(content);
                            builder.Append("{\"typeId\": ").Append(typeId is int id ? id : 0)
                                .Append(", \"name\": \"").Append(JsonEscape(contentObject.name))
                                .Append("\", \"display\": \"").Append(JsonEscape(display as string ?? string.Empty))
                                .Append("\"}");
                        }
                        else
                        {
                            builder.Append("null");
                        }
                    }
                }
            }
            catch
            {
                // slots are diagnostic only
            }

            builder.Append("\n  }");
            return builder.ToString();
        }
    }
}
