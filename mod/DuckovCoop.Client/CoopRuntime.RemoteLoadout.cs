using System;
using System.Collections.Generic;
using System.Reflection;
using DuckovCoop.Client;
using Duckov.Utilities;
using ItemStatsSystem;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// B2: apply the server-broadcast remote player loadout (slots 0..7) to the
    /// passive player mirror. The mirror keeps CharacterMainControl +
    /// CharacterEquipmentController so armor/helmet/weapon models can update.
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private readonly Dictionary<ushort, int> _remoteLoadoutSignature =
            new Dictionary<ushort, int>();
        // T7c: the game cleans up the mirror's standalone CharacterItem ~5s after it is
        // created (observed with zero V8 messages, so it is not caused by the apply).
        // Keep the type/id and rebuild or re-set the item as soon as the reference is lost.
        private readonly Dictionary<ushort, ItemStatsSystem.Item> _mirrorCharacterItem =
            new Dictionary<ushort, ItemStatsSystem.Item>();
        private readonly Dictionary<ushort, int> _mirrorCharacterItemType =
            new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, float> _mirrorCharacterItemCreateAt =
            new Dictionary<ushort, float>();
        private readonly Dictionary<ushort, float> _mirrorCharacterItemLogAt =
            new Dictionary<ushort, float>();

        private bool RestoreMirrorCharacterItem(ushort playerId, CharacterMainControl control)
        {
            // Re-setting the same item is cheap: try it every frame before re-creating.
            if (_mirrorCharacterItem.TryGetValue(playerId, out var cached) && cached != null)
            {
                try
                {
                    control.SetItem(cached);
                    if (control.CharacterItem != null)
                    {
                        ClearMirrorGear(control);
                        ResetPlayerSlotCaches(playerId);
                        LogRestore(playerId, "restored", cached.TypeID);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LogRestore(playerId, "restore-failed: " + ex.GetType().Name, cached.TypeID);
                }
            }

            if (_mirrorCharacterItemCreateAt.TryGetValue(playerId, out var at) &&
                Time.unscaledTime < at)
                return false;
            _mirrorCharacterItemCreateAt[playerId] = Time.unscaledTime + 0.5f;

            var typeId = 0;
            if (_mirrorCharacterItemType.TryGetValue(playerId, out var cachedType))
                typeId = cachedType;
            if (typeId <= 0)
                typeId = PreferredMirrorItemType();
            if (typeId <= 0)
            {
                // The default type is 0 and InstantiateAsync(0) yields a bare FallbackItem
                // that the game destroys. A clone of the local player's real CharacterItem
                // keeps the prefab slots; clone it again whenever the mirror item is gone.
                var cloned = CloneLocalCharacterItem(LocalMainCharacter());
                if (cloned != null)
                {
                    control.SetItem(cloned);
                    if (control.CharacterItem != null)
                    {
                        _mirrorCharacterItem[playerId] = control.CharacterItem;
                        _mirrorCharacterItemType[playerId] = control.CharacterItem.TypeID;
                        ClearMirrorGear(control);
                        ResetPlayerSlotCaches(playerId);
                        LogRestore(playerId, "cloned", control.CharacterItem.TypeID);
                        return true;
                    }
                }
                var main = CharacterMainControl.Main;
                var level = LevelManager.Instance != null ? LevelManager.Instance.MainCharacter : null;
                var mainType = main != null && main.CharacterItem != null ? main.CharacterItem.TypeID : -1;
                var levelType = level != null && level.CharacterItem != null ? level.CharacterItem.TypeID : -1;
                var assets = GameplayDataSettings.ItemAssets;
                var defaultType = assets != null ? assets.DefaultCharacterItemTypeID : -1;
                LogRestore(playerId,
                    $"no-type(main={mainType} level={levelType} default={defaultType} " +
                    $"cached={(_mirrorCharacterItemType.TryGetValue(playerId, out var t) ? t : -1)})", 0);
                return false;
            }

            try
            {
                var item = ItemStatsSystem.ItemAssetsCollection.InstantiateSync(typeId);
                if (item == null)
                {
                    LogRestore(playerId, "instantiate-failed", typeId);
                    return false;
                }
                control.SetItem(item);
                if (control.CharacterItem != null)
                {
                    _mirrorCharacterItem[playerId] = control.CharacterItem;
                    _mirrorCharacterItemType[playerId] = typeId;
                    ClearMirrorGear(control);
                    ResetPlayerSlotCaches(playerId);
                    LogRestore(playerId, "recreated", typeId);
                    return true;
                }
                LogRestore(playerId, "set-item-failed", typeId);
            }
            catch (Exception ex)
            {
                LogRestore(playerId, "create-failed: " + ex.GetType().Name, typeId);
            }
            return false;
        }

        private void LogRestore(ushort playerId, string result, int typeId)
        {
            if (_mirrorCharacterItemLogAt.TryGetValue(playerId, out var at) &&
                Time.unscaledTime - at < 2f)
                return;
            _mirrorCharacterItemLogAt[playerId] = Time.unscaledTime;
            Log($"[v8] mirror character item {result} id={playerId} type={typeId}");
        }

        /// <summary>Type the game itself uses for characters created by CharacterCreator.</summary>
        private static int DefaultMirrorItemType()
        {
            var assets = GameplayDataSettings.ItemAssets;
            return assets != null ? assets.DefaultCharacterItemTypeID : 0;
        }

        /// <summary>Local player's character item type is a working stand-in for the mirror.</summary>
        private static int PreferredMirrorItemType()
        {
            var typeId = DefaultMirrorItemType();
            if (typeId > 0)
                return typeId;
            var local = LocalMainCharacter();
            return local != null && local.CharacterItem != null ? local.CharacterItem.TypeID : 0;
        }

        /// <summary>
        /// Drop the mirror's visual gear agents after its CharacterItem reference was
        /// restored, so the V8 hashes can re-plug the slots without duplicates.
        /// </summary>
        private void ClearMirrorGear(CharacterMainControl control)
        {
            try
            {
                foreach (var slot in GetRemoteSlots(control))
                {
                    if (slot != null && slot.Content != null)
                        slot.Unplug();
                }
            }
            catch (Exception ex)
            {
                Log($"[v8] mirror gear clear failed: {ex.GetType().Name}: {ex.Message}");
            }
            ClearAllItemAgents(control.gameObject);
        }

        private void ApplyRemoteLoadout(ushort playerId, GameObject remote)
        {
            if (_client == null || remote == null)
                return;

            var control = remote.GetComponentInChildren<CharacterMainControl>(true);
            ProbeRemoteV8(playerId, control);
            if (control == null)
                return;
            if (control.CharacterItem == null)
            {
                if (!RestoreMirrorCharacterItem(playerId, control))
                    return;
            }
            else
            {
                _mirrorCharacterItem[playerId] = control.CharacterItem;
                _mirrorCharacterItemType[playerId] = control.CharacterItem.TypeID;
            }

            // The lobby has no V8 face payload; use the game's default yellow-duck face
            // rather than the local player's custom face. In-raid this also seeds the default
            // until the remote V8 face arrives.
            ApplyRemoteFace(playerId, control);

            if (ApplyRemotePlayerV8(playerId, remote))
                return;

            if (!_client.Loadouts.TryGetValue(playerId, out var loadout) || loadout == null)
                return;

            var signature = ComputeRemoteLoadoutSignature(loadout);
            if (_remoteLoadoutSignature.TryGetValue(playerId, out var previous) &&
                previous == signature)
                return;

            try
            {
                var desired = new Dictionary<byte, uint>();
                foreach (var entry in loadout.Entries)
                {
                    if (entry.ItemId != 0)
                        desired[entry.Slot] = entry.ItemId;
                }

                var slots = GetRemoteSlots(control);
                for (byte slotIndex = 0; slotIndex < slots.Length; slotIndex++)
                {
                    var slot = slots[slotIndex];
                    if (slot == null)
                        continue;

                    if (!desired.TryGetValue(slotIndex, out var itemId))
                    {
                        if (slot.Content != null)
                            slot.Unplug();
                        continue;
                    }
                    if (slot.Content != null && slot.Content.TypeID == unchecked((int)itemId))
                        continue;

                    var item = ItemStatsSystem.ItemAssetsCollection.InstantiateSync(
                        unchecked((int)itemId));
                    if (item == null)
                    {
                        Log($"[mirror] loadout item missing player={playerId} slot={slotIndex} " +
                            $"item={itemId}");
                        continue;
                    }
                    if (!slot.Plug(item, out _))
                        Log($"[mirror] loadout plug rejected player={playerId} slot={slotIndex} " +
                            $"item={itemId}");
                }

                // Hold the primary weapon (or melee if the player has no primary).
                ItemStatsSystem.Item hold = null;
                if (slots.Length > 0 && slots[0] != null)
                    hold = slots[0].Content;
                if (hold == null && slots.Length > 2 && slots[2] != null)
                    hold = slots[2].Content;
                if (control.agentHolder != null)
                    control.agentHolder.ChangeHoldItem(hold);

                var animator = remote.GetComponentInChildren<Animator>(true);
                if (animator != null)
                {
                    if (HasParam(animator, "HandState"))
                        animator.SetInteger("HandState", hold != null ? 1 : 0);
                    if (HasParam(animator, "GunReady"))
                        animator.SetBool("GunReady", hold != null);
                }

                _remoteLoadoutSignature[playerId] = signature;
                Log($"[mirror] loadout player={playerId} slots={desired.Count} " +
                    $"hold={(hold != null ? hold.TypeID : 0)}");
            }
            catch (Exception ex)
            {
                _remoteLoadoutSignature[playerId] = signature;
                Log($"[mirror] loadout failed player={playerId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static int ComputeRemoteLoadoutSignature(LoadoutState loadout)
        {
            var hash = 17;
            foreach (var entry in loadout.Entries)
                hash = unchecked(hash * 31 + entry.Slot * 397 ^ (int)entry.ItemId);
            return hash;
        }

        private static ItemStatsSystem.Items.Slot[] GetRemoteSlots(CharacterMainControl control)
        {
            var slots = new ItemStatsSystem.Items.Slot[8];
            try
            {
                slots[0] = control.PrimWeaponSlot();
                slots[1] = control.SecWeaponSlot();
                slots[2] = control.MeleeWeaponSlot();

                var equipment = control.EquipmentController;
                if (equipment != null)
                {
                    const BindingFlags any = BindingFlags.Instance | BindingFlags.Public |
                                             BindingFlags.NonPublic;
                    slots[3] = equipment.GetType().GetField("armorSlot", any)?.GetValue(equipment)
                        as ItemStatsSystem.Items.Slot;
                    slots[4] = equipment.GetType().GetField("helmatSlot", any)?.GetValue(equipment)
                        as ItemStatsSystem.Items.Slot;
                    slots[5] = equipment.GetType().GetField("faceMaskSlot", any)?.GetValue(equipment)
                        as ItemStatsSystem.Items.Slot;
                    slots[6] = equipment.GetType().GetField("backpackSlot", any)?.GetValue(equipment)
                        as ItemStatsSystem.Items.Slot;
                    slots[7] = equipment.GetType().GetField("headsetSlot", any)?.GetValue(equipment)
                        as ItemStatsSystem.Items.Slot;
                }
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[mirror] slot lookup failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return slots;
        }

        private readonly HashSet<ushort> _remoteAttackLogged = new HashSet<ushort>();

        private void NoteRemotePlayerAttack(ushort playerId, uint weaponId)
        {
            if (!_remotes.TryGetValue(playerId, out var remote) || remote == null)
                return;
            var animator = remote.GetComponentInChildren<Animator>(true);
            if (animator == null)
                return;
            try
            {
                if (HasParam(animator, "Attack"))
                    animator.SetTrigger("Attack");
                if (HasParam(animator, "GunReady"))
                    animator.SetBool("GunReady", true);
                if (_remoteAttackLogged.Add(playerId))
                    Log($"[mirror] attack player={playerId} weapon={weaponId}");
            }
            catch
            {
                // visual only
            }
        }

        private void ClearRemoteLoadout(ushort playerId)
        {
            _remoteLoadoutSignature.Remove(playerId);
            _remoteAttackLogged.Remove(playerId);
        }
    }
}
