using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using DuckovCoop.Client;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// Bundled monster catalog (CORE-generated monsters.catalog.json). The catalog hash
    /// is the CLogin contentHash; it deliberately covers only the monster registry.
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private Dictionary<uint, MonsterCatalogEntry> _monsterCatalog;
        private byte[] _monsterCatalogHash = new byte[32];

        internal MonsterCatalogEntry FindMonster(uint monsterId)
        {
            if (_monsterCatalog == null)
                return null;
            _monsterCatalog.TryGetValue(monsterId, out var entry);
            return entry;
        }

        private void LoadMonsterCatalog()
        {
            try
            {
                var directory = Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location) ?? string.Empty;
                var catalogPath = Path.Combine(directory, "monsters.catalog.json");
                if (!File.Exists(catalogPath))
                {
                    Log($"[catalog] missing {catalogPath}");
                    return;
                }

                var text = File.ReadAllText(catalogPath);
                var entries = new List<MonsterCatalogEntry>();
                foreach (Match match in Regex.Matches(text, @"\{[^{}]*\}", RegexOptions.Singleline))
                {
                    var body = match.Value;
                    if (body.IndexOf("\"monsterId\"", StringComparison.Ordinal) < 0)
                        continue;

                    var monsterId = ParseUInt(ExtractNumber(body, "monsterId"));
                    if (monsterId == 0)
                        continue;

                    entries.Add(new MonsterCatalogEntry
                    {
                        MonsterId = monsterId,
                        AssetName = ExtractString(body, "assetName"),
                        CharacterModelKey = ExtractString(body, "characterModelKey"),
                        AnimatorControllerKey = ExtractString(body, "animatorControllerKey"),
                        Team = ExtractString(body, "team"),
                        MaxHp = (int)ParseUInt(ExtractNumber(body, "maxHp")),
                        Scale = ParseDouble(ExtractNumber(body, "scale")),
                        NativeId = ParseUInt(ExtractNumber(body, "nativeId")),
                    });
                }

                _monsterCatalog = new Dictionary<uint, MonsterCatalogEntry>(entries.Count);
                foreach (var entry in entries)
                    _monsterCatalog[entry.MonsterId] = entry;

                var computed = MonsterCatalogHash.Compute(entries);
                _monsterCatalogHash = computed;
                var computedHex = MonsterCatalogHash.ComputeHex(entries);

                var expectedPath = Path.Combine(directory, "monsters.catalog.hash");
                var expected = File.Exists(expectedPath)
                    ? File.ReadAllText(expectedPath).Trim().ToLowerInvariant()
                    : string.Empty;
                Log($"[catalog] loaded entries={entries.Count} hash={computedHex} expected={expected}");
                if (expected.Length > 0 && !string.Equals(expected, computedHex, StringComparison.Ordinal))
                    Log("[catalog] WARNING hash mismatch between catalog and bundled .hash");
            }
            catch (Exception ex)
            {
                Log($"[catalog] load failed: {ex.GetType().Name}: {ex.Message}");
                _monsterCatalog = null;
            }
        }

        private static string ExtractString(string json, string name)
        {
            var match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"([^\"]*)\"",
                RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string ExtractNumber(string json, string name)
        {
            var match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)",
                RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value : "0";
        }

        private static uint ParseUInt(string value)
        {
            return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : 0;
        }

        private static double ParseDouble(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
                ? result
                : 1.0;
        }
    }
}
