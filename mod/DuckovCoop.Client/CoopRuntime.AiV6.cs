using System;
using System.Collections.Generic;
using DuckovCoop.Client;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// ADR-023 / wire v6: server-authored AI loadout + host-authored aim.
    /// Host samples the native AI's aim direction; non-owner replicas apply the
    /// server-broadcast loadout and aim so muzzle/weapon orientation matches the host.
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private readonly Dictionary<uint, ushort> _aiAimSeq = new Dictionary<uint, ushort>();
        private readonly Dictionary<uint, float> _aiAimNextSend = new Dictionary<uint, float>();
        private readonly Dictionary<uint, ushort> _aiAimSeenSeq = new Dictionary<uint, ushort>();
        private readonly Dictionary<uint, float> _aiAimReceivedAt = new Dictionary<uint, float>();
        private readonly HashSet<uint> _aiAimLogged = new HashSet<uint>();
        private readonly HashSet<uint> _aiAimDiag = new HashSet<uint>();
        private readonly HashSet<uint> _aiWeaponMismatchLogged = new HashSet<uint>();
        private readonly Dictionary<uint, uint> _appliedAiWeapon = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, uint> _appliedAiArmor = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, uint> _appliedAiHelmet = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, ItemStatsSystem.Item> _pendingAiWeapon = new Dictionary<uint, ItemStatsSystem.Item>();

        /// <summary>Host: send 10Hz world-space aim for each owned real AI.</summary>
        private void UpdateAiAimSampling()
        {
            if (!_inRaidScene || _client == null || _realAi.Count == 0)
                return;

            foreach (var pair in _realAi)
            {
                var ai = pair.Value;
                if (ai == null)
                    continue;
                if (_aiAimNextSend.TryGetValue(pair.Key, out var next) && Time.time < next)
                    continue;

                try
                {
                    var aiController = ai.GetComponent<AICharacterController>();
                    var control = aiController != null ? aiController.CharacterMainControl : null;
                    if (control == null)
                        control = ai.GetComponentInChildren<CharacterMainControl>(true);
                    Vector3 direction;
                    if (control != null)
                    {
                        var socket = control.CurrentUsingAimSocket;
                        direction = socket != null ? socket.forward : control.CurrentAimDirection;
                    }
                    else
                    {
                        direction = ai.transform.forward;
                        if (_aiAimDiag.Add(pair.Key))
                            Log($"[v6] aim no CharacterMainControl ai={pair.Key}; using root forward");
                    }

                    if (direction.sqrMagnitude < 0.0001f)
                    {
                        direction = ai.transform.forward;
                        if (_aiAimDiag.Add(pair.Key))
                            Log($"[v6] aim direction zero ai={pair.Key}; using root forward");
                    }
                    if (direction.sqrMagnitude < 0.0001f)
                        continue;

                    var yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
                    yaw = Mathf.Repeat(yaw, 360f);
                    var pitch = Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;

                    var seq = (ushort)(_aiAimSeq.TryGetValue(pair.Key, out var stored)
                        ? stored + 1
                        : 1);
                    _aiAimSeq[pair.Key] = seq;
                    _aiAimNextSend[pair.Key] = Time.time + 0.1f;

                    var aim = new AiAim
                    {
                        AiId = pair.Key,
                        RaidEpoch = _aiSpawnEpoch,
                        Seq = seq,
                        AimYaw = (ushort)Mathf.Clamp(
                            Mathf.RoundToInt(yaw / 360f * 65535f), 0, 65535),
                        AimPitch = (short)Mathf.Clamp(
                            Mathf.RoundToInt(pitch * 100f), -9000, 9000),
                        Flags = 0,
                    };
                    _client.SendAiAim(aim);
                    if (_aiAimLogged.Add(pair.Key))
                        Log($"[v6] aim send ai={pair.Key} yaw={yaw:F1} pitch={pitch:F1}");
                }
                catch (Exception ex)
                {
                    if (_aiAimLogged.Add(pair.Key))
                        Log($"[v6] aim sample failed ai={pair.Key}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Non-owner: apply the host's weapon to the replica. Returns false when no
        /// loadout is available yet, so the caller can keep the local preset fallback.
        /// </summary>
        private bool ApplyHostAiLoadout(uint aiId, CharacterMainControl character)
        {
            if (character == null || _client == null)
                return false;
            if (!_client.AiLoadouts.TryGetValue(aiId, out var loadout) || loadout == null)
                return false;
            if (loadout.RaidEpoch != 0 && _aiSpawnEpoch != 0 &&
                loadout.RaidEpoch != _aiSpawnEpoch)
                return false;

            var weaponApplied = IsApplied(_appliedAiWeapon, aiId, loadout.WeaponId);
            var armorApplied = IsApplied(_appliedAiArmor, aiId, loadout.ArmorId);
            var helmetApplied = IsApplied(_appliedAiHelmet, aiId, loadout.HelmetId);

            var characterItem = character.CharacterItem;
            var changed = false;
            if (!weaponApplied && loadout.WeaponId != 0)
            {
                try
                {
                    var item = FindCharacterItemByType(character, unchecked((int)loadout.WeaponId));
                    if (item == null)
                        _pendingAiWeapon.TryGetValue(aiId, out item);
                    if (item == null)
                    {
                        item = ItemStatsSystem.ItemAssetsCollection.InstantiateSync(
                            unchecked((int)loadout.WeaponId));
                    }
                    if (item == null)
                    {
                        Log($"[v6] loadout item missing ai={aiId} weapon={loadout.WeaponId}");
                    }
                    else
                    {
                        if (characterItem != null)
                            ItemUtilities.TryPlug(characterItem, item);
                        if (TryHoldItem(character, item))
                        {
                            _appliedAiWeapon[aiId] = loadout.WeaponId;
                            _pendingAiWeapon.Remove(aiId);
                            weaponApplied = true;
                            changed = true;
                        }
                        else
                        {
                            _pendingAiWeapon[aiId] = item;
                            if (_aiWeaponMismatchLogged.Add(aiId))
                            {
                                var held = character.CurrentHoldItemAgent;
                                Log($"[v6] weapon switch pending ai={aiId} " +
                                    $"want={loadout.WeaponId} " +
                                    $"held={(held != null && held.Item != null ? held.Item.TypeID : 0)}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[v6] replica weapon failed ai={aiId} weapon={loadout.WeaponId}: " +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (!armorApplied)
                armorApplied = ApplyEquipmentSlot(aiId, characterItem,
                    CharacterEquipmentController.armorHash, loadout.ArmorId,
                    "armor", _appliedAiArmor);
            if (!helmetApplied)
                helmetApplied = ApplyEquipmentSlot(aiId, characterItem,
                    CharacterEquipmentController.helmatHash, loadout.HelmetId,
                    "helmet", _appliedAiHelmet);

            if (weaponApplied && armorApplied && helmetApplied)
            {
                if (changed)
                {
                    var hold = character.CurrentHoldItemAgent;
                    Log($"[v6] replica loadout ai={aiId} weapon={loadout.WeaponId} " +
                        $"bullet={loadout.BulletId} armor={loadout.ArmorId} " +
                        $"helmet={loadout.HelmetId} hold={(hold != null ? hold.name : "<null>")}");
                }
                return true;
            }

            // WeaponId==0 means melee/unknown: keep the local preset weapon fallback.
            return loadout.WeaponId != 0 && weaponApplied;
        }

        private static ItemStatsSystem.Item FindCharacterItemByType(
            CharacterMainControl character, int typeId)
        {
            if (character == null || typeId <= 0)
                return null;
            try
            {
                var slots = character.CharacterItem != null
                    ? character.CharacterItem.Slots
                    : null;
                if (slots == null)
                    return null;
                foreach (var slot in slots)
                {
                    var item = slot != null ? slot.Content : null;
                    if (item != null && item.TypeID == typeId)
                        return item;
                }
            }
            catch
            {
                // inventory may not be initialised yet
            }
            return null;
        }

        private static bool TryHoldItem(CharacterMainControl character, ItemStatsSystem.Item item)
        {
            if (character == null || item == null)
                return false;
            try
            {
                var held = character.CurrentHoldItemAgent;
                if (held != null && held.Item == item)
                    return true;

                character.ChangeHoldItem(item);
                held = character.CurrentHoldItemAgent;
                if (held != null && held.Item != null && held.Item.TypeID == item.TypeID)
                    return true;

                // Replica has no player action controller; bypass the action gate and
                // change the holder directly, then verify the actual agent type.
                if (character.agentHolder != null)
                    character.agentHolder.ChangeHoldItem(item);
                held = character.CurrentHoldItemAgent;
                return held != null && held.Item != null && held.Item.TypeID == item.TypeID;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsApplied(Dictionary<uint, uint> applied, uint aiId, uint value)
        {
            return applied.TryGetValue(aiId, out var stored) && stored == value;
        }

        private bool ApplyEquipmentSlot(uint aiId, ItemStatsSystem.Item characterItem, int slotHash,
            uint itemId, string kind, Dictionary<uint, uint> applied)
        {
            if (characterItem == null)
                return false;
            try
            {
                var slot = characterItem.Slots.GetSlot(slotHash);
                if (slot == null)
                {
                    Log($"[v6] replica {kind} slot missing ai={aiId}");
                    return false;
                }

                if (itemId == 0)
                {
                    if (slot.Content != null)
                        slot.Unplug();
                    applied[aiId] = 0;
                    Log($"[v6] replica {kind} cleared ai={aiId}");
                    return true;
                }

                var item = ItemStatsSystem.ItemAssetsCollection.InstantiateSync(
                    unchecked((int)itemId));
                if (item == null)
                {
                    Log($"[v6] replica {kind} item missing ai={aiId} item={itemId}");
                    return false;
                }

                if (!slot.Plug(item, out _))
                {
                    Log($"[v6] replica {kind} plug rejected ai={aiId} item={itemId}");
                    return false;
                }
                applied[aiId] = itemId;
                Log($"[v6] replica {kind} ai={aiId} item={itemId}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[v6] replica {kind} failed ai={aiId} item={itemId}: " +
                    ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Non-owner: apply latest aim, keeping the native animation controller intact.</summary>
        private void ApplyAiAim(uint aiId, GameObject ghost, Transform modelRoot)
        {
            if (modelRoot == null)
                return;
            if (_client == null || !_client.AiAims.TryGetValue(aiId, out var aim) || aim == null)
            {
                modelRoot.localRotation = Quaternion.identity;
                return;
            }
            if (aim.RaidEpoch != 0 && _aiSpawnEpoch != 0 && aim.RaidEpoch != _aiSpawnEpoch)
                return;

            if (!_aiAimSeenSeq.TryGetValue(aiId, out var seen) || seen != aim.Seq)
            {
                _aiAimSeenSeq[aiId] = aim.Seq;
                _aiAimReceivedAt[aiId] = Time.unscaledTime;
            }
            var fresh = Time.unscaledTime -
                        (_aiAimReceivedAt.TryGetValue(aiId, out var at) ? at : 0f) < 0.5f;
            if (!fresh)
            {
                modelRoot.localRotation = Quaternion.identity;
                return;
            }

            var worldYaw = aim.AimYaw / 65535f * 360f;
            var pitch = aim.AimPitch / 100f;
            var targetWorld = Quaternion.Euler(-pitch, worldYaw, 0f);
            // modelRoot is a child of the body/model root; convert the host's world aim
            // into the local rotation the native animation controller expects.
            var target = modelRoot.parent != null
                ? Quaternion.Inverse(modelRoot.parent.rotation) * targetWorld
                : targetWorld;
            modelRoot.localRotation = Quaternion.Slerp(
                modelRoot.localRotation, target, 1f - Mathf.Exp(-18f * Time.deltaTime));

            if (_aiAimLogged.Add(aiId))
                Log($"[v6] aim apply ai={aiId} yaw={worldYaw:F1} pitch={pitch:F1}");
        }

        private void ClearAiV6State(uint aiId)
        {
            _aiAimSeq.Remove(aiId);
            _aiAimNextSend.Remove(aiId);
            _aiAimSeenSeq.Remove(aiId);
            _aiAimReceivedAt.Remove(aiId);
            _aiAimLogged.Remove(aiId);
            _aiAimDiag.Remove(aiId);
            _aiWeaponMismatchLogged.Remove(aiId);
            _pendingAiWeapon.Remove(aiId);
            _appliedAiWeapon.Remove(aiId);
            _appliedAiArmor.Remove(aiId);
            _appliedAiHelmet.Remove(aiId);
            _aiAnimApplyLogged.Remove(aiId);
            _aiAnimLastFlags.Remove(aiId);
        }
    }
}
