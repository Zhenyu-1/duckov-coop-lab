using System;
using System.Collections.Generic;
using System.Text;
using ItemStatsSystem;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// Presentation-only icon resolution for items created by the T4 mirror. The server only
    /// sends itemId/count, so the icon must come from native assets. Bullet prefabs use the
    /// placeholder "cross" sprite instead of a real ammo icon; this resolver prefers the
    /// curated <see cref="ItemMetaData.icon"/>, then a sibling bullet icon of the same caliber,
    /// then an exact item-name prefix match. It never substitutes the fallback icon and logs
    /// the available candidates when nothing is usable.
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private const int MaxLoggedIconCandidates = 40;

        private bool _ammoIconScanDone;
        private bool _ammoIconCandidatesLogged;
        private readonly Dictionary<string, Sprite> _ammoIconByCaliber =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _ammoIconCandidates = new List<string>();

        private void ApplyInventoryItemIcon(Item item, uint uid, string iconCategory = null)
        {
            if (item == null)
                return;

            var prefab = ItemAssetsCollection.GetPrefab(item.TypeID);
            var prefabIcon = prefab != null ? prefab.Icon : null;
            var meta = ItemAssetsCollection.GetMetaData(item.TypeID);
            var current = item.Icon;
            var isBullet = IsBulletItem(item, meta);

            if (item.TypeID == 143)
            {
                Log($"[inventory] icon item={item.TypeID} uid={uid} " +
                    $"icon={(current != null ? current.name : "null")} " +
                    $"graphic={(item.ItemGraphic != null ? item.ItemGraphic.name : "null")} " +
                    $"prefab={(prefab != null ? prefab.name : "null")} " +
                    $"meta={(meta.icon != null ? meta.icon.name : "null")} " +
                    $"caliber={(string.IsNullOrEmpty(meta.caliber) ? "-" : meta.caliber)}");
            }

            if (IsUsableItemIcon(current))
                return;

            if (IsUsableItemIcon(meta.icon))
            {
                item.Icon = meta.icon;
                Log($"[inventory] icon resolved item={item.TypeID} uid={uid} " +
                    $"sprite={meta.icon.name} source=meta");
                return;
            }

            if (IsUsableItemIcon(prefabIcon))
            {
                item.Icon = prefabIcon;
                Log($"[inventory] icon restored from prefab item={item.TypeID} uid={uid} " +
                    $"sprite={prefabIcon.name}");
                return;
            }

            if (isBullet)
            {
                ScanAmmoIconCandidates();
                if (!string.IsNullOrEmpty(meta.caliber) &&
                    _ammoIconByCaliber.TryGetValue(meta.caliber, out var sibling))
                {
                    item.Icon = sibling;
                    Log($"[inventory] icon resolved item={item.TypeID} uid={uid} " +
                        $"sprite={sibling.name} source=caliber:{meta.caliber}");
                    return;
                }

                var scanned = FindAmmoIconByName(item);
                if (scanned != null)
                {
                    item.Icon = scanned;
                    Log($"[inventory] icon resolved item={item.TypeID} uid={uid} " +
                        $"sprite={scanned.name} source=scan");
                    return;
                }

                // Native has no per-item AR icon asset; the ammo UI uses one shared icon per
                // weapon family (ammo_machinegun/pistol/sniper/shotgun/rocket). Fall back to
                // that family icon instead of leaving the white "cross" placeholder.
                var familyIcon = FindAmmoFamilyIcon(item);
                if (familyIcon != null)
                {
                    item.Icon = familyIcon;
                    Log($"[inventory] icon resolved item={item.TypeID} uid={uid} " +
                        $"sprite={familyIcon.name} source=family");
                    return;
                }

                Log($"[inventory] ammo icon asset missing item={item.TypeID} uid={uid} " +
                    $"prefab={(prefab != null ? prefab.name : "null")} " +
                    $"meta/candidates={DescribeAmmoIconCandidates()}");
                return;
            }

            var templateIcon = FindNativeItemIcon(item.TypeID, item);
            if (IsUsableItemIcon(templateIcon))
            {
                item.Icon = templateIcon;
                Log($"[inventory] icon restored from native template item={item.TypeID} " +
                    $"uid={uid} sprite={templateIcon.name}");
                return;
            }

            // Attachments (and some other prefab-only items) carry their UI sprite as a loaded
            // asset named "Pfb_<prefabName>_<n>"; ItemMetaData/Item.Icon point at the "cross"
            // placeholder instead, so resolve by prefab name before giving up.
            var prefabNamedIcon = FindPrefabIconByName(item);
            if (prefabNamedIcon != null)
            {
                item.Icon = prefabNamedIcon;
                Log($"[inventory] icon resolved item={item.TypeID} uid={uid} " +
                    $"sprite={prefabNamedIcon.name} source=prefab-name");
                return;
            }

            var categoryIcon = FindCategoryIcon(iconCategory);
            if (categoryIcon != null)
            {
                item.Icon = categoryIcon;
                Log($"[inventory] icon resolved item={item.TypeID} uid={uid} " +
                    $"sprite={categoryIcon.name} source=category:{iconCategory}");
                return;
            }

            Log($"[inventory] icon asset missing item={item.TypeID} uid={uid} " +
                "(prefab/meta/native template all null; not faking FallbackItemIcon)");
        }

        private static readonly Dictionary<string, string> IconCategoryPrefixes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Scope", "Pfb_Scope_" },
                { "Mag", "Pfb_Magazine_" },
                { "Muzzle", "Pfb_Muzzle_" },
                { "Grip", "Pfb_Grip_" },
                { "Stock", "Pfb_Stock_" },
                { "Tec", "Pfb_Tec_" },
            };

        /// <summary>
        /// Last-resort attachment icon: any loaded sprite of the same category (a scope icon for
        /// a scope) beats the white "cross" placeholder.
        /// </summary>
        private static Sprite FindCategoryIcon(string category)
        {
            if (string.IsNullOrEmpty(category) ||
                !IconCategoryPrefixes.TryGetValue(category, out var prefix))
                return null;
            foreach (var pair in EnsureSpriteIndex())
            {
                if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    IsUsableItemIcon(pair.Value))
                    return pair.Value;
            }
            return null;
        }

        /// <summary>Loaded sprite index keyed by sprite name (built once).</summary>
        private static Dictionary<string, Sprite> _spriteIndex;

        private static Dictionary<string, Sprite> EnsureSpriteIndex()
        {
            if (_spriteIndex != null)
                return _spriteIndex;
            _spriteIndex = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sprite == null || string.IsNullOrEmpty(sprite.name))
                        continue;
                    if (!_spriteIndex.ContainsKey(sprite.name))
                        _spriteIndex[sprite.name] = sprite;
                }
            }
            catch
            {
                // sprite index is best effort
            }
            return _spriteIndex;
        }

        private static Sprite FindPrefabIconByName(Item item)
        {
            if (item == null)
                return null;
            string name = null;
            try
            {
                var prefab = ItemAssetsCollection.GetPrefab(item.TypeID);
                name = prefab != null ? prefab.name : null;
            }
            catch
            {
                // fall back to the instance name
            }
            if (string.IsNullOrEmpty(name))
                name = item.name;
            if (string.IsNullOrEmpty(name))
                return null;
            var clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone >= 0)
                name = name.Substring(0, clone);
            if (name.Length == 0)
                return null;

            var index = EnsureSpriteIndex();
            if (index.TryGetValue("Pfb_" + name + "_1", out var exact) &&
                IsUsableItemIcon(exact))
                return exact;

            // Atlas variants are numbered (_2, _3, ...); accept any Pfb_<name>* sprite.
            var prefix = "Pfb_" + name;
            foreach (var pair in index)
            {
                if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsUsableItemIcon(pair.Value))
                    return pair.Value;
            }
            return null;
        }

        private static bool IsBulletItem(Item item, ItemMetaData meta)
        {
            var itemName = item != null ? item.name : string.Empty;
            var metaName = meta.Name ?? string.Empty;
            if (itemName.StartsWith("Bullet_", StringComparison.OrdinalIgnoreCase) ||
                metaName.StartsWith("Bullet_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(meta.caliber))
                return true;
            try
            {
                return item != null && !string.IsNullOrEmpty(item.GetString("Caliber", string.Empty));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUsableItemIcon(Sprite sprite)
        {
            if (sprite == null)
                return false;
            // The native bullet prefabs reference a sprite literally named "cross", which is
            // also what GameplayDataSettings.UIStyle.FallbackItemIcon renders. Never use it.
            if (string.Equals(sprite.name, "cross", StringComparison.OrdinalIgnoreCase))
                return false;
            if (sprite.name.IndexOf("fallback", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return true;
        }

        /// <summary>
        /// One-time scan: bullet item metadata (curated UI icons) grouped by caliber, plus any
        /// loaded Sprite whose name looks like ammo. The list is logged once for diagnostics;
        /// only an exact item-name prefix match is used to replace a placeholder.
        /// </summary>
        private void ScanAmmoIconCandidates()
        {
            if (_ammoIconScanDone)
                return;
            _ammoIconScanDone = true;

            try
            {
                var ids = ItemAssetsCollection.GetAllTypeIds(default(ItemFilter));
                if (ids != null)
                {
                    foreach (var id in ids)
                    {
                        var meta = ItemAssetsCollection.GetMetaData(id);
                        var metaName = meta.Name ?? string.Empty;
                        if (!metaName.StartsWith("Bullet_", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!IsUsableItemIcon(meta.icon))
                            continue;

                        if (!string.IsNullOrEmpty(meta.caliber) &&
                            !_ammoIconByCaliber.ContainsKey(meta.caliber))
                            _ammoIconByCaliber[meta.caliber] = meta.icon;
                        _ammoIconCandidates.Add(
                            $"meta:{id}:{metaName}:{meta.icon.name}:caliber={meta.caliber}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[inventory] ammo meta scan failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sprite == null || string.IsNullOrEmpty(sprite.name))
                        continue;
                    if (!LooksLikeAmmoIconName(sprite.name))
                        continue;
                    var texture = sprite.texture != null ? sprite.texture.name : "-";
                    _ammoIconCandidates.Add($"sprite:{sprite.name}:texture={texture}");
                }
            }
            catch (Exception ex)
            {
                Log("[inventory] ammo sprite scan failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (!_ammoIconCandidatesLogged)
            {
                _ammoIconCandidatesLogged = true;
                Log($"[inventory] ammo icon candidates: {DescribeAmmoIconCandidates()}");
            }
        }

        private static bool LooksLikeAmmoIconName(string name)
        {
            return name.IndexOf("Bullet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Ammo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("SMG", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("_AR_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Caliber", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private Sprite FindAmmoIconByName(Item item)
        {
            var name = item != null ? item.name : string.Empty;
            if (string.IsNullOrEmpty(name))
                return null;

            var clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone >= 0)
                name = name.Substring(0, clone);
            var last = name.LastIndexOf('_');
            var prefix = last > 0 ? name.Substring(0, last) : name;
            if (prefix.Length < 4)
                return null;

            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sprite == null || string.IsNullOrEmpty(sprite.name))
                    continue;
                if (sprite.name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    IsUsableItemIcon(sprite))
                    return sprite;
            }
            return null;
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            foreach (var needle in needles)
            {
                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private Sprite FindAmmoFamilyIcon(Item item)
        {
            var name = item != null ? item.name : string.Empty;
            if (string.IsNullOrEmpty(name))
                return null;

            // Native bullet prefab names use short caliber families; the curated UI icons are
            // one per weapon class (ammo_machinegun/pistol/sniper/shotgun/rocket).
            string family = null;
            if (ContainsAny(name, "_AR_", "_BR_", "_MG_", "_SMG_"))
                family = "ammo_machinegun";
            else if (ContainsAny(name, "_SHOT_", "_SHT_"))
                family = "ammo_shotgun";
            else if (ContainsAny(name, "_SNP_", "_Sniper"))
                family = "ammo_sniper";
            else if (ContainsAny(name, "_Pistol", "_Pis_", "_MAG_", "_NM_"))
                family = "ammo_pistol";
            else if (ContainsAny(name, "_Rocket", "_ARR_"))
                family = "ammo_rocket";
            if (family == null)
                return null;

            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sprite == null || string.IsNullOrEmpty(sprite.name))
                    continue;
                if (sprite.name.StartsWith(family, StringComparison.OrdinalIgnoreCase) &&
                    IsUsableItemIcon(sprite))
                    return sprite;
            }
            return null;
        }

        private string DescribeAmmoIconCandidates()
        {
            if (_ammoIconCandidates.Count == 0)
                return "<none>";

            var builder = new StringBuilder();
            var count = Math.Min(_ammoIconCandidates.Count, MaxLoggedIconCandidates);
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                    builder.Append(" | ");
                builder.Append(_ammoIconCandidates[i]);
            }
            if (_ammoIconCandidates.Count > count)
                builder.Append(" | ...(+").Append(_ammoIconCandidates.Count - count).Append(')');
            return builder.ToString();
        }
    }
}
