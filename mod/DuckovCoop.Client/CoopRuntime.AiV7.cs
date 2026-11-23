using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DuckovCoop.Client;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// ADR-024 / wire v7: native AnimSample state playback, opaque AI item snapshots
    /// and host-authored shot effects. The server only relays these presentation facts.
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private readonly Dictionary<uint, ushort> _aiStateSampleSeq = new Dictionary<uint, ushort>();
        private readonly Dictionary<uint, ushort> _aiStateSampleSeen = new Dictionary<uint, ushort>();
        private readonly Dictionary<uint, float> _aiStateSampleAt = new Dictionary<uint, float>();
        private readonly Dictionary<uint, uint> _aiAppliedStateHash = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, uint> _aiShotSeq = new Dictionary<uint, uint>();
        private readonly HashSet<uint> _aiShotLogged = new HashSet<uint>();
        private readonly HashSet<uint> _aiShotSoundLogged = new HashSet<uint>();
        private readonly Dictionary<uint, int> _aiV7LoadoutSignature = new Dictionary<uint, int>();
        private readonly Dictionary<uint, int> _aiV7LoadoutApplied = new Dictionary<uint, int>();
        private readonly HashSet<uint> _aiV7LoadoutLogged = new HashSet<uint>();
        private readonly HashSet<uint> _aiStateHashWarned = new HashSet<uint>();
        private readonly Dictionary<uint, float> _aiV7LoadoutNext = new Dictionary<uint, float>();

        /// <summary>Host: 10Hz native animator state + item snapshot + shot facts.</summary>
        private void UpdateAiV7Sampling()
        {
            if (!_inRaidScene || _client == null || _realAi.Count == 0)
                return;

            foreach (var pair in _realAi)
            {
                var ai = pair.Value;
                if (ai == null)
                    continue;

                var controller = ai.GetComponent<AICharacterController>();
                var control = controller != null ? controller.CharacterMainControl : null;
                if (control == null)
                    control = ai.GetComponentInChildren<CharacterMainControl>(true);
                if (control == null)
                    continue;

                SendAiStateSampleIfDue(pair.Key, ai, control);
                SendAiLoadoutV7IfChanged(pair.Key, control);
            }
        }

        private void SendAiStateSampleIfDue(uint aiId, GameObject ai, CharacterMainControl control)
        {
            if (_aiStateSampleNext.TryGetValue(aiId, out var next) && Time.time < next)
                return;

            var animator = ResolveAiAnimator(aiId, ai, control);
            if (animator == null)
                return;

            try
            {
                var state = animator.GetCurrentAnimatorStateInfo(0);
                var seq = (ushort)(_aiStateSampleSeq.TryGetValue(aiId, out var stored)
                    ? stored + 1
                    : 1);
                _aiStateSampleSeq[aiId] = seq;
                _aiStateSampleNext[aiId] = Time.time + 0.1f;

                var norm = state.normalizedTime;
                norm -= Mathf.Floor(norm);
                var sample = new AiStateSample
                {
                    AiId = aiId,
                    RaidEpoch = _aiSpawnEpoch,
                    Seq = seq,
                    StateHash = unchecked((uint)state.shortNameHash),
                    NormTime = (ushort)Mathf.Clamp(Mathf.RoundToInt(norm * 65535f), 0, 65535),
                    Flags = (byte)(AiStateSample.FlagValid |
                            (animator.IsInTransition(0) ? AiStateSample.FlagInTransition : (byte)0)),
                };
                _client.SendAiStateSample(sample);
                if (_aiStateSampleLogged.Add(aiId))
                    Log($"[v7] state sample send ai={aiId} hash=0x{state.shortNameHash:X8} " +
                        $"norm={norm:F3}");
            }
            catch (Exception ex)
            {
                if (_aiStateSampleLogged.Add(aiId))
                    Log($"[v7] state sample failed ai={aiId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private readonly Dictionary<uint, float> _aiStateSampleNext = new Dictionary<uint, float>();
        private readonly HashSet<uint> _aiStateSampleLogged = new HashSet<uint>();

        private Animator ResolveAiAnimator(uint aiId, GameObject ai, CharacterMainControl control)
        {
            if (_realAiAnimators.TryGetValue(aiId, out var cached) && cached != null)
                return cached;

            Animator animator = null;
            try
            {
                var model = control != null ? control.characterModel : null;
                if (model != null)
                {
                    var magic = model.GetComponent<CharacterAnimationControl_MagicBlend>();
                    if (magic != null && magic.animator != null)
                        animator = magic.animator;
                    if (animator == null)
                    {
                        var basic = model.GetComponent<CharacterAnimationControl>();
                        if (basic != null && basic.animator != null)
                            animator = basic.animator;
                    }
                }
            }
            catch
            {
                // fall through to child lookup
            }
            if (animator == null)
                animator = ai.GetComponentInChildren<Animator>(true);
            if (animator != null)
                _realAiAnimators[aiId] = animator;
            return animator;
        }

        /// <summary>Non-owner: play the host's native animator state.</summary>
        private void ApplyAiStateSample(Animator animator, uint aiId)
        {
            if (animator == null || _client == null ||
                !_client.AiStateSamples.TryGetValue(aiId, out var sample) || sample == null)
                return;
            if (sample.RaidEpoch != 0 && _aiSpawnEpoch != 0 && sample.RaidEpoch != _aiSpawnEpoch)
                return;
            if (!_aiStateSampleSeen.TryGetValue(aiId, out var seen) || seen != sample.Seq)
            {
                _aiStateSampleSeen[aiId] = sample.Seq;
                _aiStateSampleAt[aiId] = Time.unscaledTime;
            }
            if (Time.unscaledTime - (_aiStateSampleAt.TryGetValue(aiId, out var at) ? at : 0f) > 0.5f)
                return;
            if ((sample.Flags & AiStateSample.FlagValid) == 0)
                return;

            try
            {
                var hash = unchecked((int)sample.StateHash);
                if (!animator.HasState(0, hash))
                {
                    if (_aiStateHashWarned.Add(aiId))
                        Log($"[v7] state hash missing ai={aiId} hash=0x{sample.StateHash:X8}");
                    return;
                }
                var norm = sample.NormTime / 65535f;
                if (_aiAppliedStateHash.TryGetValue(aiId, out var appliedHash) &&
                    appliedHash == sample.StateHash)
                    return;
                _aiAppliedStateHash[aiId] = sample.StateHash;
                animator.Play(hash, 0, Mathf.Clamp01(norm));
                Log($"[v7] state apply ai={aiId} hash=0x{sample.StateHash:X8} norm={norm:F2}");
            }
            catch (Exception ex)
            {
                if (_aiStateHashWarned.Add(aiId))
                    Log($"[v7] state apply failed ai={aiId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Host: capture one AI shot from ItemAgent_Gun.ShootOneBullet prefix.</summary>
        internal void NoteAiShot(ItemAgent_Gun gun, CharacterMainControl holder,
            Vector3 muzzlePoint, Vector3 direction)
        {
            if (gun == null || holder == null || _client == null ||
                _client.State != ClientState.InGame || !_inRaidScene)
                return;
            if (!TryResolveAiId(holder.gameObject, out var aiId) || !OwnsAi(aiId))
                return;
            if (direction.sqrMagnitude < 0.0001f)
                return;
            direction.Normalize();

            var seq = (_aiShotSeq.TryGetValue(aiId, out var stored) ? stored : 0u) + 1u;
            _aiShotSeq[aiId] = seq;
            var shot = new AiShot
            {
                AiId = aiId,
                RaidEpoch = _aiSpawnEpoch,
                ShotId = seq,
                WeaponId = gun.Item != null ? (uint)gun.Item.TypeID : 0u,
                BulletId = gun.BulletItem != null ? (uint)gun.BulletItem.TypeID : 0u,
                OriginX = Mathf.RoundToInt(muzzlePoint.x * 100f),
                OriginY = Mathf.RoundToInt(muzzlePoint.y * 100f),
                OriginZ = Mathf.RoundToInt(muzzlePoint.z * 100f),
                DirX = (short)Mathf.Clamp(Mathf.RoundToInt(direction.x * 32767f), short.MinValue, short.MaxValue),
                DirY = (short)Mathf.Clamp(Mathf.RoundToInt(direction.y * 32767f), short.MinValue, short.MaxValue),
                DirZ = (short)Mathf.Clamp(Mathf.RoundToInt(direction.z * 32767f), short.MinValue, short.MaxValue),
                Seed = unchecked((uint)UnityEngine.Random.Range(int.MinValue, int.MaxValue)),
                Flags = 0,
            };
            _client.SendAiShot(shot);
            if (_aiShotLogged.Add(aiId))
                Log($"[v7] shot send ai={aiId} weapon={shot.WeaponId} bullet={shot.BulletId} " +
                    $"origin=({muzzlePoint.x:F1},{muzzlePoint.y:F1},{muzzlePoint.z:F1})");
        }

        /// <summary>Non-owner: play host shot effect and orient the replica muzzle.</summary>
        private void UpdateAiShots()
        {
            if (_client == null || _client.AiShots.Count == 0)
                return;
            foreach (var pair in _client.AiShots)
            {
                var shot = pair.Value;
                if (shot == null)
                    continue;
                if (_aiShotSeen.TryGetValue(pair.Key, out var seen) && seen == shot.ShotId)
                    continue;
                _aiShotSeen[pair.Key] = shot.ShotId;
                SpawnAiShotFx(pair.Key, shot);
            }
        }

        private readonly Dictionary<uint, uint> _aiShotSeen = new Dictionary<uint, uint>();

        private void SpawnAiShotFx(uint aiId, AiShot shot)
        {
            try
            {
                if (!_aiGhosts.TryGetValue(aiId, out var ghost) || ghost == null)
                    return;
                var control = ghost.GetComponentInChildren<CharacterMainControl>(true);
                var agent = control != null ? control.CurrentHoldItemAgent : null;
                var origin = new Vector3(shot.OriginX / 100f, shot.OriginY / 100f, shot.OriginZ / 100f);
                var direction = new Vector3(shot.DirX, shot.DirY, shot.DirZ);
                if (direction.sqrMagnitude < 0.0001f)
                    return;
                direction.Normalize();

                if (agent != null)
                {
                    var setting = agent.GetType()
                        .GetField("_gunItemSetting", System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Public)
                        ?.GetValue(agent);
                    var fx = setting?.GetType()
                        .GetField("muzzleFxPfb", System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic)
                        ?.GetValue(setting) as GameObject;
                    if (fx != null)
                    {
                        var instance = UnityEngine.Object.Instantiate(
                            fx, origin, Quaternion.LookRotation(direction));
                        UnityEngine.Object.Destroy(instance, 1.5f);
                    }

                    // AI gunshots were visual-only on the guest: play the same native FMOD
                    // event the mirror uses for remote players (positional, so distance fades).
                    var shotEvent = PlayMirrorShotSound(agent, setting, origin, shot.WeaponId);
                    if (_aiShotSoundLogged.Add(aiId))
                    {
                        Log($"[v7] ai shot sound ai={aiId} weapon={shot.WeaponId} " +
                            $"event={shotEvent ?? "<none>"}");
                    }
                }

                SpawnAiBullet(aiId, origin, direction, agent as ItemAgent_Gun);

                // Snap the model to the authoritative shot direction; the per-10Hz
                // state sample then keeps the pose in sync.
                if (_aiReplicaModelRoots.TryGetValue(aiId, out var modelRoot) && modelRoot != null)
                {
                    var targetWorld = Quaternion.LookRotation(direction, Vector3.up);
                    modelRoot.localRotation = modelRoot.parent != null
                        ? Quaternion.Inverse(modelRoot.parent.rotation) * targetWorld
                        : targetWorld;
                }
                if (_aiShotLogged.Add(aiId))
                    Log($"[v7] shot apply ai={aiId} shot={shot.ShotId} weapon={shot.WeaponId}");
            }
            catch (Exception ex)
            {
                if (_aiShotLogged.Add(aiId))
                    Log($"[v7] shot fx failed ai={aiId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- loadout snapshots

        private void SendAiLoadoutV7IfChanged(uint aiId, CharacterMainControl control)
        {
            if (_aiV7LoadoutNext.TryGetValue(aiId, out var next) && Time.time < next)
                return;
            _aiV7LoadoutNext[aiId] = Time.time + 0.5f;

            try
            {
                var slots = new List<AiLoadoutSlot>();
                var signature = 17;
                ItemStatsSystem.Item primary = null;
                ItemStatsSystem.Item melee = null;
                ItemStatsSystem.Item armor = null;
                ItemStatsSystem.Item helmet = null;

                var characterItem = control.CharacterItem;
                if (characterItem != null)
                {
                    var primarySlot = control.PrimWeaponSlot();
                    primary = primarySlot != null ? primarySlot.Content : null;
                    var meleeSlot = control.MeleeWeaponSlot();
                    melee = meleeSlot != null ? meleeSlot.Content : null;
                    var armorSlot = characterItem.Slots.GetSlot(CharacterEquipmentController.armorHash);
                    armor = armorSlot != null ? armorSlot.Content : null;
                    var helmetSlot = characterItem.Slots.GetSlot(CharacterEquipmentController.helmatHash);
                    helmet = helmetSlot != null ? helmetSlot.Content : null;
                }
                if (primary == null && control.CurrentHoldItemAgent != null)
                    primary = control.CurrentHoldItemAgent.Item;

                AddSnapshotSlot(slots, 0, primary, ref signature);
                AddSnapshotSlot(slots, 1, armor, ref signature);
                AddSnapshotSlot(slots, 2, helmet, ref signature);
                AddSnapshotSlot(slots, 3, melee, ref signature);

                var activeItem = control.CurrentHoldItemAgent != null
                    ? control.CurrentHoldItemAgent.Item
                    : null;
                var activeSlot = (byte)(activeItem != null && melee != null &&
                                        activeItem.TypeID == melee.TypeID ? 3 : 0);

                var message = new AiLoadoutV7
                {
                    AiId = aiId,
                    RaidEpoch = _aiSpawnEpoch,
                    ActiveSlot = activeSlot,
                    Slots = slots,
                };

                if (_aiV7LoadoutSignature.TryGetValue(aiId, out var previous) &&
                    previous == signature)
                    return;

                _aiV7LoadoutSignature[aiId] = signature;
                _client.SendAiLoadoutV7(message);
                if (_aiV7LoadoutLogged.Add(aiId))
                    Log($"[v7] loadout send ai={aiId} slots={slots.Count} active={activeSlot} " +
                        $"sig=0x{signature:X8}");
            }
            catch (Exception ex)
            {
                if (_aiV7LoadoutLogged.Add(aiId))
                    Log($"[v7] loadout sample failed ai={aiId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void AddSnapshotSlot(List<AiLoadoutSlot> slots, byte slot,
            ItemStatsSystem.Item item, ref int signature)
        {
            if (item == null)
            {
                // Explicit empty slot: the replica uses it to clear a preset item.
                slots.Add(new AiLoadoutSlot { Slot = slot, Blob = Array.Empty<byte>() });
                signature = unchecked(signature * 31 + slot + 1);
                return;
            }
            var blob = SerializeAiItem(item, 256);
            if (blob == null)
            {
                Log($"[v7] loadout blob too large slot={slot} type={item.TypeID}");
                return;
            }
            slots.Add(new AiLoadoutSlot { Slot = slot, Blob = blob });
            signature = unchecked(signature * 31 + slot);
            foreach (var value in blob)
                signature = unchecked(signature * 31 + value);
        }

        private static byte[] SerializeAiItem(ItemStatsSystem.Item item, int maxBytes,
            bool skipVolatile = false)
        {
            if (item == null)
                return null;
            try
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    WriteAiItem(writer, item, 0, skipVolatile);
                    if (stream.Length <= maxBytes)
                        return stream.ToArray();
                }
            }
            catch
            {
                // fall through to the type-only snapshot
            }

            // The frozen v7 cap is 256B per slot. A fully-attached weapon can exceed
            // that; send the base type so the replica at least gets the host's weapon /
            // armor / helmet model instead of the local preset.
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(item.TypeID);
                writer.Write((byte)0);
                writer.Write((byte)0);
                return stream.ToArray();
            }
        }

        private static void WriteAiItem(BinaryWriter writer, ItemStatsSystem.Item item, int depth,
            bool skipVolatile)
        {
            if (item == null || depth > 6)
                throw new InvalidOperationException("item tree too deep");
            writer.Write(item.TypeID);

            var variables = new List<Duckov.Utilities.CustomData>();
            foreach (var variable in item.Variables)
            {
                // M2-v8.1: the player mirror only needs appearance. Count/durability/loaded
                // ammo change during normal play and would otherwise re-send the slot.
                if (skipVolatile && IsVolatileVariableKey(variable.Key))
                    continue;
                variables.Add(variable);
            }
            if (variables.Count > 255)
                throw new InvalidOperationException("too many variables");
            writer.Write((byte)variables.Count);
            foreach (var variable in variables)
            {
                var key = Encoding.UTF8.GetBytes(variable.Key ?? string.Empty);
                if (key.Length > 255)
                    throw new InvalidOperationException("variable key too long");
                writer.Write((byte)key.Length);
                writer.Write(key);
                writer.Write((byte)variable.DataType);
                var raw = variable.GetRawCopied();
                if (raw.Length > 1024)
                    throw new InvalidOperationException("variable too large");
                writer.Write((ushort)raw.Length);
                writer.Write(raw);
            }

            var slotContents = new List<KeyValuePair<string, ItemStatsSystem.Item>>();
            foreach (var slot in item.Slots)
            {
                if (slot == null || slot.Content == null)
                    continue;
                slotContents.Add(new KeyValuePair<string, ItemStatsSystem.Item>(
                    slot.Key ?? string.Empty, slot.Content));
            }
            if (slotContents.Count > 255)
                throw new InvalidOperationException("too many slots");
            writer.Write((byte)slotContents.Count);
            foreach (var pair in slotContents)
            {
                var key = Encoding.UTF8.GetBytes(pair.Key);
                if (key.Length > 255)
                    throw new InvalidOperationException("slot key too long");
                writer.Write((byte)key.Length);
                writer.Write(key);
                WriteAiItem(writer, pair.Value, depth + 1, skipVolatile);
            }
        }

        /// <summary>
        /// Variables that change during normal play. They never affect how a mirrored item
        /// looks, so the V8 player snapshot drops them to keep the per-slot fingerprint
        /// stable while firing/reloading (static markers like IsGun stay).
        /// </summary>
        private static bool IsVolatileVariableKey(string key)
        {
            return key == "Count"
                || key == "Durability"
                || key == "BulletCount"
                || key == "CurrentEnergy"
                || key == "CurrentWater";
        }

        private static ItemStatsSystem.Item DeserializeAiItem(byte[] blob)
        {
            if (blob == null || blob.Length == 0)
                return null;
            try
            {
                using (var stream = new MemoryStream(blob))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                    return ReadAiItem(reader, 0);
            }
            catch
            {
                return null;
            }
        }

        private static ItemStatsSystem.Item ReadAiItem(BinaryReader reader, int depth)
        {
            if (depth > 6)
                throw new InvalidOperationException("item tree too deep");
            var typeId = reader.ReadInt32();
            var item = ItemStatsSystem.ItemAssetsCollection.InstantiateSync(typeId);
            if (item == null)
                throw new InvalidOperationException("missing item type " + typeId);

            var variableCount = reader.ReadByte();
            for (var i = 0; i < variableCount; i++)
            {
                var keyLength = reader.ReadByte();
                var key = Encoding.UTF8.GetString(reader.ReadBytes(keyLength));
                var dataType = (Duckov.Utilities.CustomDataType)reader.ReadByte();
                var rawLength = reader.ReadUInt16();
                var raw = reader.ReadBytes(rawLength);
                item.Variables.SetRaw(key, dataType, raw);
            }

            var slotCount = reader.ReadByte();
            for (var i = 0; i < slotCount; i++)
            {
                var keyLength = reader.ReadByte();
                var key = Encoding.UTF8.GetString(reader.ReadBytes(keyLength));
                var child = ReadAiItem(reader, depth + 1);
                var slot = item.Slots.GetSlot(key);
                if (slot != null && child != null)
                    slot.Plug(child, out _);
            }
            return item;
        }

        private static int ComputeLoadoutSignature(AiLoadoutV7 loadout)
        {
            var hash = 17;
            hash = unchecked(hash * 31 + loadout.ActiveSlot);
            foreach (var slot in loadout.Slots)
            {
                hash = unchecked(hash * 31 + slot.Slot);
                foreach (var value in slot.Blob)
                    hash = unchecked(hash * 31 + value);
            }
            return hash;
        }

        /// <summary>Non-owner: rebuild host item snapshots, including attachments.</summary>
        private bool ApplyHostAiLoadoutV7(uint aiId, CharacterMainControl character)
        {
            if (character == null || _client == null)
                return false;
            if (!_client.AiLoadoutSnapshots.TryGetValue(aiId, out var loadout) || loadout == null)
                return false;
            if (loadout.RaidEpoch != 0 && _aiSpawnEpoch != 0 &&
                loadout.RaidEpoch != _aiSpawnEpoch)
                return false;
            if (loadout.Slots.Count == 0)
                return false;

            var signature = ComputeLoadoutSignature(loadout);
            if (_aiV7LoadoutApplied.TryGetValue(aiId, out var applied) && applied == signature)
                return true;

            var items = new Dictionary<byte, ItemStatsSystem.Item>();
            foreach (var slot in loadout.Slots)
            {
                if (slot.Blob == null || slot.Blob.Length == 0)
                {
                    if (slot.Slot == 1 || slot.Slot == 2)
                        ClearEquipmentSlot(character,
                            slot.Slot == 1
                                ? CharacterEquipmentController.armorHash
                                : CharacterEquipmentController.helmatHash);
                    continue;
                }
                var item = DeserializeAiItem(slot.Blob);
                if (item == null)
                {
                    Log($"[v7] loadout decode failed ai={aiId} slot={slot.Slot}");
                    continue;
                }
                items[slot.Slot] = item;
                if (slot.Slot == 0 || slot.Slot == 3)
                {
                    if (character.CharacterItem != null)
                        ItemUtilities.TryPlug(character.CharacterItem, item);
                }
                else if (slot.Slot == 1 || slot.Slot == 2)
                {
                    var hash = slot.Slot == 1
                        ? CharacterEquipmentController.armorHash
                        : CharacterEquipmentController.helmatHash;
                    var equipmentSlot = character.CharacterItem != null
                        ? character.CharacterItem.Slots.GetSlot(hash)
                        : null;
                    if (equipmentSlot != null)
                        equipmentSlot.Plug(item, out _);
                }
            }

            ItemStatsSystem.Item hold = null;
            if (items.TryGetValue(loadout.ActiveSlot, out var activeItem))
                hold = activeItem;
            else if (items.TryGetValue(0, out var primary))
                hold = primary;
            if (hold != null && TryHoldItem(character, hold))
            {
                _aiV7LoadoutApplied[aiId] = signature;
                if (_aiV7LoadoutLogged.Add(aiId))
                    Log($"[v7] loadout apply ai={aiId} slots={items.Count} active={loadout.ActiveSlot} " +
                        $"hold={hold.TypeID}");
                return true;
            }
            return false;
        }

        private readonly HashSet<uint> _aiTracerLogged = new HashSet<uint>();
        // M2-v12: live visual bullets, so a remote impact can retire the matching one.
        private readonly List<AiVisualBullet> _visualBullets = new List<AiVisualBullet>();

        /// <summary>
        /// Spawn a visual-only native bullet. The native Projectile logic is disabled so
        /// B never settles damage; only its mesh/trail is moved along the host shot.
        /// </summary>
        private void SpawnAiBullet(uint aiId, Vector3 origin, Vector3 direction, ItemAgent_Gun gun)
        {
            try
            {
                direction.Normalize();
                var speed = gun != null ? Mathf.Clamp(gun.BulletSpeed, 20f, 400f) : 80f;
                var prefab = gun != null && gun.GunItemSetting != null
                    ? gun.GunItemSetting.bulletPfb
                    : null;

                if (prefab != null)
                {
                    var bullet = UnityEngine.Object.Instantiate(
                        prefab, origin, Quaternion.LookRotation(direction, Vector3.up));
                    var logic = bullet.GetComponent<Projectile>();
                    if (logic != null)
                    {
                        logic.enabled = false;
                        try
                        {
                            logic.Init(); // clear trails / choose color, no damage
                        }
                        catch
                        {
                        }
                    }
                    foreach (var collider in bullet.GetComponentsInChildren<Collider>(true))
                        collider.enabled = false;
                    foreach (var body in bullet.GetComponentsInChildren<Rigidbody>(true))
                    {
                        body.isKinematic = true;
                        body.detectCollisions = false;
                    }
                    var mover = bullet.gameObject.AddComponent<AiVisualBullet>();
                    mover.Velocity = direction * speed;
                    mover.Life = 1.2f;
                    TrackVisualBullet(mover, aiId, origin);
                }
                else
                {
                    var bullet = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    bullet.name = "CoopAiVisualBullet";
                    bullet.transform.position = origin;
                    bullet.transform.localScale = Vector3.one * 0.08f;
                    var collider = bullet.GetComponent<Collider>();
                    if (collider != null)
                        collider.enabled = false;
                    var renderer = bullet.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
                        if (shader != null)
                            renderer.material = new Material(shader) { color = new Color(1f, 0.8f, 0.2f) };
                    }
                    var mover = bullet.AddComponent<AiVisualBullet>();
                    mover.Velocity = direction * speed;
                    mover.Life = 1.2f;
                    TrackVisualBullet(mover, aiId, origin);
                }

                if (_aiTracerLogged.Add(aiId))
                {
                    // Player shots reuse this presenter with 0x80000000 | playerId, which
                    // used to print as a nonsensical "ai=2147483657" during log triage.
                    var who = (aiId & 0x80000000u) != 0
                        ? $"player={aiId & 0x7FFFFFFFu}"
                        : $"ai={aiId}";
                    Log($"[v7] bullet {who} from=({origin.x:F1},{origin.y:F1},{origin.z:F1}) " +
                        $"dir=({direction.x:F2},{direction.y:F2},{direction.z:F2}) speed={speed:F0}");
                }
            }
            catch (Exception ex)
            {
                if (_aiTracerLogged.Add(aiId))
                    Log($"[v7] bullet failed ai={aiId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void TrackVisualBullet(AiVisualBullet mover, uint aiId, Vector3 origin)
        {
            var isPlayer = (aiId & 0x80000000u) != 0;
            mover.Origin = origin;
            mover.SourceKind = isPlayer ? (byte)0 : (byte)1;
            mover.SourceId = aiId & 0x7FFFFFFFu;
            _visualBullets.Add(mover);
        }

        private static void ClearEquipmentSlot(CharacterMainControl character, int slotHash)
        {
            try
            {
                var item = character != null ? character.CharacterItem : null;
                var slot = item != null ? item.Slots.GetSlot(slotHash) : null;
                if (slot != null && slot.Content != null)
                    slot.Unplug();
            }
            catch
            {
                // visual cleanup is best effort
            }
        }

        private void ClearAiV7State(uint aiId)
        {
            _aiStateSampleSeq.Remove(aiId);
            _aiStateSampleSeen.Remove(aiId);
            _aiStateSampleAt.Remove(aiId);
            _aiStateSampleNext.Remove(aiId);
            _aiStateSampleLogged.Remove(aiId);
            _aiAppliedStateHash.Remove(aiId);
            _aiShotSeq.Remove(aiId);
            _aiShotSeen.Remove(aiId);
            _aiShotLogged.Remove(aiId);
            _aiV7LoadoutSignature.Remove(aiId);
            _aiV7LoadoutApplied.Remove(aiId);
            _aiV7LoadoutLogged.Remove(aiId);
            _aiV7LoadoutNext.Remove(aiId);
            _aiStateHashWarned.Remove(aiId);
            _aiTracerLogged.Remove(aiId);
        }
    }

    /// <summary>Visual-only linear mover for a native AI bullet prefab.</summary>
    public sealed class AiVisualBullet : MonoBehaviour
    {
        public Vector3 Velocity;
        public float Life = 1.2f;
        // M2-v12: identify the shooter so a remote SImpactFx can stop the right bullet.
        public Vector3 Origin;
        public byte SourceKind;
        public uint SourceId;

        private void Update()
        {
            transform.position += Velocity * Time.deltaTime;
            Life -= Time.deltaTime;
            if (Life <= 0f)
                Destroy(gameObject);
        }
    }
}
