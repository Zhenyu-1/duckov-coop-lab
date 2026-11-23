using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using DuckovCoop.Client;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// ADR-025 / wire v8: complete remote-player presentation.
    /// Host captures local animator state, face, per-slot item snapshots and shots;
    /// B applies them to the passive remote-player mirror.
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private const byte PlayerFlagValid = 1 << 0;
        private const byte PlayerFlagTransition = 1 << 1;
        private const byte PlayerFlagAttacking = 1 << 2;
        private const byte PlayerFlagReloading = 1 << 3;
        private const byte PlayerFlagDashing = 1 << 4;
        private const byte PlayerFlagAiming = 1 << 5;
        private const byte PlayerShotFlagMelee = 1 << 0;
        /// <summary>
        /// M2-v8.1 / DQ-1: demo players all keep the default duck face. The send path is off;
        /// the receive path stays so an older server's broadcast is still harmless.
        /// (readonly rather than const so the disabled body does not become CS0162 noise.)
        /// </summary>
        private static readonly bool EnableFaceSync = false;

        private ushort _playerStateSeq;
        private byte _playerAttackSeq;
        private uint _playerShotSeq;
        private float _playerAttackUntil;
        private float _nextPlayerStateSend;
        private float _nextPlayerLoadoutSend;
        private const float LoadoutHeartbeatSeconds = 3f;
        private readonly ulong[] _sentSlotHashes = new ulong[8];
        private readonly bool[] _sentSlotValid = new bool[8];
        private bool _sentActiveSlotValid;
        private byte _sentActiveSlot;
        private float _nextLoadoutHeartbeat;
        private int _sentPlayerFaceSig;
        private bool _playerFaceTooLargeLogged;
        private bool _playerMeleeSendLogged;
        private bool _playerGunSendLogged;

        private readonly Dictionary<ushort, ushort> _playerStateSeen =
            new Dictionary<ushort, ushort>();
        private readonly Dictionary<ushort, float> _playerStateAt =
            new Dictionary<ushort, float>();
        private readonly Dictionary<ushort, byte> _playerAttackSeen =
            new Dictionary<ushort, byte>();
        private readonly Dictionary<ushort, float> _playerAttackBlockedUntil =
            new Dictionary<ushort, float>();
        private readonly Dictionary<ushort, uint> _playerStateHash =
            new Dictionary<ushort, uint>();
        private readonly Dictionary<ushort, uint> _playerShotSeen =
            new Dictionary<ushort, uint>();
        // T5: NotePlayerShot bumps attackSeq too, so the 10Hz state sample would replay the
        // same attack ~100ms after the shot already did. A shot within 0.3s wins.
        private readonly Dictionary<ushort, float> _lastPlayerShotAt =
            new Dictionary<ushort, float>();
        private readonly Dictionary<ulong, ulong> _appliedSlotHash =
            new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, ulong> _slotIssueLogged =
            new Dictionary<ulong, ulong>();
        private readonly Dictionary<ushort, byte> _appliedActiveSlot =
            new Dictionary<ushort, byte>();
        // M2-v12: the item currently pushed into the mirror. A slot re-plug replaces the Item
        // instance without changing the active slot index, so the index alone cannot tell us
        // when to re-hold (the mirror would keep a stale, already-unplugged weapon).
        private readonly Dictionary<ushort, ItemStatsSystem.Item> _mirrorHeldItem =
            new Dictionary<ushort, ItemStatsSystem.Item>();
        private readonly HashSet<ushort> _mirrorHoldFailed = new HashSet<ushort>();
        private readonly Dictionary<ushort, ulong> _appliedFaceHash =
            new Dictionary<ushort, ulong>();
        private readonly HashSet<ushort> _appliedDefaultFace = new HashSet<ushort>();
        private readonly Dictionary<ushort, ItemStatsSystem.Items.Slot[]> _remoteSlotCache =
            new Dictionary<ushort, ItemStatsSystem.Items.Slot[]>();
        // T7c diagnostic: every 5s show what the apply path actually sees for a remote.
        private readonly Dictionary<ushort, float> _v8ProbeAt =
            new Dictionary<ushort, float>();
        private readonly HashSet<ushort> _playerV8Logged = new HashSet<ushort>();
        private readonly HashSet<ushort> _playerShotLogged = new HashSet<ushort>();
        private readonly Dictionary<ushort, int> _playerAttackCount =
            new Dictionary<ushort, int>();
        private int _mirrorShotFxLogged;

        private void UpdatePlayerV8Sampling()
        {
            if (!_inRaidScene || _client == null || _client.PlayerId == 0)
                return;
            var main = CharacterMainControl.Main;
            if (main == null)
                main = LevelManager.Instance != null ? LevelManager.Instance.MainCharacter : null;
            if (main == null || main.characterModel == null)
                return;

            SendPlayerStateSampleIfDue(main);
            if (Time.unscaledTime >= _nextPlayerLoadoutSend)
            {
                _nextPlayerLoadoutSend = Time.unscaledTime + 0.5f;
                SendPlayerLoadoutV8IfChanged(main);
                SendPlayerFaceV8IfChanged(main);
            }
        }

        private void SendPlayerStateSampleIfDue(CharacterMainControl main)
        {
            if (Time.unscaledTime < _nextPlayerStateSend)
                return;

            var animator = ResolvePlayerAnimator(main);
            if (animator == null)
                return;

            try
            {
                var state = animator.GetCurrentAnimatorStateInfo(0);
                var norm = state.normalizedTime;
                norm -= Mathf.Floor(norm);
                byte flags = PlayerFlagValid;
                if (animator.IsInTransition(0))
                    flags |= PlayerFlagTransition;
                if (Time.time < _playerAttackUntil)
                    flags |= PlayerFlagAttacking;
                if (HasParam(animator, "Reloading") && animator.GetBool("Reloading"))
                    flags |= PlayerFlagReloading;
                if (HasParam(animator, "Dashing") && animator.GetBool("Dashing"))
                    flags |= PlayerFlagDashing;
                if (HasParam(animator, "GunReady") && animator.GetBool("GunReady"))
                    flags |= PlayerFlagAiming;

                _nextPlayerStateSend = Time.unscaledTime + 0.1f;
                var sample = new PlayerStateSample
                {
                    RaidEpoch = _client.RaidEpoch,
                    Seq = ++_playerStateSeq,
                    StateHash = unchecked((uint)state.shortNameHash),
                    NormTime = (ushort)Mathf.Clamp(Mathf.RoundToInt(norm * 65535f), 0, 65535),
                    Flags = flags,
                    AttackSeq = _playerAttackSeq,
                    HandState = (byte)Mathf.Clamp(
                        HasParam(animator, "HandState") ? animator.GetInteger("HandState") : 0,
                        0, 255),
                };
                _client.SendPlayerStateSample(sample);
            }
            catch
            {
                // best-effort presentation
            }
        }

        private static Animator ResolvePlayerAnimator(CharacterMainControl main)
        {
            try
            {
                var model = main.characterModel;
                var magic = model.GetComponent<CharacterAnimationControl_MagicBlend>();
                if (magic != null && magic.animator != null)
                    return magic.animator;
                var basic = model.GetComponent<CharacterAnimationControl>();
                if (basic != null && basic.animator != null)
                    return basic.animator;
            }
            catch
            {
                // fall through
            }
            return main.GetComponentInChildren<Animator>(true);
        }

        private void SendPlayerLoadoutV8IfChanged(CharacterMainControl main)
        {
            var slots = GetRemoteSlots(main);
            var blobs = new byte[8][];
            var types = new int[8];
            var hashes = new ulong[8];
            for (byte i = 0; i < 8; i++)
            {
                var item = slots[i] != null ? slots[i].Content : null;
                types[i] = item != null ? item.TypeID : 0;
                // M2-v8.1: ammo/durability/count are volatile and never affect the mirror's
                // appearance. Keeping them made the blob change on every shot, so the whole
                // 8-slot package was re-sent every 0.5s with identical types.
                blobs[i] = item != null
                    ? SerializeAiItem(item, 256, skipVolatile: true)
                    : Array.Empty<byte>();
                if (blobs[i] == null)
                    blobs[i] = Array.Empty<byte>();
                hashes[i] = Fnv1a(blobs[i]);
            }

            var held = main.agentHolder != null ? main.agentHolder.CurrentHoldItemAgent : null;
            var heldItem = held != null ? held.Item : null;
            var active = (byte)0;
            if (heldItem != null)
            {
                for (byte i = 0; i < 3; i++)
                {
                    if (types[i] != 0 && types[i] == heldItem.TypeID)
                    {
                        active = i;
                        break;
                    }
                }
            }

            var changed = new List<byte>(8);
            for (byte i = 0; i < 8; i++)
            {
                if (!_sentSlotValid[i] || _sentSlotHashes[i] != hashes[i])
                    changed.Add(i);
            }

            var now = Time.unscaledTime;
            string why;
            if (changed.Count > 0)
            {
                foreach (var slot in changed)
                    SendPlayerLoadoutSlot(slot, active, blobs[slot]);
                foreach (var slot in changed)
                {
                    _sentSlotHashes[slot] = hashes[slot];
                    _sentSlotValid[slot] = true;
                }
                _sentActiveSlot = active;
                _sentActiveSlotValid = true;
                _nextLoadoutHeartbeat = now + LoadoutHeartbeatSeconds;
                why = string.Join(",", changed);
            }
            else if (!_sentActiveSlotValid || _sentActiveSlot != active)
            {
                // Switching weapons does not change slot contents: one message carries the
                // new held slot and the (unchanged) blob of that slot.
                SendPlayerLoadoutSlot(active, active, blobs[active]);
                _sentActiveSlot = active;
                _sentActiveSlotValid = true;
                _nextLoadoutHeartbeat = now + LoadoutHeartbeatSeconds;
                why = "active";
            }
            else if (now >= _nextLoadoutHeartbeat)
            {
                // Best-effort baseline: appearance may be eventually consistent, so a full
                // resend every 3s covers a lost reliable burst or an apply-side retry.
                for (byte i = 0; i < 8; i++)
                {
                    SendPlayerLoadoutSlot(i, active, blobs[i]);
                    _sentSlotHashes[i] = hashes[i];
                    _sentSlotValid[i] = true;
                }
                _nextLoadoutHeartbeat = now + LoadoutHeartbeatSeconds;
                why = "heartbeat";
            }
            else
            {
                return;
            }

            Log($"[v8] player loadout send epoch={_client.RaidEpoch} active={active} " +
                $"changed={why} types={string.Join(",", types)}");
        }

        private void SendPlayerLoadoutSlot(byte slot, byte active, byte[] blob)
        {
            _client.SendPlayerLoadoutV8(new PlayerLoadoutV8
            {
                RaidEpoch = _client.RaidEpoch,
                ActiveSlot = active,
                Slot = slot,
                Blob = blob,
            });
        }

        private void SendPlayerFaceV8IfChanged(CharacterMainControl main)
        {
            if (!EnableFaceSync)
                return;
            try
            {
                var face = main.characterModel != null ? main.characterModel.CustomFace : null;
                if (face == null)
                    return;
                var json = face.ConvertToSaveData().DataToJson();
                var bytes = Encoding.UTF8.GetBytes(json);
                if (bytes.Length > 1024)
                {
                    if (!_playerFaceTooLargeLogged)
                    {
                        _playerFaceTooLargeLogged = true;
                        Log($"[v8] player face too large bytes={bytes.Length}; skipped");
                    }
                    return;
                }
                var signature = 23;
                foreach (var value in bytes)
                    signature = unchecked(signature * 31 + value);
                if (signature == _sentPlayerFaceSig)
                    return;
                _sentPlayerFaceSig = signature;
                _client.SendPlayerFaceV8(new PlayerFaceV8
                {
                    RaidEpoch = _client.RaidEpoch,
                    Face = bytes,
                });
                Log($"[v8] player face send bytes={bytes.Length}");
            }
            catch (Exception ex)
            {
                Log($"[v8] player face sample failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal void NotePlayerShot(ItemAgent_Gun gun, CharacterMainControl holder,
            Vector3 muzzlePoint, Vector3 direction)
        {
            if (gun == null || holder == null || _client == null ||
                _client.State != ClientState.InGame || !_inRaidScene)
                return;
            if (holder != CharacterMainControl.Main && !holder.IsMainCharacter)
                return;
            if (direction.sqrMagnitude < 0.0001f)
                return;
            direction.Normalize();

            _playerAttackSeq++;
            _playerAttackUntil = Time.time + 0.35f;
            var shot = new PlayerShot
            {
                RaidEpoch = _client.RaidEpoch,
                ShotId = ++_playerShotSeq,
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
            _client.SendPlayerShot(shot);
            if (!_playerGunSendLogged)
            {
                _playerGunSendLogged = true;
                Log($"[v8] player gun send weapon={shot.WeaponId} ammo={shot.BulletId}");
            }
        }

        internal void NotePlayerMelee(CharacterMainControl holder)
        {
            if (holder == null)
                return;
            if (holder != CharacterMainControl.Main && !holder.IsMainCharacter)
                return;
            var agent = holder.CurrentHoldItemAgent;
            if (!(agent is ItemAgent_MeleeWeapon))
                return;
            _playerAttackSeq++;
            _playerAttackUntil = Time.time + 0.35f;

            var item = agent != null ? agent.Item : null;
            var forward = holder.transform.forward;
            _client.SendPlayerShot(new PlayerShot
            {
                RaidEpoch = _client.RaidEpoch,
                ShotId = ++_playerShotSeq,
                WeaponId = item != null ? (uint)item.TypeID : 0u,
                BulletId = 0u,
                OriginX = Mathf.RoundToInt(holder.transform.position.x * 100f),
                OriginY = Mathf.RoundToInt(holder.transform.position.y * 100f),
                OriginZ = Mathf.RoundToInt(holder.transform.position.z * 100f),
                DirX = (short)Mathf.Clamp(Mathf.RoundToInt(forward.x * 32767f), short.MinValue, short.MaxValue),
                DirY = (short)Mathf.Clamp(Mathf.RoundToInt(forward.y * 32767f), short.MinValue, short.MaxValue),
                DirZ = (short)Mathf.Clamp(Mathf.RoundToInt(forward.z * 32767f), short.MinValue, short.MaxValue),
                Seed = 0u,
                Flags = PlayerShotFlagMelee,
            });
            if (!_playerMeleeSendLogged)
            {
                _playerMeleeSendLogged = true;
                Log($"[v8] player melee send weapon={(item != null ? item.TypeID : 0)}");
            }
        }

        private void ApplyPlayerStateSample(Animator animator, ushort playerId)
        {
            if (animator == null || _client == null)
                return;
            if (!_client.PlayerStateSamples.TryGetValue(playerId, out var sample) || sample == null)
                return;
            if (sample.RaidEpoch != 0 && _client.RaidEpoch != 0 &&
                sample.RaidEpoch != _client.RaidEpoch)
                return;
            if (!_playerStateSeen.TryGetValue(playerId, out var seen) || seen != sample.Seq)
            {
                _playerStateSeen[playerId] = sample.Seq;
                _playerStateAt[playerId] = Time.unscaledTime;
            }
            if (Time.unscaledTime - (_playerStateAt.TryGetValue(playerId, out var at) ? at : 0f) > 0.5f)
                return;

            try
            {
                if ((sample.Flags & PlayerFlagValid) != 0)
                {
                    var hash = unchecked((int)sample.StateHash);
                    var attackingNow = _playerAttackBlockedUntil.TryGetValue(playerId, out var blockUntil) &&
                                       Time.unscaledTime < blockUntil;
                    if (!attackingNow && animator.HasState(0, hash) &&
                        (!_playerStateHash.TryGetValue(playerId, out var appliedHash) ||
                         appliedHash != sample.StateHash))
                    {
                        _playerStateHash[playerId] = sample.StateHash;
                        var norm = sample.NormTime / 65535f;
                        animator.Play(hash, 0, Mathf.Clamp01(norm));
                    }
                }
                var stateControl = animator.GetComponentInParent<CharacterMainControl>();
                var holdingGun = stateControl != null &&
                                 stateControl.GetComponentInChildren<ItemAgent_Gun>(true) != null;
                if (HasParam(animator, "HandState"))
                    animator.SetInteger("HandState", sample.HandState);
                SetBoolIfExists(animator, "Reloading", (sample.Flags & PlayerFlagReloading) != 0);
                SetBoolIfExists(animator, "Dashing", (sample.Flags & PlayerFlagDashing) != 0);
                SetBoolIfExists(animator, "GunReady",
                    (sample.Flags & PlayerFlagAiming) != 0 || holdingGun);
                if (!_playerAttackSeen.TryGetValue(playerId, out var attackSeq) ||
                    attackSeq != sample.AttackSeq)
                {
                    _playerAttackSeen[playerId] = sample.AttackSeq;
                    // The shot-driven path already produced this attack's muzzle FX/sound:
                    // do not trigger it a second time from the state sample. Keep the seq
                    // bookkeeping above either way so a lost shot still has a fallback once
                    // the 0.3s window has passed.
                    if (!_lastPlayerShotAt.TryGetValue(playerId, out var lastShotAt) ||
                        Time.unscaledTime - lastShotAt >= 0.3f)
                        TriggerPlayerAttack(playerId, false);
                }
            }
            catch (Exception ex)
            {
                if (_playerV8Logged.Add(playerId))
                    Log($"[v8] player state apply failed id={playerId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void TriggerPlayerAttack(ushort playerId, bool melee)
        {
            if (!_remotes.TryGetValue(playerId, out var remote) || remote == null)
                return;
            var animator = FindBodyAnimator(remote);
            if (animator == null)
                return;
            var control = remote.GetComponentInChildren<CharacterMainControl>(true);
            var agent = control != null ? control.CurrentHoldItemAgent : null;
            var isMelee = melee || agent is ItemAgent_MeleeWeapon;

            // Melee: play the attack clip by direct sampling (the controller's layer 1
            // reports the state/clip at weight 1 but contributes no pose).
            // Guns: the native character has NO body animation when firing (measured on
            // the local player: 0.0 deg on every arm bone), so the visible cues are the
            // muzzle flash + a short weapon kick instead of a body clip.
            var driver = animator.GetComponent<PlayerMirrorAttackAnim>();
            var nativeAttack = remote.GetComponentInChildren<CharacterAnimationControl_MagicBlend>(true);
            if (nativeAttack != null)
            {
                nativeAttack.characterMainControl = control;
                nativeAttack.characterModel = control != null ? control.characterModel : null;
                nativeAttack.animator = animator;
                if (nativeAttack.attackLayerWeightCurve == null)
                    nativeAttack.attackLayerWeightCurve = AnimationCurve.Linear(0f, 1f, 0.3f, 0f);
                nativeAttack.OnAttack();
            }

            if (isMelee)
            {
                if (driver == null)
                    driver = animator.gameObject.AddComponent<PlayerMirrorAttackAnim>();
                driver.Animator = animator;
                driver.Trigger(playerId);
                var weaponPosition = agent != null ? agent.transform.position
                    : control != null ? control.transform.position : Vector3.zero;
                PlayMirrorMeleeSound(agent, weaponPosition);
                if (agent != null)
                    PlayMirrorMeleeFx(agent, weaponPosition);
            }
            else
            {
                PlayMirrorShotFx(control, playerId);
            }
            _playerAttackBlockedUntil[playerId] = Time.unscaledTime + 0.4f;

            var layer = animator.GetLayerIndex("MeleeAttack");
            _playerAttackCount.TryGetValue(playerId, out var attackCount);
            attackCount++;
            _playerAttackCount[playerId] = attackCount;
            if (attackCount <= 24)
                Log($"[v8] player attack n={attackCount} id={playerId} melee={isMelee} " +
                    $"weapon={(agent != null && agent.Item != null ? agent.Item.TypeID : 0)} " +
                    $"layer={layer} weight={(layer >= 0 ? animator.GetLayerWeight(layer) : -1f):F2}");
        }


        /// <summary>Pick the body armature animator (has UpperArm.R), not the face animator.</summary>
        internal static Animator FindBodyAnimator(GameObject remote)
        {
            if (remote == null)
                return null;
            var control = remote.GetComponentInChildren<CharacterMainControl>(true);
            var roots = new List<Transform>(2);
            if (control != null && control.characterModel != null)
                roots.Add(control.characterModel.transform);
            roots.Add(remote.transform);
            foreach (var root in roots)
            {
                foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                {
                    if (animator == null)
                        continue;
                    foreach (var bone in animator.GetComponentsInChildren<Transform>(true))
                    {
                        if (bone != null && bone.name == "UpperArm.R")
                            return animator;
                    }
                }
            }
            return remote.GetComponentInChildren<Animator>(true);
        }

        /// <summary>Attach the aim-pose overlay to a (re)built mirror.</summary>
        private void EnsureMirrorAimPose(ushort playerId, GameObject remote)
        {
            try
            {
                if (remote == null)
                    return;
                var control = remote.GetComponentInChildren<CharacterMainControl>(true);
                // Native characters now keep their CharacterAnimationControl, which drives
                // the gun pose from HandState/GunReady. The procedural pose would re-aim the
                // socket a frame before the body yaw, making the muzzle lead the body.
                if (control != null)
                {
                    foreach (var behaviour in control.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (behaviour != null && behaviour.GetType().Name.StartsWith(
                                "CharacterAnimation", StringComparison.Ordinal))
                        {
                            Log($"[v8] mirror procedural pose skipped id={playerId} " +
                                $"native={behaviour.GetType().Name}");
                            return;
                        }
                    }
                }
                Animator animator = FindBodyAnimator(remote);
                if (animator == null)
                    return;
                var pose = animator.GetComponent<MirrorAimPose>();
                if (pose == null)
                    pose = animator.gameObject.AddComponent<MirrorAimPose>();
                pose.Init(playerId, animator, control);
                // Bone/parameter copying from the local player caused B's mirror to follow
                // A's movement; the sample now carries HandState and GunReady, so let the
                // native animator drive the body.
                Log($"[v8] mirror aim pose attached id={playerId} animator={animator.name} " +
                    $"active={animator.gameObject.activeInHierarchy} control={(control != null)}");
            }
            catch (Exception ex)
            {
                Log($"[v8] mirror aim pose attach failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Muzzle resolution for a mirror weapon: a child named like a muzzle, else a
        /// Transform/GameObject field named like one, else null (caller falls back to the
        /// front of the weapon bounds).
        /// </summary>
        private static Transform ResolveMuzzleTransform(ItemStatsSystem.ItemAgent agent,
            ref string source)
        {
            var root = agent.transform;
            foreach (var candidate in root.GetComponentsInChildren<Transform>(true))
            {
                if (candidate == null)
                    continue;
                var name = candidate.name.ToLowerInvariant();
                if (name.Contains("muzzle") || name.Contains("firepoint") ||
                    name.Contains("barrelend"))
                {
                    source = "child";
                    return candidate;
                }
            }

            var type = agent.GetType();
            while (type != null)
            {
                foreach (var field in type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.FieldType != typeof(Transform) && field.FieldType != typeof(GameObject))
                        continue;
                    if (field.Name.IndexOf("muzzle", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var value = field.GetValue(agent);
                    var transform = value as Transform;
                    if (transform != null)
                    {
                        source = "field:" + field.Name;
                        return transform;
                    }

                    var gameObject = value as GameObject;
                    if (gameObject != null)
                    {
                        source = "field:" + field.Name;
                        return gameObject.transform;
                    }
                }

                type = type.BaseType;
            }

            source = "none";
            return null;
        }

        /// <summary>Front-most point of the weapon mesh along the barrel axis.</summary>
        private static Vector3 BoundsMuzzlePosition(ItemStatsSystem.ItemAgent agent)
        {
            var forward = agent.transform.forward;
            var hasBounds = false;
            var bounds = new Bounds();
            foreach (var renderer in agent.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                    continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds)
                return agent.transform.position;

            var center = bounds.center;
            var extent = 0f;
            var min = bounds.min;
            var max = bounds.max;
            for (var x = 0; x < 2; x++)
            {
                for (var y = 0; y < 2; y++)
                {
                    for (var z = 0; z < 2; z++)
                    {
                        var corner = new Vector3(
                            x == 0 ? min.x : max.x,
                            y == 0 ? min.y : max.y,
                            z == 0 ? min.z : max.z);
                        var dot = Vector3.Dot(corner - center, forward);
                        if (dot > extent)
                            extent = dot;
                    }
                }
            }

            return center + forward * extent;
        }

        /// <summary>
        /// Gunfire has no character-body animation in the native game, so the mirror
        /// replays the shooter's visible cues: the gun's own muzzle flash prefab plus a
        /// short recoil kick on the weapon model.
        /// </summary>
        private void PlayMirrorShotFx(CharacterMainControl control, ushort playerId)        {
            try
            {
                var agent = control != null ? control.CurrentHoldItemAgent : null;
                if (agent == null || !agent.GetType().Name.Contains("Gun"))
                {
                    if (_mirrorShotFxLogged < 3)
                    {
                        _mirrorShotFxLogged++;
                        Log($"[v8] mirror shot fx skipped id={playerId} agent=" +
                            $"{(agent != null ? agent.GetType().Name : "<null>")}");
                    }

                    return;
                }

                var muzzleSource = "none";
                var muzzleTransform = ResolveMuzzleTransform(agent, ref muzzleSource);
                var setting = agent.GetType()
                    .GetField("_gunItemSetting",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    ?.GetValue(agent);
                var fx = setting?.GetType()
                    .GetField("muzzleFxPfb",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(setting) as GameObject;

                // The AK47 model has no "Muzzle" child, so the flash/sound position falls
                // back to the front of the weapon bounds instead of the gun root (which
                // sits in the hand).
                var position = muzzleTransform != null
                    ? muzzleTransform.position
                    : BoundsMuzzlePosition(agent);
                if (muzzleTransform == null)
                    muzzleSource = "bounds";
                var rotation = muzzleTransform != null
                    ? muzzleTransform.rotation
                    : agent.transform.rotation;

                if (fx != null)
                {
                    var instance = UnityEngine.Object.Instantiate(fx, position, rotation);
                    if (instance != null)
                        UnityEngine.Object.Destroy(instance, 1f);
                }

                var shotEvent = PlayMirrorShotSound(agent, setting, position,
                    agent.Item != null ? (uint)agent.Item.TypeID : 0u);

                // Kick the whole weapon model (all its renderer children move together),
                // not a single mesh child.
                var recoilTarget = agent.transform;
                var recoil = recoilTarget.GetComponent<MirrorGunRecoil>();
                if (recoil == null)
                    recoil = recoilTarget.gameObject.AddComponent<MirrorGunRecoil>();
                recoil.Play(muzzleTransform != null ? muzzleTransform : agent.transform);

                if (_mirrorShotFxLogged < 3)
                {
                    _mirrorShotFxLogged++;
                    Log($"[v8] mirror shot fx id={playerId} weapon=" +
                        $"{(agent.Item != null ? agent.Item.TypeID : 0)} " +
                        $"fx={(fx != null ? fx.name : "<null>")} " +
                        $"muzzle={(muzzleTransform != null ? muzzleTransform.name : "<bounds>")} " +
                        $"muzzleSource={muzzleSource} " +
                        $"sound={(string.IsNullOrEmpty(shotEvent) ? "<none>" : shotEvent)} " +
                        $"recoilTarget={recoilTarget.name}");
                }
            }
            catch (Exception ex)
            {
                if (_mirrorShotFxLogged < 3)
                {
                    _mirrorShotFxLogged++;
                    Log($"[v8] mirror shot fx failed id={playerId}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private bool ApplyRemotePlayerV8(ushort playerId, GameObject remote)
        {
            if (remote == null || _client == null)
                return false;
            // V8 is the in-raid presentation contract. The base scene uses the legacy
            // SLoadout path; applying a raid V8 after extraction showed the other player in
            // raid gear in the lobby.
            if (!_inRaidScene)
                return false;
            var control = remote.GetComponentInChildren<CharacterMainControl>(true);
            if (control == null || control.CharacterItem == null)
                return false;
            if (_client.RaidEpoch == 0)
                return false;

            if (!_remoteSlotCache.TryGetValue(playerId, out var slots) || slots == null)
            {
                slots = GetRemoteSlots(control);
                _remoteSlotCache[playerId] = slots;
            }

            var sawMessage = false;
            var sawCurrentEpoch = false;
            var applied = 0;
            var skipped = 0;
            for (byte slot = 0; slot < slots.Length; slot++)
            {
                var key = ((ulong)playerId << 8) | slot;
                if (!_client.PlayerLoadoutsV8.TryGetValue(key, out var message) || message == null)
                    continue; // never received: keep whatever the mirror already has
                sawMessage = true;
                if (message.RaidEpoch != _client.RaidEpoch)
                    continue; // stale raid
                sawCurrentEpoch = true;

                var hash = Fnv1a(message.Blob);
                if (_appliedSlotHash.TryGetValue(key, out var appliedHash) &&
                    appliedHash == hash)
                {
                    skipped++;
                    continue;
                }

                var target = slots[slot];
                if (target == null)
                {
                    // The slot exists conceptually but the mirror has no slot object yet
                    // (equipment controller not ready). Not the same as "empty": retry.
                    LogSlotIssue(key, hash, playerId, slot, "no-slot");
                    continue;
                }

                if (message.Blob == null || message.Blob.Length == 0)
                {
                    if (target.Content != null)
                        target.Unplug();
                    _appliedSlotHash[key] = hash;
                    _slotIssueLogged.Remove(key);
                    applied++;
                    Log($"[v8] apply id={playerId} slot={slot} result=empty");
                    continue;
                }

                var item = DeserializeAiItem(message.Blob);
                if (item == null)
                {
                    LogSlotIssue(key, hash, playerId, slot, "decode");
                    continue;
                }
                if (!target.Plug(item, out _))
                {
                    LogSlotIssue(key, hash, playerId, slot, "reject");
                    continue;
                }
                _appliedSlotHash[key] = hash;
                _slotIssueLogged.Remove(key);
                applied++;
                Log($"[v8] apply id={playerId} slot={slot} result=ok type={item.TypeID}");
            }

            if (!sawMessage)
                return false; // no V8 data at all: keep the legacy v6 fallback available
            if (!sawCurrentEpoch)
                return true; // everything we hold is from another raid

            if (_client.PlayerActiveSlots.TryGetValue(playerId, out var activeSlot))
            {
                ItemStatsSystem.Item hold = null;
                if (activeSlot < slots.Length && slots[activeSlot] != null)
                    hold = slots[activeSlot].Content;
                if (hold == null && slots.Length > 0 && slots[0] != null)
                    hold = slots[0].Content;
                // Never push a null hold item into the mirror: a null ChangeHoldItem can
                // make native code drop the mirror's CharacterItem (T7c). Empty hands stay
                // as-is until a real weapon slot arrives.
                //
                // Re-hold when the active slot changes, when the slot was re-plugged with a
                // new Item instance, or when the mirror lost its held agent: the index-only
                // check used to leave a stale (already unplugged) weapon in hand, which showed
                // up as a missing gun model and a silent SPlayerShot muzzle.
                _mirrorHeldItem.TryGetValue(playerId, out var previousHold);
                var needsHold = hold != null && control.agentHolder != null &&
                    (control.CurrentHoldItemAgent == null ||
                     !ReferenceEquals(hold, previousHold));
                if (needsHold)
                {
                    var agent = control.agentHolder.ChangeHoldItem(hold);
                    if (agent != null)
                    {
                        _mirrorHoldFailed.Remove(playerId);
                        _mirrorHeldItem[playerId] = hold;
                        EnsureHeldWeaponVisual(playerId, control, hold);
                    }
                    else if (_mirrorHoldFailed.Add(playerId))
                    {
                        Log($"[v8] hold failed id={playerId} type={hold.TypeID}");
                    }
                }
                _appliedActiveSlot[playerId] = activeSlot;
                if (needsHold)
                {
                    Log($"[v8] apply id={playerId} active={activeSlot} " +
                        $"hold={(hold != null ? hold.TypeID : 0)}");
                }
            }

            ApplyRemoteFace(playerId, control);

            if (applied > 0)
                Log($"[v8] apply summary id={playerId} applied={applied} skipped={skipped}");
            return true;
        }

        /// <summary>
        /// Reference-mod fallback: ItemAgentHolder.ChangeHoldItem can fail on a native
        /// mirror (no DuckovItemAgent / holder not wired), leaving the weapon holstered.
        /// Ensure an ItemAgent_Gun / ItemAgent_MeleeWeapon exists under the matching hand
        /// socket and has a Holder, so MirrorAimPose can raise the arm.
        /// </summary>
        private void EnsureHeldWeaponVisual(ushort playerId, CharacterMainControl control,
            ItemStatsSystem.Item hold)
        {
            if (control == null || control.characterModel == null || hold == null)
                return;
            try
            {
                var model = control.characterModel;
                var gun = model.RightHandSocket != null
                    ? model.RightHandSocket.GetComponentInChildren<ItemAgent_Gun>(true)
                    : null;
                if (gun == null)
                    gun = model.GetComponentInChildren<ItemAgent_Gun>(true);
                Log($"[v8] held visual id={playerId} item={hold.TypeID} " +
                    $"right={(model.RightHandSocket != null)} gun={(gun != null)} " +
                    $"parent={(gun != null && gun.transform.parent != null ? gun.transform.parent.name : "<null>")}");
                if (gun != null)
                {
                    var gunSocket = model.RightHandSocket;
                    if (gunSocket != null && gun.transform.parent != gunSocket)
                    {
                        gun.transform.SetParent(gunSocket, false);
                        gun.transform.localPosition = Vector3.zero;
                        gun.transform.localRotation = Quaternion.identity;
                        Log($"[v8] mirror hand agent reparented id={playerId} " +
                            $"item={(gun.Item != null ? gun.Item.TypeID : 0)} " +
                            $"socket={gunSocket.name}");
                    }
                    if (gun.Holder == null)
                        gun.SetHolder(control);
                    return;
                }

                if (model.MeleeWeaponSocket != null)
                {
                    var melee = model.MeleeWeaponSocket
                        .GetComponentInChildren<ItemAgent_MeleeWeapon>(true);
                    if (melee != null)
                    {
                        var meleeSocket = model.MeleeWeaponSocket;
                        if (meleeSocket != null && melee.transform.parent != meleeSocket)
                        {
                            melee.transform.SetParent(meleeSocket, false);
                            melee.transform.localPosition = Vector3.zero;
                            melee.transform.localRotation = Quaternion.identity;
                            Log($"[v8] mirror melee agent reparented id={playerId}");
                        }
                        if (melee.Holder == null)
                            melee.SetHolder(control);
                        return;
                    }
                }

                var agent = hold.CreateHandheldAgent();
                var duck = agent as DuckovItemAgent;
                if (duck == null)
                {
                    if (agent != null)
                        Destroy(agent.gameObject);
                    return;
                }
                var socket = duck.handheldSocket == HandheldSocketTypes.meleeWeapon
                    ? model.MeleeWeaponSocket
                    : model.RightHandSocket;
                if (socket == null)
                    socket = model.RightHandSocket;
                if (socket == null)
                {
                    Destroy(duck.gameObject);
                    return;
                }
                duck.transform.SetParent(socket, false);
                duck.transform.localPosition = Vector3.zero;
                duck.transform.localRotation = Quaternion.identity;
                duck.SetHolder(control);
                Log($"[v8] mirror hand agent created id={playerId} item={hold.TypeID} " +
                    $"socket={socket.name}");
            }
            catch (Exception ex)
            {
                Log($"[v8] mirror hand agent failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ApplyRemoteFace(ushort playerId, CharacterMainControl control)
        {
            if (!_client.PlayerFaces.TryGetValue(playerId, out var face) ||
                face == null || face.Face == null || face.Face.Length == 0)
            {
                // No remote face data (notably the lobby, where V8 is intentionally
                // suppressed): keep the game's default yellow-duck face instead of the local
                // player's custom face that was cloned with the body. Retry until the native
                // CustomFace instance is ready.
                if (control != null && !_appliedDefaultFace.Contains(playerId) &&
                    ApplyDefaultFace(control))
                    _appliedDefaultFace.Add(playerId);
                return;
            }
            if (face.RaidEpoch != 0 && _client.RaidEpoch != 0 &&
                face.RaidEpoch != _client.RaidEpoch)
                return;
            var hash = Fnv1a(face.Face);
            if (_appliedFaceHash.TryGetValue(playerId, out var applied) && applied == hash)
                return;
            try
            {
                var json = Encoding.UTF8.GetString(face.Face);
                if (!CustomFaceSettingData.JsonToData(json, out var data))
                    return;
                if (control.characterModel != null)
                    control.characterModel.SetFaceFromData(data);
                var bodyColor = data.headSetting.mainColor;
                if (bodyColor.a <= 0.01f ||
                    (bodyColor.r > 0.98f && bodyColor.g > 0.98f && bodyColor.b > 0.98f))
                    ApplyBodyTint(control, DefaultDuckBodyColor);
                var wingColor = data.wingInfo.color;
                if (wingColor.a <= 0.01f ||
                    (wingColor.r > 0.98f && wingColor.g > 0.98f && wingColor.b > 0.98f))
                    ApplyWingTint(control, DefaultDuckBodyColor);
                _appliedFaceHash[playerId] = hash;
                _appliedDefaultFace.Remove(playerId);
            }
            catch (Exception ex)
            {
                Log($"[v8] player face apply failed id={playerId}: {ex.Message}");
            }
        }

        private static ulong Fnv1a(byte[] bytes)
        {
            var hash = 14695981039346656037UL;
            if (bytes == null)
                return hash;
            foreach (var value in bytes)
            {
                hash ^= value;
                hash *= 1099511628211UL;
            }
            return hash;
        }

        private void LogSlotIssue(ulong key, ulong hash, ushort playerId, byte slot, string result)
        {
            if (_slotIssueLogged.TryGetValue(key, out var logged) && logged == hash)
                return;
            _slotIssueLogged[key] = hash;
            Log($"[v8] apply id={playerId} slot={slot} result={result}");
        }

        private void ResetPlayerSlotCaches(ushort playerId)
        {
            for (byte slot = 0; slot < 10; slot++)
            {
                var key = ((ulong)playerId << 8) | slot;
                _appliedSlotHash.Remove(key);
                _slotIssueLogged.Remove(key);
            }
            _appliedActiveSlot.Remove(playerId);
            _appliedFaceHash.Remove(playerId);
            _appliedDefaultFace.Remove(playerId);
            _remoteSlotCache.Remove(playerId);
            _v8ProbeAt.Remove(playerId);
        }

        /// <summary>T7c diagnostic: what the apply path sees; rate-limited to one line / 5s.</summary>
        private void ProbeRemoteV8(ushort playerId, CharacterMainControl control)
        {
            if (_client == null)
                return;
            if (_v8ProbeAt.TryGetValue(playerId, out var at) && Time.unscaledTime - at < 5f)
                return;
            _v8ProbeAt[playerId] = Time.unscaledTime;
            var count = 0;
            var nonEmpty = 0;
            for (byte slot = 0; slot < 8; slot++)
            {
                if (!_client.PlayerLoadoutsV8.TryGetValue(((ulong)playerId << 8) | slot,
                        out var message) || message == null)
                    continue;
                count++;
                if (message.Blob != null && message.Blob.Length > 0)
                    nonEmpty++;
            }
            Log($"[v8] probe id={playerId} msgs={count} nonEmpty={nonEmpty} " +
                $"epoch={_client.RaidEpoch} control={(control != null)} " +
                $"item={(control != null && control.CharacterItem != null)}");
        }

        private void UpdatePlayerShots()
        {
            if (_client == null || _client.PlayerShots.Count == 0)
                return;
            foreach (var pair in _client.PlayerShots)
            {
                var shot = pair.Value;
                if (shot == null)
                    continue;
                if (_playerShotSeen.TryGetValue(pair.Key, out var seen) && seen == shot.ShotId)
                    continue;
                _playerShotSeen[pair.Key] = shot.ShotId;
                if (!_remotes.TryGetValue(pair.Key, out var remote) || remote == null)
                    continue;
                var control = remote.GetComponentInChildren<CharacterMainControl>(true);
                var gun = control != null ? control.CurrentHoldItemAgent as ItemAgent_Gun : null;
                var origin = new Vector3(shot.OriginX / 100f, shot.OriginY / 100f, shot.OriginZ / 100f);
                var direction = new Vector3(shot.DirX, shot.DirY, shot.DirZ);
                if (direction.sqrMagnitude < 0.0001f)
                    continue;
                var melee = (shot.Flags & PlayerShotFlagMelee) != 0;
                var thrown = (shot.Flags & PlayerShotFlagThrow) != 0;
                _lastPlayerShotAt[pair.Key] = Time.unscaledTime;
                TriggerPlayerAttack(pair.Key, melee);
                if (thrown)
                    SpawnRemoteProjectile(pair.Key, shot, origin, direction.normalized);
                else if (!melee)
                    SpawnAiBullet(0x80000000u | pair.Key, origin, direction.normalized, gun);
                if (_playerShotLogged.Add(pair.Key))
                    Log($"[v8] player shot apply id={pair.Key} weapon={shot.WeaponId} " +
                        $"melee={melee} thrown={thrown}");
            }
        }

        private static void SetBoolIfExists(Animator animator, string name, bool value)
        {
            if (HasParam(animator, name))
                animator.SetBool(name, value);
        }

        private void ClearPlayerV8(ushort playerId)
        {
            ClearPlayerV8WireState(playerId);
            _playerStateSeen.Remove(playerId);
            _playerStateAt.Remove(playerId);
            _playerAttackSeen.Remove(playerId);
            _playerAttackBlockedUntil.Remove(playerId);
            _playerStateHash.Remove(playerId);
            _playerShotSeen.Remove(playerId);
            _lastPlayerShotAt.Remove(playerId);
            ResetPlayerSlotCaches(playerId);
            _mirrorCharacterItem.Remove(playerId);
            _mirrorCharacterItemType.Remove(playerId);
            _mirrorCharacterItemCreateAt.Remove(playerId);
            _mirrorCharacterItemLogAt.Remove(playerId);
            _playerV8Logged.Remove(playerId);
            _playerShotLogged.Remove(playerId);
            _playerAttackCount.Remove(playerId);
        }

        /// <summary>
        /// Drop the received V8 presentation state for one player. The sampler is gated on
        /// _inRaidScene, so without this the old raid loadout stays in PlayerLoadoutsV8 when the
        /// squad returns to the base and is re-applied to the fresh lobby mirror (A saw B in
        /// raid gear). Base uses the legacy SLoadout fallback until the next raid.
        /// </summary>
        private void ClearPlayerV8WireState(ushort playerId)
        {
            if (_client == null || playerId == 0)
                return;
            for (byte slot = 0; slot < 10; slot++)
                _client.PlayerLoadoutsV8.Remove(((ulong)playerId << 8) | slot);
            _client.PlayerActiveSlots.Remove(playerId);
            _client.PlayerFaces.Remove(playerId);
            _client.PlayerStateSamples.Remove(playerId);
            _client.PlayerShots.Remove(playerId);
        }

        /// <summary>
        /// Drop every received V8 presentation record when the local player leaves the raid.
        /// Remotes that were not in the mirror dictionary would otherwise keep stale gear.
        /// </summary>
        private void ClearAllPlayerV8WireState()
        {
            if (_client == null)
                return;
            _client.PlayerLoadoutsV8.Clear();
            _client.PlayerActiveSlots.Clear();
            _client.PlayerFaces.Clear();
            _client.PlayerStateSamples.Clear();
            _client.PlayerShots.Clear();
        }

        /// <summary>
        /// The local equipment slots were rebuilt from SInitialInventory/SInventorySnapshot.
        /// Force the next V8 sample to be sent even when the pre-apply sample was empty; B's
        /// mirror otherwise stayed unarmed on A after a death/respawn.
        /// </summary>
        internal void InvalidateLocalLoadout()
        {
            for (var i = 0; i < _sentSlotValid.Length; i++)
                _sentSlotValid[i] = false;
            _sentActiveSlotValid = false;
            _nextLoadoutHeartbeat = 0f;
            _nextPlayerLoadoutSend = 0f;
        }

        /// <summary>
        /// The mirror GameObject was recreated (fresh, bare clone). Drop the
        /// "already applied" per-slot hashes so V8 loadout + face are pushed onto the new
        /// clone in the same frame; otherwise the hash check short-circuits and the
        /// mirror stays undressed (BUG-2026-11-12 "remote lost all equipment").
        /// Attack/shot sequence caches are kept on purpose: clearing them would replay a
        /// phantom attack on the rebuilt mirror.
        /// </summary>
        private void ResetRemotePresentationCaches(ushort playerId)
        {
            ResetPlayerSlotCaches(playerId);
            _mirrorCharacterItem.Remove(playerId);
            _mirrorCharacterItemType.Remove(playerId);
            _mirrorCharacterItemCreateAt.Remove(playerId);
            _mirrorCharacterItemLogAt.Remove(playerId);
            _remoteLoadoutSignature.Remove(playerId);
            _playerStateHash.Remove(playerId);
            _playerV8Logged.Remove(playerId);
            _playerShotLogged.Remove(playerId);
            _playerAttackCount.Remove(playerId);
            Log($"[v8] mirror rebuilt id={playerId}; presentation caches reset");
        }
    }

    /// <summary>
    /// BUG-2026-11-12: plays the remote player's attack animation by sampling the
    /// native attack clip directly.
    ///
    /// Why not Animator.Play: on the cloned controller the layer-1 state reports the
    /// correct state hash, clip and weight 1.00 but contributes no pose (verified
    /// 2026-11-12 by forcing the same state on the LOCAL player animator: state/clip
    /// correct, skeleton static). Direct AnimationClip.SampleAnimation does move the
    /// skeleton on both the local model and the mirror, so playback is driven that way.
    /// </summary>
    public sealed class PlayerMirrorAttackAnim : MonoBehaviour
    {
        public Animator Animator;

        /// <summary>True while a melee clip is being sampled (aim overlay must stand back).</summary>
        public bool IsPlaying
        {
            get { return _active; }
        }

        private const string MeleeClipName = "Armature|MeleeAttack_RightSlash";
        private const int MaxDiagnosticPlays = 6;

        private static readonly HashSet<int> LoggedMissingClips = new HashSet<int>();

        private AnimationClip _clip;
        private float _time;
        private bool _active;
        private int _plays;

        public void Trigger(ushort playerId)
        {
            if (Animator == null)
                return;

            var clip = FindClip(Animator, MeleeClipName);
            if (clip == null)
            {
                if (LoggedMissingClips.Add(Animator.StringToHash(MeleeClipName)))
                    CoopRuntime.Log($"[v8] mirror attack clip missing clip={MeleeClipName}");
                return;
            }

            // Every swing is a separate attack, so always restart the clip.
            _clip = clip;
            _time = 0f;
            _active = true;
            if (_plays >= MaxDiagnosticPlays)
                return;

            _plays++;
            CoopRuntime.Log($"[v8] mirror melee clip id={playerId} " +
                $"clip={clip.name} len={clip.length:F2} plays={_plays}");
        }

        private void LateUpdate()
        {
            if (!_active || Animator == null || _clip == null)
                return;

            try
            {
                _clip.SampleAnimation(Animator.gameObject, Mathf.Min(_time, _clip.length));
            }
            catch (Exception ex)
            {
                _active = false;
                if (_plays <= MaxDiagnosticPlays)
                    CoopRuntime.Log("[v8] mirror melee clip sample failed: " +
                        ex.GetType().Name + ": " + ex.Message);
                return;
            }

            _time += Time.deltaTime;
            if (_time < _clip.length)
                return;

            _active = false;
        }

        private static AnimationClip FindClip(Animator animator, string name)
        {
            var controller = animator.runtimeAnimatorController;
            if (controller == null || controller.animationClips == null)
                return null;

            foreach (var clip in controller.animationClips)
            {
                if (clip != null && clip.name == name)
                    return clip;
            }

            return null;
        }
    }

    /// <summary>
    /// Keeps a gun-holding mirror in a raised/aim pose. The native player pose comes from
    /// the game's own aim machinery, which the passive clone never runs; without it the
    /// mirror holds the weapon pointing at the floor (hand bone pitched ~64 deg, measured
    /// 2026-11-12). We sample the native "Aim" pose clip on the upper body every frame and
    /// restore the lower body so walking still plays.
    /// </summary>
    /// <summary>
    /// Copies the local player's arm-bone rotations onto the native mirror after the
    /// Animator runs. The mirror's aim/raise layers do not exist on the created character,
    /// so the procedural raise is nearly a no-op once the weapon already points forward;
    /// mirroring the local arms gives a correct two-handed hold immediately.
    /// </summary>
    public sealed class MirrorBonePose : MonoBehaviour
    {
        private static readonly string[] BoneNames =
        {
            "UpperArm.R", "LowerArm.R", "Hand.R",
            "UpperArm.L", "LowerArm.L", "Hand.L",
        };

        private Transform[] _source;
        private Transform[] _target;
        private Animator _sourceAnimator;
        private Animator _targetAnimator;

        private static readonly string[] ParamNames =
        {
            "GunReady", "HandState", "Reloading",
        };

        internal void Init(CharacterMainControl local, CharacterMainControl mirror)
        {
            _source = Find(local);
            _target = Find(mirror);
            _sourceAnimator = local != null ? CoopRuntime.FindBodyAnimator(local.gameObject) : null;
            _targetAnimator = mirror != null ? CoopRuntime.FindBodyAnimator(mirror.gameObject) : null;
            if (_targetAnimator != null)
                _targetAnimator.applyRootMotion = false;
        }

        private static Transform[] Find(CharacterMainControl control)
        {
            if (control == null)
                return null;
            var root = control.characterModel != null
                ? control.characterModel.transform
                : control.transform;
            var byName = new Dictionary<string, Transform>();
            foreach (var bone in root.GetComponentsInChildren<Transform>(true))
            {
                if (bone != null && !byName.ContainsKey(bone.name))
                    byName[bone.name] = bone;
            }
            var result = new Transform[BoneNames.Length];
            for (var i = 0; i < BoneNames.Length; i++)
                byName.TryGetValue(BoneNames[i], out result[i]);
            return result;
        }

        private void LateUpdate()
        {
            CopyParams();
            if (_source == null || _target == null)
                return;
            for (var i = 0; i < _source.Length; i++)
            {
                if (_source[i] != null && _target[i] != null)
                    _target[i].localRotation = _source[i].localRotation;
            }
        }

        private void CopyParams()
        {
            if (_sourceAnimator == null || _targetAnimator == null)
                return;
            _targetAnimator.applyRootMotion = false;
            foreach (var name in ParamNames)
            {
                var hash = Animator.StringToHash(name);
                if (!HasParam(_targetAnimator, hash))
                    continue;
                if (HasParam(_sourceAnimator, hash))
                {
                    if (TryGetBool(_sourceAnimator, hash, out var b))
                        _targetAnimator.SetBool(hash, b);
                    else if (TryGetInt(_sourceAnimator, hash, out var i))
                        _targetAnimator.SetInteger(hash, i);
                    else if (TryGetFloat(_sourceAnimator, hash, out var f))
                        _targetAnimator.SetFloat(hash, f);
                }
            }
        }

        private static bool HasParam(Animator animator, int hash)
        {
            if (animator == null)
                return false;
            foreach (var p in animator.parameters)
            {
                if (p.nameHash == hash)
                    return true;
            }
            return false;
        }

        private static bool TryGetBool(Animator animator, int hash, out bool value)
        {
            value = false;
            foreach (var p in animator.parameters)
            {
                if (p.nameHash != hash)
                    continue;
                if (p.type == AnimatorControllerParameterType.Bool)
                {
                    value = animator.GetBool(hash);
                    return true;
                }
                return false;
            }
            return false;
        }

        private static bool TryGetInt(Animator animator, int hash, out int value)
        {
            value = 0;
            foreach (var p in animator.parameters)
            {
                if (p.nameHash != hash)
                    continue;
                if (p.type == AnimatorControllerParameterType.Int)
                {
                    value = animator.GetInteger(hash);
                    return true;
                }
                return false;
            }
            return false;
        }

        private static bool TryGetFloat(Animator animator, int hash, out float value)
        {
            value = 0f;
            foreach (var p in animator.parameters)
            {
                if (p.nameHash != hash)
                    continue;
                if (p.type == AnimatorControllerParameterType.Float)
                {
                    value = animator.GetFloat(hash);
                    return true;
                }
                return false;
            }
            return false;
        }
    }

    /// <summary>
    /// The native mirror's CharacterMainControl/aim control keeps re-aiming modelRoot with
    /// the local player's input, so only the weapon turned. Drive modelRoot to the remote
    /// snapshot yaw in LateUpdate (after the animator), like the reference mod.
    /// </summary>
    public sealed class MirrorYawFollow : MonoBehaviour
    {
        private float _yaw;
        private CharacterMainControl _control;
        private Vector3 _previousPosition;
        private Vector3 _targetPosition;
        private Vector3 _previousTarget;
        private bool _hasPrevious;
        private bool _hasTarget;
        private bool _hasTargetPrevious;
        private Vector3 _targetDirection;
        private float _lastTargetTime;
        private float _smoothedSpeed;
        // How long the snapshot target must stay unchanged before we treat the remote as
        // standing still (3 snapshots at 20Hz) and let the native mover go idle.
        private const float IdleAfterSeconds = 0.15f;
        // The native Movement/ECM2 mover writes the body transform every Update (that is
        // what keeps the native walk/run pose alive), so the body must be pinned back to
        // its anchor every LateUpdate or it accumulates an offset from the mirror root.
        private Vector3 _anchorLocalPosition;
        private Quaternion _anchorLocalRotation = Quaternion.identity;
        private bool _anchorCaptured;
        private int _positionLogs;
        private float _nextPositionLog;

        internal void SetYaw(float yaw)
        {
            CaptureAnchor();
            _yaw = yaw;
        }

        internal void CaptureAnchor()
        {
            if (_anchorCaptured)
                return;
            _anchorCaptured = true;
            _anchorLocalPosition = transform.localPosition;
            _anchorLocalRotation = transform.localRotation;
        }

        internal void SetTarget(Vector3 target)
        {
            if (!_hasTargetPrevious)
            {
                _previousTarget = target;
                _lastTargetTime = Time.unscaledTime;
                _hasTargetPrevious = true;
            }
            _targetPosition = target;
            _hasTarget = true;
        }

        private void LateUpdate()
        {
            if (_control == null)
                _control = GetComponent<CharacterMainControl>();
            if (_control != null && _control.modelRoot != null)
                _control.modelRoot.rotation = Quaternion.Euler(0f, _yaw, 0f);

            // CharacterAnimationControl resets locomotion params to zero in Update because
            // movement scripts are disabled. Feed the remote snapshot velocity after it so
            // the blend tree plays walk/run at the remote player's actual speed.
            var root = transform.parent != null ? transform.parent : transform;
            if (_hasTarget)
            {
                // Teleports (spawn assign / respawn) snap; ordinary movement is already
                // smoothed once in UpdateRemotePlayers, so do not smooth it a second time
                // here (the two Lerps together were adding ~80ms of extra lag).
                if (Vector3.Distance(root.position, _targetPosition) > 5f)
                    root.position = _targetPosition;
            }
            var position = root.position;
            var deltaTime = Mathf.Max(0.0001f, Time.deltaTime);
            var speed = 0f;
            var direction = Vector3.zero;
            if (_hasTarget)
            {
                if (_targetPosition != _previousTarget)
                {
                    var targetDelta = _targetPosition - _previousTarget;
                    targetDelta.y = 0f;
                    var elapsed = Mathf.Max(0.05f, Time.unscaledTime - _lastTargetTime);
                    var instant = targetDelta.magnitude / elapsed;
                    _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, instant, 0.5f);
                    if (targetDelta.sqrMagnitude > 0.000001f)
                        _targetDirection = targetDelta.normalized;
                    _previousTarget = _targetPosition;
                    _lastTargetTime = Time.unscaledTime;
                }
                else if (Time.unscaledTime - _lastTargetTime > IdleAfterSeconds)
                {
                    // The target stopped changing (the remote player is standing still).
                    // Without this the last non-zero move input kept the native Movement
                    // walking in place forever, and the decay only ran on target changes.
                    _smoothedSpeed = Mathf.MoveTowards(_smoothedSpeed, 0f, 18f * deltaTime);
                }
                speed = _smoothedSpeed;
                // Feed a zero move input once idle so the native Movement decelerates and
                // CharacterAnimationControl writes MoveSpeed=0 by itself.
                direction = speed > 0.1f ? _targetDirection : Vector3.zero;
            }
            else if (_hasPrevious)
            {
                var delta = position - _previousPosition;
                delta.y = 0f;
                speed = delta.sqrMagnitude > 25f ? 0f : delta.magnitude / deltaTime;
                if (delta.sqrMagnitude > 0.000001f)
                    direction = delta.normalized;
            }

            if (speed > 25f)
                speed = 0f;
            if (_control != null)
            {
                _control.SetMoveInput(direction);
                _control.SetRunInput(speed > 3.5f);
            }
            var local = root.InverseTransformDirection(direction);
            var animator = _control != null && _control.characterModel != null
                ? _control.characterModel.GetComponentInChildren<Animator>(true)
                : null;
            SetFloat(animator, "MoveSpeed", speed);
            SetFloat(animator, "Speed", speed);
            SetFloat(animator, "MoveDirX", local.x);
            SetFloat(animator, "MoveDirY", local.z);
            _previousPosition = position;
            _hasPrevious = true;

            // Movement/ECM2 has already integrated this frame's velocity and written the
            // body transform. Pin the body back onto its anchor so the visible model sits
            // exactly on the mirror root; only the root follows snapshots.
            if (_anchorCaptured)
            {
                transform.localPosition = _anchorLocalPosition;
                transform.localRotation = _anchorLocalRotation;
            }
            LogPositionIfDue(root);
        }

        /// <summary>
        /// A few lines per mirror: proves whether the body stays on the root (~0 offset) or
        /// accumulates a local offset from the native mover, and how far the root lags the
        /// snapshot target.
        /// </summary>
        private void LogPositionIfDue(Transform root)
        {
            if (_positionLogs >= 8 || Time.unscaledTime < _nextPositionLog)
                return;
            _nextPositionLog = Time.unscaledTime + 2f;
            _positionLogs++;
            var target = _hasTarget ? _targetPosition.ToString("F2") : "<none>";
            var error = _hasTarget ? Vector3.Distance(transform.position, _targetPosition) : -1f;
            CoopRuntime.Log($"[mirror-pos] {root.name} root={root.position.ToString("F2")} " +
                $"bodyLocal={transform.localPosition.ToString("F3")} " +
                $"bodyWorld={transform.position.ToString("F2")} target={target} err={error:F2} " +
                $"speed={_smoothedSpeed:F2}");
        }

        private static void SetFloat(Animator animator, string name, float value)
        {
            if (animator == null)
                return;
            foreach (var parameter in animator.parameters)
            {
                if (parameter.name != name)
                    continue;
                if (parameter.type == AnimatorControllerParameterType.Float)
                    animator.SetFloat(parameter.nameHash, value);
                return;
            }
        }
    }

    public sealed class MirrorAimPose : MonoBehaviour
    {
        // The native aim/raise-gun pose clips (Armature|Aim / Armature_Pose_RaiseGun) are
        // NOT part of the character controller's clip list, so they cannot be sampled from
        // runtimeAnimatorController.animationClips. The mirror therefore raises the weapon
        // procedurally: part of the barrel-to-forward rotation goes on the shoulder, the
        // rest on the weapon socket, recomputed every frame after the Animator.
        private const float ArmRaiseShare = 0.35f;
        private const float MeleeArmRaiseShare = 0.60f;
        private const float MaxRaiseDegrees = 70f;

        private ushort _playerId;
        private Animator _animator;
        private CharacterMainControl _control;
        private PlayerMirrorAttackAnim _attack;
        private Transform _upperArm;
        private Transform _socket;
        private int _logCount;
        private float _nextLog;

        internal void Init(ushort playerId, Animator animator, CharacterMainControl control)
        {
            _playerId = playerId;
            _animator = animator;
            _control = control;
        }

        private void LateUpdate()
        {
            if (GetComponent<MirrorBonePose>() != null)
                return; // bone-copy pose owns the arms
            if (_animator == null)
            {
                Destroy(this);
                return;
            }

            if (_attack == null)
                _attack = _animator.GetComponent<PlayerMirrorAttackAnim>();
            if (_attack != null && _attack.IsPlaying)
            {
                LogAimSkip("attack-playing");
                return;
            }

            ItemStatsSystem.ItemAgent agent =
                _control != null ? _control.CurrentHoldItemAgent : null;
            if (agent == null)
                agent = ResolveHeldAgentFromSockets();
            if (agent == null)
            {
                LogAimSkip("no-agent");
                return;
            }

            var isGun = agent.GetType().Name.Contains("Gun");
            var isMelee = !isGun &&
                          agent.GetType().Name.IndexOf("Melee", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isGun && !isMelee)
            {
                LogAimSkip("agent-type-" + agent.GetType().Name);
                return;
            }

            if (_upperArm == null || _socket == null)
                Resolve(agent);
            if (_upperArm == null || _socket == null)
                return;

            try
            {
                var character = _control.transform;

                if (isGun)
                {
                    var target = character.forward;
                    var delta = Quaternion.FromToRotation(agent.transform.forward, target);
                    var angle = Quaternion.Angle(Quaternion.identity, delta);
                    if (angle > MaxRaiseDegrees)
                        delta = Quaternion.Slerp(Quaternion.identity, delta, MaxRaiseDegrees / angle);

                    _upperArm.rotation = Quaternion.Slerp(Quaternion.identity, delta, ArmRaiseShare) *
                                         _upperArm.rotation;

                    // Re-aim the socket so the barrel actually points forward after the
                    // shoulder moved.
                    var remaining = Quaternion.FromToRotation(agent.transform.forward, target);
                    _socket.rotation = remaining * _socket.rotation;
                }
                else
                {
                    // Melee: the socket offset itself is authored correctly (the weapon is
                    // parented to RightHandSocket); what looks "stowed" is the ARM sitting
                    // in the idle pose. So raise the arm only and leave the weapon's local
                    // rotation untouched — every attempt to re-aim the socket rotated the
                    // blade into the face / above the head (reported 2026-11-12).
                    var target = character.forward;
                    var delta = Quaternion.FromToRotation(agent.transform.forward, target);
                    var angle = Quaternion.Angle(Quaternion.identity, delta);
                    if (angle > MaxRaiseDegrees)
                        delta = Quaternion.Slerp(Quaternion.identity, delta, MaxRaiseDegrees / angle);

                    _upperArm.rotation = Quaternion.Slerp(Quaternion.identity, delta, MeleeArmRaiseShare) *
                                         _upperArm.rotation;

                    if (_logCount < 2 && Time.unscaledTime >= _nextLog)
                    {
                        _logCount++;
                        _nextLog = Time.unscaledTime + 2f;
                        var hand = _socket.position;
                        CoopRuntime.Log($"[v8] mirror melee pose id={_playerId} " +
                            $"raise={angle * MeleeArmRaiseShare:F1}deg " +
                            $"handY={hand.y - character.position.y:F2}m " +
                            $"weapon={agent.transform.forward.ToString("F2")} " +
                            $"socket={_socket.name}");
                    }

                    return;
                }

                if (_logCount < 3 && Time.unscaledTime >= _nextLog)
                {
                    _logCount++;
                    _nextLog = Time.unscaledTime + 2f;
                    var after = Vector3.Angle(agent.transform.forward, character.forward);
                    CoopRuntime.Log($"[v8] mirror aim pose id={_playerId} kind=gun " +
                        $"upper={_upperArm.name} socket={_socket.name} " +
                        $"now={after:F1}deg agentParent=" +
                        $"{(agent.transform.parent != null ? agent.transform.parent.name : "<null>")} " +
                        $"localPos={agent.transform.localPosition.ToString("F3")}");
                }
            }
            catch (Exception ex)
            {
                CoopRuntime.Log($"[v8] mirror aim pose failed: {ex.GetType().Name}: {ex.Message}");
                _upperArm = null;
                _socket = null;
            }
        }


        /// <summary>
        /// Native-created mirrors keep a weapon agent under the hand socket, but their
        /// ItemAgentHolder does not always expose CurrentHoldItemAgent. Find the socket
        /// child agent instead so the aim pose still raises the arm.
        /// </summary>
        private ItemStatsSystem.ItemAgent ResolveHeldAgentFromSockets()
        {
            if (_control == null)
                return null;
            try
            {
                foreach (var candidate in _control.GetComponentsInChildren<ItemStatsSystem.ItemAgent>(true))
                {
                    if (candidate == null || candidate.Item == null)
                        continue;
                    var parent = candidate.transform.parent;
                    if (parent == null)
                        continue;
                    var parentName = parent.name;
                    if (parentName.IndexOf("Socket", StringComparison.OrdinalIgnoreCase) < 0 &&
                        parentName.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (_logCount < 1)
                    {
                        _logCount++;
                        CoopRuntime.Log($"[v8] mirror aim agent fallback id={_playerId} " +
                            $"item={candidate.Item.TypeID} parent={parentName}");
                    }
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                CoopRuntime.Log($"[v8] mirror aim agent fallback failed: {ex.GetType().Name}");
            }
            return null;
        }

        private void LogAimSkip(string reason)
        {
            if (_logCount >= 6 || Time.unscaledTime < _nextLog)
                return;
            _logCount++;
            _nextLog = Time.unscaledTime + 2f;
            CoopRuntime.Log($"[v8] mirror aim skip id={_playerId} reason={reason}");
        }

        private void Resolve(ItemStatsSystem.ItemAgent agent)        {
            try
            {
                foreach (var transform in _animator.GetComponentsInChildren<Transform>(true))
                {
                    if (transform == null)
                        continue;
                    if (transform.name == "UpperArm.R")
                        _upperArm = transform;
                }

                var parent = agent.transform.parent;
                if (parent != null &&
                    parent.name.IndexOf("Socket", StringComparison.OrdinalIgnoreCase) >= 0)
                    _socket = parent;
                else
                    _socket = agent.transform;

                CoopRuntime.Log($"[v8] mirror aim resolved id={_playerId} " +
                    $"upper={(_upperArm != null ? _upperArm.name : "<null>")} " +
                    $"socket={_socket.name} agentParent={(parent != null ? parent.name : "<null>")}");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log($"[v8] mirror aim resolve failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Short recoil kick for a remote mirror's weapon model. The mirror is a passive
    /// clone, so the native recoil never runs; this only offsets the weapon transform
    /// in LateUpdate (after the Animator) and restores it when the kick ends.
    /// </summary>
    public sealed class MirrorGunRecoil : MonoBehaviour
    {
        private const float Duration = 0.16f;
        private const float KickBack = 0.05f;
        private const float KickUp = 0.025f;
        private const float KickPitch = 15f;
        private const float KickRoll = 3f;

        private float _time = Duration;
        private Vector3 _baseLocalPosition;
        private Quaternion _baseLocalRotation;
        private Vector3 _parentBack = Vector3.back;
        private Vector3 _parentUp = Vector3.up;
        private bool _playing;

        internal Vector3 BackDirection
        {
            get { return _parentBack; }
        }

        public void Play(Transform muzzle)
        {
            if (!_playing)
            {
                _baseLocalPosition = transform.localPosition;
                _baseLocalRotation = transform.localRotation;
            }

            var parent = transform.parent;
            if (muzzle != null)
            {
                // localPosition lives in the PARENT (socket) space, so the kick
                // direction has to be expressed there too.
                var forward = muzzle.forward;
                var back = parent != null
                    ? -parent.InverseTransformDirection(forward)
                    : -transform.InverseTransformDirection(forward);
                if (back.sqrMagnitude > 0.0001f)
                    _parentBack = back.normalized;
            }

            if (parent != null)
            {
                // Orientation-independent bob so the kick is visible even when the
                // barrel points down (reported on the mirror).
                var up = parent.InverseTransformDirection(Vector3.up);
                if (up.sqrMagnitude > 0.0001f)
                    _parentUp = up.normalized;
            }

            _playing = true;
            _time = 0f;
        }

        private void LateUpdate()
        {
            if (!_playing)
                return;

            _time += Time.deltaTime;
            if (_time >= Duration)
            {
                transform.localPosition = _baseLocalPosition;
                transform.localRotation = _baseLocalRotation;
                _playing = false;
                return;
            }

            var k = 1f - _time / Duration;
            transform.localPosition = _baseLocalPosition +
                _parentBack * (KickBack * k) + _parentUp * (KickUp * k);
            transform.localRotation = _baseLocalRotation *
                Quaternion.Euler(-KickPitch * k, 0f, KickRoll * k);
        }
    }
}
