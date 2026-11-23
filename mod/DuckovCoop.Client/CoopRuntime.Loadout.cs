using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DuckovCoop.Client;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private uint[] _lastLoadout;
        private float _nextLoadoutSend;
        private int _lastLoadoutCount;
        // ---------- B2: local loadout ----------

        private void UpdateLoadout()
        {
            if (_client == null || Time.unscaledTime < _nextLoadoutSend)
                return;

            _nextLoadoutSend = Time.unscaledTime + 2f;

            var loadout = ReadLocalLoadout();
            if (loadout == null)
                return;

            if (_lastLoadout != null)
            {
                var same = true;
                for (var i = 0; i < loadout.Length; i++)
                {
                    if (loadout[i] == _lastLoadout[i])
                        continue;
                    same = false;
                    break;
                }

                if (same)
                    return;
            }

            _lastLoadout = loadout;
            _playerStatsDirty = true; // v5: CPlayerStats carries maxHealth; resend on equipment change.

            var entries = new List<LoadoutEntry>();
            for (byte slot = 0; slot < loadout.Length; slot++)
                if (loadout[slot] != 0)
                    entries.Add(new LoadoutEntry { Slot = slot, ItemId = loadout[slot] });

            _client.SendLoadout(++_correlation, entries.ToArray());
            Log($"loadout sent: slots={entries.Count} [" +
                string.Join(",", entries.Select(e => $"{e.Slot}:{e.ItemId}")) + "]");
        }

        private void CheckLoadoutResult()
        {
            if (_client == null || _client.LoadoutCount == _lastLoadoutCount)
                return;

            _lastLoadoutCount = _client.LoadoutCount;
            if (_client.Loadouts.TryGetValue(_client.PlayerId, out var state))
                Log($"loadout result: code={state.Code} slots={state.Entries.Length}");
        }

        private static uint[] ReadLocalLoadout()
        {
            try
            {
                var main = CharacterMainControl.Main;
                if (main == null)
                    return null;

                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                // v5: slots 0-7 classic equipment, 8/9 Totem1/Totem2 (ADR-022).
                var result = new uint[10];

                // slot 0 primary, 1 secondary, 2 melee, 3 armor, 4 helmet,
                // 5 face mask, 6 backpack, 7 headset, 8/9 totems
                var equipment = main.GetType().GetProperty("EquipmentController", any)?.GetValue(main);
                if (equipment != null)
                {
                    result[3] = ReadSlotItem(equipment.GetType().GetField("armorSlot", any)?.GetValue(equipment));
                    result[4] = ReadSlotItem(equipment.GetType().GetField("helmatSlot", any)?.GetValue(equipment));
                    result[5] = ReadSlotItem(equipment.GetType().GetField("faceMaskSlot", any)?.GetValue(equipment));
                    result[6] = ReadSlotItem(equipment.GetType().GetField("backpackSlot", any)?.GetValue(equipment));
                    result[7] = ReadSlotItem(equipment.GetType().GetField("headsetSlot", any)?.GetValue(equipment));
                }

                result[0] = ReadSlotItem(InvokeSlotMethod(main, "PrimWeaponSlot"));
                result[1] = ReadSlotItem(InvokeSlotMethod(main, "SecWeaponSlot"));
                result[2] = ReadSlotItem(InvokeSlotMethod(main, "MeleeWeaponSlot"));

                var characterItem = main.GetType().GetProperty("CharacterItem", any)?.GetValue(main);
                result[8] = ReadSlotByKey(characterItem, "Totem1");
                result[9] = ReadSlotByKey(characterItem, "Totem2");
                return result;
            }
            catch (Exception ex)
            {
                Log("loadout read failed: " + ex.GetType().Name + " " + ex.Message);
                return null;
            }
        }

        private static object InvokeSlotMethod(object instance, string name)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return instance.GetType().GetMethod(name, any)?.Invoke(instance, null);
        }

        private static uint ReadSlotItem(object slot)
        {
            if (slot == null)
                return 0;

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var content = slot.GetType().GetProperty("Content", any)?.GetValue(slot)
                          ?? slot.GetType().GetField("content", any)?.GetValue(slot);
            if (content == null)
                return 0;

            var typeId = content.GetType().GetField("typeID", any)?.GetValue(content)
                         ?? content.GetType().GetProperty("TypeID", any)?.GetValue(content);
            return typeId is int id && id > 0 ? (uint)id : 0;
        }

        /// <summary>v5: read a character-item slot by its key (Totem1/Totem2).</summary>
        private static uint ReadSlotByKey(object characterItem, string key)
        {
            if (characterItem == null)
                return 0;

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var slots = characterItem.GetType().GetProperty("Slots", any)?.GetValue(characterItem) as IEnumerable;
                if (slots == null)
                    return 0;
                foreach (var slot in slots)
                {
                    if (slot == null)
                        continue;
                    var slotKey = slot.GetType().GetProperty("Key", any)?.GetValue(slot) as string;
                    if (!string.Equals(slotKey, key, StringComparison.Ordinal))
                        continue;
                    return ReadSlotItem(slot);
                }
            }
            catch (Exception ex)
            {
                Log($"loadout slot read failed ({key}): {ex.GetType().Name} {ex.Message}");
            }

            return 0;
        }

    }
}
