using System;
using System.Reflection;
using ItemStatsSystem;
using UnityEngine;

namespace DuckovCoopClient
{
    public sealed partial class CoopRuntime
    {
        private bool _quickBarCleared;
        private float _nextQuickBarTry;

        /// <summary>
        /// The native quick bar (<c>Duckov.ItemShortcut</c>) is saved with the character, so a
        /// co-op kit inherits whatever was assigned in single player. Clear it once per raid and
        /// let the player decide what goes there.
        /// </summary>
        private void UpdateQuickBarClear()
        {
            if (!_inRaidScene)
            {
                _quickBarCleared = false;
                return;
            }

            if (_quickBarCleared || Time.unscaledTime < _nextQuickBarTry)
                return;
            _nextQuickBarTry = Time.unscaledTime + 1f;

            // Wait for the local character; the save reload would restore a cleared bar.
            if (CharacterMainControl.Main == null)
                return;

            var type = FindTypeByName("ItemShortcut");
            if (type == null)
                return;
            var instanceField =
                type.GetField("Instance", BindingFlags.Public | BindingFlags.Static) ??
                type.GetField("Instance", BindingFlags.NonPublic | BindingFlags.Static);
            var instance = instanceField?.GetValue(null);
            if (instance == null)
                return;

            var maxIndex = 8;
            var maxIndexProp = type.GetProperty("MaxIndex", BindingFlags.Instance | BindingFlags.Public);
            if (maxIndexProp != null)
            {
                try { maxIndex = Convert.ToInt32(maxIndexProp.GetValue(instance)); }
                catch { maxIndex = 8; }
            }

            var set = type.GetMethod("Set", BindingFlags.Instance | BindingFlags.Public,
                null, new[] { typeof(int), typeof(Item) }, null);
            var setLocal = type.GetMethod("Set_Local", BindingFlags.Instance | BindingFlags.Public,
                null, new[] { typeof(int), typeof(Item) }, null);
            var setDirty = type.GetMethod("SetDirty", BindingFlags.Instance | BindingFlags.Public,
                null, new[] { typeof(int) }, null);
            var save = type.GetMethod("Save", BindingFlags.Instance | BindingFlags.Public,
                null, Type.EmptyTypes, null);

            var cleared = 0;
            for (var i = 0; i < maxIndex; i++)
            {
                var ok = false;
                if (set != null)
                {
                    try
                    {
                        if (set.Invoke(instance, new object[] { i, null }) is bool accepted)
                            ok = accepted;
                    }
                    catch
                    {
                        ok = false;
                    }
                }
                if (!ok && setLocal != null)
                {
                    try
                    {
                        setLocal.Invoke(instance, new object[] { i, null });
                        setDirty?.Invoke(instance, new object[] { i });
                        ok = true;
                    }
                    catch
                    {
                        // best effort
                    }
                }
                if (ok)
                    cleared++;
            }

            try { save?.Invoke(instance, null); }
            catch { }
            _quickBarCleared = true;
            Log($"[shortcut] quick bar cleared slots={cleared}/{maxIndex}");
        }
    }
}
