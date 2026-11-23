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
    /// Content dump for CORE tables (D5): native monster presets (full combat fields),
    /// native combat buffs and loaded item settings. Enabled by a `content_dump.on`
    /// sentinel next to the mod DLL; writes one JSON next to the game log.
    /// No wire, no gameplay change.
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private const string ContentDumpSentinel = "content_dump.on";
        private bool _contentDumpDone;
        private bool _contentDumpSentinelLogged;
        private float _nextContentDumpCheck;

        private void UpdateContentDump()
        {
            if (_contentDumpDone || Time.unscaledTime < 3f || Time.unscaledTime < _nextContentDumpCheck)
                return;
            _nextContentDumpCheck = Time.unscaledTime + 1f;

            try
            {
                var candidates = new List<string>();
                var assemblyDir = Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location);
                if (!string.IsNullOrEmpty(assemblyDir))
                    candidates.Add(assemblyDir);
                candidates.Add(Application.persistentDataPath);
                try
                {
                    candidates.Add(Directory.GetCurrentDirectory());
                }
                catch
                {
                    // cwd unavailable
                }

                var sentinel = string.Empty;
                foreach (var candidate in candidates)
                {
                    var sentinelPath = Path.Combine(candidate, ContentDumpSentinel);
                    if (File.Exists(sentinelPath))
                    {
                        sentinel = sentinelPath;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(sentinel))
                {
                    if (!_contentDumpSentinelLogged)
                    {
                        _contentDumpSentinelLogged = true;
                        Log($"[dump] no {ContentDumpSentinel} in: {string.Join(" | ", candidates)}");
                    }

                    return;
                }

                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static;
                var settingsType = FindTypeByName("GameplayDataSettings") ??
                                   FindTypeByFullName("Duckov.Utilities.GameplayDataSettings");
                var settings = settingsType?.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                if (settings == null && settingsType != null)
                {
                    var found = Resources.FindObjectsOfTypeAll(settingsType);
                    if (found != null && found.Length > 0)
                        settings = found[0];
                }

                if (settings == null || settingsType == null)
                {
                    Log($"[dump] waiting for GameplayDataSettings (type={(settingsType != null)}) sentinel={sentinel}");
                    return;
                }

                var builder = new StringBuilder(1024 * 256);
                builder.Append("{\n  \"meta\": {\n");
                builder.Append("    \"built\": \"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\",\n");
                builder.Append("    \"scene\": \"").Append(JsonEscape(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)).Append("\"\n");
                builder.Append("  },\n");

                var presets = DumpPresets(settings, settingsType, any);
                builder.Append("  \"presets\": [");
                AppendEntries(builder, presets);
                builder.Append("],\n");

                var buffs = DumpBuffs(settings, settingsType, any);
                builder.Append("  \"buffs\": [");
                AppendEntries(builder, buffs);
                builder.Append("],\n");

                builder.Append("  \"ruleset\": ");
                var rule = FindTypeByName("LevelManager")?
                    .GetProperty("Rule", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                builder.Append(rule is UnityEngine.Object ruleObject ? DumpObject("ruleset", ruleObject) : "null");
                builder.Append(",\n");

                var itemSettings = DumpLoadedItemSettings();
                builder.Append("  \"itemSettings\": {");
                var first = true;
                foreach (var pair in itemSettings)
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    builder.Append("\n    \"").Append(pair.Key).Append("\": [");
                    AppendEntries(builder, pair.Value);
                    builder.Append(']');
                }

                builder.Append("\n  }\n}\n");

                var path = Path.Combine(Application.persistentDataPath, "coop_content_dump.json");
                File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
                TryStartItemDump();
                TryStartRulesetDump();
                TryStartPlayerDump();
                TryStartPerkDump();
                TryStartRulesetsDump();

                var itemCount = 0;
                foreach (var pair in itemSettings)
                    itemCount += pair.Value.Count;

                Log($"[dump] content written: {path} presets={presets.Count} buffs={buffs.Count} items={itemCount}");
                if (itemCount == 0)
                {
                    // Item settings load with the stash/raid; keep the sentinel and retry.
                    _nextContentDumpCheck = Time.unscaledTime + 30f;
                    Log("[dump] item settings empty; retry in 30s");
                    return;
                }

                _contentDumpDone = true;
            }
            catch (Exception ex)
            {
                _contentDumpDone = true;
                Log("[dump] content failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Type FindTypeByFullName(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch
                {
                    // ignore broken assemblies
                }
            }

            return null;
        }

        private static List<string> DumpPresets(object settings, Type settingsType, BindingFlags any)
        {
            var result = new List<string>();
            try
            {
                var presetData = settingsType.GetProperty("CharacterRandomPresetData", any)?.GetValue(settings, null);
                var presets = presetData?.GetType().GetField("presets", any)?.GetValue(presetData) as IList;
                if (presets == null)
                    return result;

                for (var i = 0; i < presets.Count; i++)
                {
                    if (presets[i] is UnityEngine.Object preset)
                        result.Add(DumpObject("preset", preset));
                }
            }
            catch (Exception ex)
            {
                result.Add("{\"error\":\"" + JsonEscape(ex.GetType().Name) + "\"}");
            }

            return result;
        }

        private static List<string> DumpBuffs(object settings, Type settingsType, BindingFlags any)
        {
            var result = new List<string>();
            try
            {
                const BindingFlags fieldFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var buffsData = settingsType.GetProperty("Buffs", any)?.GetValue(settings, null);
                if (buffsData == null)
                    return result;

                var seen = new HashSet<int>();
                foreach (var field in buffsData.GetType().GetFields(fieldFlags))
                {
                    if (!typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                        continue;
                    if (!(field.GetValue(buffsData) is UnityEngine.Object buff) || buff == null)
                        continue;
                    if (!seen.Add(buff.GetInstanceID()))
                        continue;
                    result.Add(DumpObject("buff:" + field.Name, buff));
                }

                if (buffsData.GetType().GetField("allBuffs", fieldFlags)?.GetValue(buffsData) is IList all)
                {
                    for (var i = 0; i < all.Count; i++)
                    {
                        if (all[i] is UnityEngine.Object buff && buff != null && seen.Add(buff.GetInstanceID()))
                            result.Add(DumpObject("allBuffs", buff));
                    }
                }
            }
            catch (Exception ex)
            {
                result.Add("{\"error\":\"" + JsonEscape(ex.GetType().Name) + "\"}");
            }

            return result;
        }

        private static Dictionary<string, List<string>> DumpLoadedItemSettings()
        {
            var result = new Dictionary<string, List<string>>();
            foreach (var name in new[] { "ItemSetting_Gun", "ItemSetting_Bullet", "ItemSetting_MeleeWeapon" })
            {
                var list = new List<string>();
                try
                {
                    var type = FindTypeByName(name);
                    if (type != null)
                    {
                        var found = Resources.FindObjectsOfTypeAll(type);
                        for (var i = 0; i < found.Length && i < 200; i++)
                        {
                            if (found[i] is UnityEngine.Object obj)
                                list.Add(DumpObject(name, obj));
                        }
                    }
                }
                catch (Exception ex)
                {
                    list.Add("{\"error\":\"" + JsonEscape(ex.GetType().Name) + "\"}");
                }

                result[name] = list;
            }

            return result;
        }

        private static string DumpObject(string label, UnityEngine.Object obj)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var builder = new StringBuilder();
            builder.Append("{\n      \"_label\": \"").Append(JsonEscape(label)).Append("\",\n");
            builder.Append("      \"_name\": \"").Append(JsonEscape(obj.name)).Append("\",\n");
            builder.Append("      \"_type\": \"").Append(JsonEscape(obj.GetType().FullName)).Append("\",\n");
            builder.Append("      \"fields\": {");

            var fields = new List<FieldInfo>();
            for (var type = obj.GetType(); type != null && type != typeof(object); type = type.BaseType)
                fields.AddRange(type.GetFields(flags | BindingFlags.DeclaredOnly));

            var first = true;
            foreach (var field in fields)
            {
                object value;
                try
                {
                    value = field.GetValue(obj);
                }
                catch
                {
                    continue;
                }

                if (!first)
                    builder.Append(',');
                first = false;
                builder.Append("\n        \"").Append(JsonEscape(field.Name)).Append("\": ")
                    .Append(DescribeValue(value, 0));
            }

            builder.Append("\n      }\n    }");
            return builder.ToString();
        }

        private static string DescribeValue(object value, int depth)
        {
            if (value == null)
                return "null";
            if (depth > 2)
                return "\"<deep>\"";

            var type = value.GetType();
            if (value is string text)
                return "\"" + JsonEscape(text) + "\"";
            if (type.IsEnum)
                return "\"" + JsonEscape(value.ToString()) + "\"";
            if (value is bool flag)
                return flag ? "true" : "false";
            if (value is float single)
                return single.ToString("R", CultureInfo.InvariantCulture);
            if (value is double number)
                return number.ToString("R", CultureInfo.InvariantCulture);
            if (value is int || value is long || value is uint || value is ulong ||
                value is short || value is ushort || value is byte || value is sbyte)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (value is UnityEngine.Object unityObject)
                return "\"" + JsonEscape(unityObject != null ? unityObject.name : "<null>") + "\"";
            if (value is Vector2 v2)
                return "{\"x\":" + v2.x.ToString("R", CultureInfo.InvariantCulture) + ",\"y\":" + v2.y.ToString("R", CultureInfo.InvariantCulture) + "}";
            if (value is Vector3 v3)
                return "{\"x\":" + v3.x.ToString("R", CultureInfo.InvariantCulture) + ",\"y\":" + v3.y.ToString("R", CultureInfo.InvariantCulture) + ",\"z\":" + v3.z.ToString("R", CultureInfo.InvariantCulture) + "}";
            if (value is Vector2Int vi2)
                return "{\"x\":" + vi2.x + ",\"y\":" + vi2.y + "}";
            if (value is Color color)
                return "{\"r\":" + color.r.ToString("R", CultureInfo.InvariantCulture) + ",\"g\":" + color.g.ToString("R", CultureInfo.InvariantCulture) +
                       ",\"b\":" + color.b.ToString("R", CultureInfo.InvariantCulture) + ",\"a\":" + color.a.ToString("R", CultureInfo.InvariantCulture) + "}";
            if (value is IList list)
            {
                var builder = new StringBuilder("[");
                var count = Math.Min(list.Count, 32);
                for (var i = 0; i < count; i++)
                {
                    if (i > 0)
                        builder.Append(',');
                    builder.Append(DescribeValue(list[i], depth + 1));
                }

                if (list.Count > count)
                    builder.Append(",\"<truncated>\"");
                builder.Append(']');
                return builder.ToString();
            }

            return "\"" + JsonEscape(type.Name) + "\"";
        }

        private static void AppendEntries(StringBuilder builder, List<string> entries)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append('\n').Append("    ").Append(entries[i]);
            }
        }

        private static string JsonEscape(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            var builder = new StringBuilder(text.Length + 8);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(c);
                        break;
                }
            }

            return builder.ToString();
        }
    }
}
