using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DuckovCoop.Client
{
    /// <summary>
    /// Client-side monster registry entry; this is the subset of monsters.xml that the
    /// game needs to resolve SAiSpawn.monsterId to a model/animator/team.
    /// </summary>
    public sealed class MonsterCatalogEntry
    {
        public uint MonsterId;
        public string AssetName = string.Empty;
        public string CharacterModelKey = string.Empty;
        public string AnimatorControllerKey = string.Empty;
        public string Team = string.Empty;
        public int MaxHp;
        public double Scale = 1.0;
        public uint NativeId;
    }

    /// <summary>
    /// CORE's canonical monster-catalog hash (ADR-020 option B). Keep this byte-for-byte
    /// identical to server/src/content/ContentStore.cpp::CanonicalMonsterCatalog:
    ///
    ///   monster-catalog-v1\n
    ///   id \t assetName \t characterModelKey \t animatorControllerKey \t team \t maxHp \t scaleMilli \t nativeId \n
    ///
    /// scaleMilli = round(scale * 1000), with away-from-zero rounding. Entries are sorted
    /// by monsterId; this hash is carried by CLogin.contentHash and deliberately ignores
    /// items/scenes/spawn tables.
    /// </summary>
    public static class MonsterCatalogHash
    {
        public static byte[] Compute(IEnumerable<MonsterCatalogEntry> entries)
        {
            var builder = new StringBuilder("monster-catalog-v1\n");
            foreach (var entry in (entries ?? Enumerable.Empty<MonsterCatalogEntry>())
                         .OrderBy(e => e.MonsterId))
            {
                var scaleMilli = (long)Math.Round(entry.Scale * 1000.0, MidpointRounding.AwayFromZero);
                builder.Append(entry.MonsterId).Append('\t')
                    .Append(entry.AssetName ?? string.Empty).Append('\t')
                    .Append(entry.CharacterModelKey ?? string.Empty).Append('\t')
                    .Append(entry.AnimatorControllerKey ?? string.Empty).Append('\t')
                    .Append(entry.Team ?? string.Empty).Append('\t')
                    .Append(entry.MaxHp).Append('\t')
                    .Append(scaleMilli).Append('\t')
                    .Append(entry.NativeId).Append('\n');
            }

            using (var sha = SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        }

        public static string ComputeHex(IEnumerable<MonsterCatalogEntry> entries)
        {
            var bytes = Compute(entries);
            var text = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
                text.Append(value.ToString("x2"));
            return text.ToString();
        }
    }
}
