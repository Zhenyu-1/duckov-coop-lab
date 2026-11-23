using System;
using System.Collections.Generic;
using System.Reflection;
using DuckovCoop.Client;
using HarmonyLib;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// Host-side AI animation sampling (CAiAnim): 10Hz + immediate send on attack/reload edge.
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private readonly Dictionary<uint, Animator> _realAiAnimators = new Dictionary<uint, Animator>();
        private readonly Dictionary<uint, Vector3> _realAiLastPositions = new Dictionary<uint, Vector3>();
        private readonly Dictionary<uint, ushort> _aiAnimSeq = new Dictionary<uint, ushort>();
        private readonly Dictionary<uint, byte> _aiAttackSeq = new Dictionary<uint, byte>();
        private readonly Dictionary<uint, float> _aiAnimNextSend = new Dictionary<uint, float>();
        private readonly Dictionary<uint, bool> _aiAnimDirty = new Dictionary<uint, bool>();
        private readonly Dictionary<uint, bool> _aiAttackEdge = new Dictionary<uint, bool>();
        private readonly Dictionary<uint, ushort> _aiAnimSeenSeq = new Dictionary<uint, ushort>();
        private readonly Dictionary<uint, float> _aiAnimReceivedAt = new Dictionary<uint, float>();
        private readonly Dictionary<uint, byte> _appliedAiAttackSeq = new Dictionary<uint, byte>();
        private readonly HashSet<uint> _aiAnimLogged = new HashSet<uint>();
        private readonly HashSet<uint> _aiAnimApplyLogged = new HashSet<uint>();
        private readonly Dictionary<uint, byte> _aiAnimLastFlags = new Dictionary<uint, byte>();

        private void UpdateAiAnimSampling()
        {
            if (!_inRaidScene || _client == null || _client.PlayerId == 0 || _realAi.Count == 0)
                return;

            foreach (var pair in _realAi)
            {
                var ai = pair.Value;
                if (ai == null)
                    continue;

                if (!_realAiAnimators.TryGetValue(pair.Key, out var animator) || animator == null)
                {
                    animator = ai.GetComponentInChildren<Animator>(true);
                    if (animator == null)
                        continue;
                    _realAiAnimators[pair.Key] = animator;
                    _aiAnimSeq[pair.Key] = 0;
                    _aiAttackSeq[pair.Key] = 0;
                    _aiAnimNextSend[pair.Key] = 0f;
                    _aiAnimDirty[pair.Key] = true;
                }

                var due = !_aiAnimNextSend.TryGetValue(pair.Key, out var next) || Time.time >= next;
                var dirty = _aiAnimDirty.TryGetValue(pair.Key, out var dirtyFlag) && dirtyFlag;
                if (!due && !dirty)
                    continue;

                SendAiAnimSample(pair.Key, animator, ai.transform);
            }
        }

        private void SendAiAnimSample(uint aiId, Animator animator, Transform root)
        {
            var seq = (ushort)(_aiAnimSeq.TryGetValue(aiId, out var stored) ? stored + 1 : 1);
            _aiAnimSeq[aiId] = seq;
            _aiAnimNextSend[aiId] = Time.time + 0.1f;
            _aiAnimDirty[aiId] = false;

            var position = root.position;
            var moved = _realAiLastPositions.TryGetValue(aiId, out var previous);
            var delta = moved ? position - previous : Vector3.zero;
            delta.y = 0f;
            _realAiLastPositions[aiId] = position;

            var speed = moved ? Mathf.RoundToInt(delta.magnitude / Mathf.Max(Time.deltaTime, 0.001f) * 100f) : 0;
            var direction = delta.sqrMagnitude > 0.0001f ? delta.normalized : Vector3.zero;

            var flags = (byte)0;
            if (ReadAnimBool(animator, "Die"))
                flags |= AiAnim.FlagDie;
            if (ReadAnimBool(animator, "Reloading"))
                flags |= AiAnim.FlagReloading;
            if (ReadAnimBool(animator, "Dashing"))
                flags |= AiAnim.FlagDashing;
            if (ReadAnimBool(animator, "GunReady"))
                flags |= AiAnim.FlagGunReady;
            if (_aiAttackEdge.TryGetValue(aiId, out var fired) && fired)
                flags |= AiAnim.FlagAttackTrigger;

            var handState = (byte)Mathf.Clamp(ReadAnimInt(animator, "HandState"), 0, 255);
            var attackIndex = (byte)Mathf.Clamp(ReadAnimInt(animator, "AttackIndex"), 0, 255);
            _aiAttackEdge[aiId] = false;
            var attackSeq = _aiAttackSeq.TryGetValue(aiId, out var attack) ? attack : (byte)0;

            var anim = new AiAnim
            {
                AiId = aiId,
                RaidEpoch = _aiSpawnEpoch,
                Seq = seq,
                MoveSpeed = (ushort)Mathf.Clamp(speed, 0, 65535),
                MoveDirX = (sbyte)Mathf.Clamp(Mathf.RoundToInt(direction.x * 127f), -127, 127),
                MoveDirY = (sbyte)Mathf.Clamp(Mathf.RoundToInt(direction.z * 127f), -127, 127),
                HandState = handState,
                AttackIndex = attackIndex,
                Flags = flags,
                AttackSeq = attackSeq,
            };

            if (_aiAnimLogged.Add(aiId) || (flags & AiAnim.FlagAttackTrigger) != 0)
                Log($"[d5] anim send ai={aiId} seq={seq} flags=0x{flags:X2} speed={speed}");
            _client.SendAiAnim(anim);
        }

        /// <summary>Client side: true when a fresh sampled animation exists for this AI.</summary>
        private bool TryGetFreshAiAnim(uint aiId, out AiAnim anim)
        {
            anim = null;
            if (_client == null || !_client.AiAnims.TryGetValue(aiId, out anim))
                return false;
            if (!_aiAnimSeenSeq.TryGetValue(aiId, out var seen) || seen != anim.Seq)
            {
                _aiAnimSeenSeq[aiId] = anim.Seq;
                _aiAnimReceivedAt[aiId] = Time.unscaledTime;
            }
            return Time.unscaledTime - (_aiAnimReceivedAt.TryGetValue(aiId, out var at) ? at : 0f) < 0.75f;
        }

        private void ApplyAiAnimSample(Animator animator, AiAnim anim, uint aiId)
        {
            // Log once per AI and on each new attack edge. The old condition logged every
            // sample while the trigger bit stayed set, which flooded the log at 10 Hz.
            _aiAnimLastFlags.TryGetValue(aiId, out var lastFlags);
            var attackEdge = (anim.Flags & AiAnim.FlagAttackTrigger) != 0 &&
                             (lastFlags & AiAnim.FlagAttackTrigger) == 0;
            _aiAnimLastFlags[aiId] = anim.Flags;
            if (_aiAnimApplyLogged.Add(aiId) || attackEdge)
                Log($"[d5] anim apply ai={aiId} seq={anim.Seq} flags=0x{anim.Flags:X2} " +
                    $"attackEdge={attackEdge}");
            if (animator == null || anim == null)
                return;
            try
            {
                if (HasParam(animator, "MoveSpeed")) animator.SetFloat("MoveSpeed", anim.MoveSpeed / 100f);
                if (HasParam(animator, "MoveDirX")) animator.SetFloat("MoveDirX", anim.MoveDirX / 127f);
                if (HasParam(animator, "MoveDirY")) animator.SetFloat("MoveDirY", anim.MoveDirY / 127f);
                if (HasParam(animator, "HandState")) animator.SetInteger("HandState", anim.HandState);
                if (HasParam(animator, "AttackIndex")) animator.SetInteger("AttackIndex", anim.AttackIndex);
                if (HasParam(animator, "GunReady")) animator.SetBool("GunReady", (anim.Flags & AiAnim.FlagGunReady) != 0);
                if (HasParam(animator, "Dashing")) animator.SetBool("Dashing", (anim.Flags & AiAnim.FlagDashing) != 0);
                if (HasParam(animator, "Reloading")) animator.SetBool("Reloading", (anim.Flags & AiAnim.FlagReloading) != 0);
                if (HasParam(animator, "Die")) animator.SetBool("Die", (anim.Flags & AiAnim.FlagDie) != 0);
                var previous = _appliedAiAttackSeq.TryGetValue(aiId, out var stored) ? stored : anim.AttackSeq;
                if (anim.AttackSeq != previous)
                {
                    if (HasParam(animator, "Attack")) animator.SetTrigger("Attack");
                    _appliedAiAttackSeq[aiId] = anim.AttackSeq;
                    SpawnReplicaShotFx(aiId);
                }
            }
            catch
            {
                // animation apply is best effort
            }
        }

        /// <summary>Approximate remote gun VFX: the shooter's bullet/tracer is local-only,
        /// so the replica plays the muzzle flash from its own (already equipped) gun.</summary>
        private void SpawnReplicaShotFx(uint aiId)
        {
            try
            {
                if (!_aiGhosts.TryGetValue(aiId, out var ghost) || ghost == null)
                    return;
                var control = ghost.GetComponentInChildren<CharacterMainControl>(true);
                var agent = control != null ? control.CurrentHoldItemAgent : null;
                if (agent == null || !agent.GetType().Name.Contains("Gun"))
                    return;
                var setting = agent.GetType()
                    .GetField("_gunItemSetting", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    ?.GetValue(agent);
                var fx = setting?.GetType()
                    .GetField("muzzleFxPfb", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(setting) as GameObject;
                var muzzle = agent.transform.Find("Muzzle") ?? agent.transform;
                if (fx != null)
                    UnityEngine.Object.Instantiate(fx, muzzle.position, muzzle.rotation);
            }
            catch
            {
                // cosmetic only
            }
        }

        internal void NoteAiAttack(GameObject shooter)
        {
            if (shooter == null)
                return;
            if (!TryResolveAiId(shooter, out var aiId))
                return;
            _aiAttackSeq[aiId] = (byte)((_aiAttackSeq.TryGetValue(aiId, out var sequence) ? sequence : (byte)0) + 1);
            _aiAttackEdge[aiId] = true;
            _aiAnimDirty[aiId] = true;
        }

        private static bool ReadAnimBool(Animator animator, string name)
        {
            try
            {
                if (HasParam(animator, name))
                    return animator.GetBool(name);
            }
            catch
            {
                // parameter may be missing
            }

            return false;
        }

        private static int ReadAnimInt(Animator animator, string name)
        {
            try
            {
                if (HasParam(animator, name))
                    return animator.GetInteger(name);
            }
            catch
            {
                // parameter may be missing
            }

            return 0;
        }

        private static bool HasParam(Animator animator, string name)
        {
            var parameters = animator.parameters;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == name)
                    return true;
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(ItemAgent_Gun), "ShootOneBullet")]
    internal static class AiGunFireAnimPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ItemAgent_Gun __instance, Vector3 _muzzlePoint, Vector3 _shootDirection)
        {
            try
            {
                var holder = __instance != null ? __instance.Holder : null;
                CoopRuntime.Active?.NoteAiAttack(holder != null ? holder.gameObject : null);
                CoopRuntime.Active?.NoteAiShot(__instance, holder, _muzzlePoint, _shootDirection);
                CoopRuntime.Active?.NotePlayerShot(__instance, holder, _muzzlePoint, _shootDirection);
            }
            catch
            {
                // animation/shot sampling is best effort
            }
        }

        // NOTE: the shot-direction consistency check (projectile.forward vs the
        // direction we relay) was removed after it measured angle=0.000deg on 5/5 shots
        // (2026-11-12 23:00): the relayed direction is the final bullet direction.
    }

    [HarmonyPatch(typeof(ItemAgent_MeleeWeapon), "CheckAndDealDamage")]
    internal static class AiMeleeAnimPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ItemAgent_MeleeWeapon __instance)
        {
            try
            {
                var holder = __instance != null ? __instance.Holder : null;
                CoopRuntime.Active?.NoteAiAttack(holder != null ? holder.gameObject : null);
            }
            catch
            {
                // animation sampling is best effort
            }
        }
    }

    [HarmonyPatch(typeof(CharacterAnimationControl_MagicBlend), "OnAttack")]
    internal static class PlayerNativeAttackPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CharacterAnimationControl_MagicBlend __instance)
        {
            try
            {
                CoopRuntime.Active?.NotePlayerMelee(
                    __instance != null ? __instance.characterMainControl : null);
            }
            catch
            {
                // animation sampling is best effort
            }
        }
    }
}
