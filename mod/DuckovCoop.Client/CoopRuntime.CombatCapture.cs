using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DuckovCoop.Client;
using HarmonyLib;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// D5 hit capture (wire v4). Builds <see cref="HitEvent"/> records from the native
    /// damage path. Sending is intentionally disabled until the server runs wire v4:
    /// for now records are logged/queued so the capture can be validated in-game.
    /// Enabled by a `combat_capture.on` sentinel (mod dir or game log dir).
    /// </summary>
    internal static class CombatCapture
    {
        private const string SentinelName = "combat_capture.on";
        private const int MaxPending = 256;

        private static readonly List<HitEvent> Pending = new List<HitEvent>(MaxPending);
        private static CoopRuntime _runtime;
        private static bool _enabled;
        private static float _nextSentinelCheck;
        private static uint _nextHitId = 1;
        private static string _lastHitSignature = string.Empty;
        private static float _lastHitSignatureAt;
        private static bool _lastHitSent;
        private static float _nextMapMissLog;

        public static void AttachRuntime(CoopRuntime runtime) => _runtime = runtime;

        /// <summary>Set when the client is logged in; receives locally-resolved hits (D5).</summary>
        public static Func<HitEvent, bool> Transport;

        public static bool Enabled
        {
            get
            {
                RefreshSentinel();
                return _enabled;
            }
        }

        public static uint NextHitId() => _nextHitId++;

        public static HitEvent[] TakePending()
        {
            var copy = Pending.ToArray();
            Pending.Clear();
            return copy;
        }

        private static void RefreshSentinel()
        {
            // D5 v4: capture is always on (it only logs `hit-raw` offline; nothing is sent
            // and native damage is untouched unless Transport accepts the hit).
            // A `combat_capture.off` sentinel disables it for debugging.
            if (Time.unscaledTime < _nextSentinelCheck)
                return;
            _nextSentinelCheck = Time.unscaledTime + 5f;

            var candidates = new List<string>(3);
            try
            {
                var assemblyDir = Path.GetDirectoryName(typeof(CombatCapture).Assembly.Location);
                if (!string.IsNullOrEmpty(assemblyDir))
                    candidates.Add(assemblyDir);
            }
            catch
            {
                // ignore
            }

            candidates.Add(Application.persistentDataPath);
            foreach (var dir in candidates)
            {
                if (!File.Exists(Path.Combine(dir, "combat_capture.off")))
                    continue;
                if (_enabled)
                    CoopRuntime.Log("[combat] capture disabled by combat_capture.off");
                _enabled = false;
                return;
            }

            if (!_enabled)
                CoopRuntime.Log("[combat] capture enabled");
            _enabled = true;
        }
        public static bool Record(Health health, DamageInfo info)
        {
            if (_runtime == null || health == null)
                return false;

            try
            {
                var reporter = _runtime.LocalPlayerId;
                var source = info.fromCharacter;
                var unmapped = false;
                byte sourceKind;
                uint sourceId;

                if (source == null)
                {
                    sourceKind = 2; // environment
                    sourceId = 0;
                }
                else if (source.IsMainCharacter)
                {
                    sourceKind = 0;
                    sourceId = reporter;
                }
                else if (_runtime.TryResolveAiId(source.gameObject, out var aiSource))
                {
                    sourceKind = 1;
                    sourceId = aiSource;
                }
                else
                {
                    // Offline / not-yet-mapped AI: synthetic id so the payload can still be
                    // validated locally (marked `-raw` in the log, never sent as-is).
                    sourceKind = 1;
                    sourceId = SyntheticId(source.gameObject);
                    unmapped = true;
                }

                // Only the AI owner may report AI-sourced damage. A non-owner must also
                // never apply that damage natively to its own player: the host reports it
                // and the server sends the authoritative SCombatEvent (D5 HP clamp 1).
                if (sourceKind == 1 && !unmapped && !_runtime.OwnsAi(sourceId) &&
                    !health.IsMainCharacterHealth)
                    return false;

                byte targetKind;
                uint targetId;
                if (health.IsMainCharacterHealth)
                {
                    targetKind = 0;
                    targetId = reporter;
                }
                else if (_runtime.TryResolveAiId(health.gameObject, out var aiTarget))
                {
                    targetKind = 1;
                    targetId = aiTarget;
                }
                else if (_runtime.TryResolvePlayerIdFromObject(health.gameObject, out var remoteTarget))
                {
                    targetKind = 0;
                    targetId = remoteTarget;
                }
                else if (health.TryGetCharacter() != null)
                {
                    targetKind = 1;
                    targetId = SyntheticId(health.gameObject);
                    unmapped = true;
                }
                else
                {
                    return false; // props/targets are not D5 combatants
                }

                if (sourceKind == 1 && health.IsMainCharacterHealth && !_runtime.OwnsAi(sourceId))
                    return true; // non-owner: native AI damage to local player is not authoritative

                // Contract 2.16.0: non-AI damage to OUR OWN player (own grenade, fall,
                // environment) is settled through the CReportHit self channel
                // (World::ApplyHits selfReport), which ReportLocalDamage() feeds from the
                // native HP delta. Reporting the same bite as a D5 hit event as well would
                // apply it twice, so the native damage runs and only the self channel reports.
                if (targetKind == 0 && targetId == reporter && sourceKind != 1)
                    return false;

                if (sourceKind == 0 && targetKind == 1)
                    _runtime.NoteLocalHit(targetId);

                var ammoId = ResolveAmmoId(source);
                var isMelee = IsMelee(source);
                var hit = new HitEvent
                {
                    HitId = NextHitId(),
                    SourceKind = sourceKind,
                    SourceId = sourceId,
                    ReporterPlayerId = reporter,
                    TargetKind = targetKind,
                    TargetId = targetId,
                    WeaponId = unchecked((uint)info.fromWeaponItemID),
                    AmmoId = ammoId,
                    AttackId = 0,
                    HitX = ToCm(info.damagePoint.x),
                    HitY = ToCm(info.damagePoint.y),
                    HitZ = ToCm(info.damagePoint.z),
                };

                if (info.isExplosion)
                    hit.Flags |= HitEvent.FlagExplosion;
                if (isMelee)
                    hit.Flags |= HitEvent.FlagMelee;
                if (info.damageType == DamageTypes.realDamage)
                    hit.Flags |= HitEvent.FlagRealDamage;
                if (IsHeadshot(source))
                    hit.Flags |= HitEvent.FlagHeadshot;

                // Some native paths call Hurt twice for one projectile (two receivers);
                // send/suppress once and let the duplicate be suppressed silently.
                var signature = sourceKind + ":" + sourceId + ">" + targetKind + ":" + targetId +
                                ":" + hit.WeaponId + ":" + hit.AmmoId + ":" + hit.HitX + "," + hit.HitY + "," + hit.HitZ;
                if (signature == _lastHitSignature && Time.unscaledTime - _lastHitSignatureAt < 0.05f)
                {
                    // DamageReceiver.Hurt and Health.Hurt fire for the same projectile. Only
                    // suppress the second (native) call when the first copy was actually sent
                    // to the server. If it was an unmapped "hit-raw", native damage must run or
                    // the victim's health bar never moves.
                    return _lastHitSent;
                }
                _lastHitSignature = signature;
                _lastHitSignatureAt = Time.unscaledTime;
                _lastHitSent = false;

                if (Pending.Count >= MaxPending)
                    Pending.RemoveAt(0);
                Pending.Add(hit);

                CoopRuntime.Log($"[combat] {(unmapped ? "hit-raw" : "hit")} id={hit.HitId} src={Kind(sourceKind)}:{sourceId} " +
                                $"victim={Kind(targetKind)}:{targetId} weapon={hit.WeaponId} ammo={hit.AmmoId} " +
                                $"flags=0x{hit.Flags:X2} dmg={info.damageValue:F1} point=({hit.HitX},{hit.HitY},{hit.HitZ})");

                if (unmapped && Time.unscaledTime >= _nextMapMissLog)
                {
                    _nextMapMissLog = Time.unscaledTime + 2f;
                    CoopRuntime.Log($"[combat] map-miss target={DescribeObject(health.gameObject)} " +
                                    $"source={(source != null ? DescribeObject(source.gameObject) : "<env>")}");
                }

                if (!unmapped && Transport != null && Transport(hit))
                {
                    _lastHitSent = true;
                    CoopRuntime.Log($"[combat] sent hit id={hit.HitId}");
                    return true; // D5: native damage is replaced by the server result
                }
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[combat] capture failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return false;
        }

        private static string Kind(byte kind) => kind == 0 ? "player" : kind == 1 ? "ai" : "env";

        private static string DescribeObject(GameObject go)
        {
            if (go == null)
                return "<null>";

            var builder = new System.Text.StringBuilder(go.name);
            var parent = go.transform.parent;
            for (var depth = 0; parent != null && depth < 8; depth++)
            {
                builder.Append('/').Append(parent.name);
                parent = parent.parent;
            }

            return builder.ToString();
        }

        private static int ToCm(float meters) => Mathf.RoundToInt(meters * 100f);

        private static uint SyntheticId(GameObject go)
        {
            var instanceId = go != null ? go.GetInstanceID() : 0;
            return 0x80000000u | ((uint)instanceId & 0x7FFFFFFFu);
        }

        private static bool IsHeadshot(CharacterMainControl source)
        {
            try
            {
                if (source == null || !source.IsMainCharacter)
                    return false;
                var levelManager = LevelManager.Instance;
                var input = levelManager != null ? levelManager.InputManager : null;
                return input != null && input.AimingEnemyHead;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsMelee(CharacterMainControl source)
        {
            try
            {
                var agent = source != null ? source.CurrentHoldItemAgent : null;
                return agent != null && agent.GetType().Name.Contains("Melee");
            }
            catch
            {
                return false;
            }
        }

        private static uint ResolveAmmoId(CharacterMainControl source)
        {
            try
            {
                var agent = source != null ? source.CurrentHoldItemAgent : null;
                if (agent == null || !agent.GetType().Name.Contains("Gun"))
                    return 0;
                var bulletProperty = agent.GetType().GetProperty("BulletItem",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var bullet = bulletProperty?.GetValue(agent);
                if (bullet == null)
                    return 0;
                var typeId = bullet.GetType().GetProperty("TypeID",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(bullet);
                return typeId is int id && id > 0 ? (uint)id : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>DamageReceiver.Hurt runs before Health.Hurt; capture here so replica hits
    /// (suppressed by <see cref="AiReplicaGuard"/>) are still reported.</summary>
    [HarmonyPatch(typeof(DamageReceiver), "Hurt")]
    internal static class CombatCaptureReceiverHurtPatch
    {
        [HarmonyPriority(Priority.First)]
        [HarmonyPrefix]
        private static bool Prefix(DamageReceiver __instance, DamageInfo damageInfo)
        {
            if (!CombatCapture.Enabled || __instance == null || __instance.health == null)
                return true;
            return !CombatCapture.Record(__instance.health, damageInfo);
        }
    }

    /// <summary>Harmony entry: capture every native hit before it is applied.</summary>
    [HarmonyPatch(typeof(Health), "Hurt")]
    internal static class CombatCaptureHurtPatch
    {
        [HarmonyPriority(Priority.First)]
        [HarmonyPrefix]
        private static bool Prefix(Health __instance, DamageInfo damageInfo)
        {
            if (!CombatCapture.Enabled)
                return true;
            return !CombatCapture.Record(__instance, damageInfo);
        }
    }

    public sealed partial class CoopRuntime
    {
        internal ushort LocalPlayerId => _client != null ? _client.PlayerId : (ushort)0;

        private readonly Dictionary<GameObject, uint> _aiObjectIds = new Dictionary<GameObject, uint>();

        internal void RegisterAiObject(GameObject aiObject, uint aiId)
        {
            if (aiObject != null)
                _aiObjectIds[aiObject] = aiId;
        }

        internal void UnregisterAiObjects(uint aiId)
        {
            var stale = new List<GameObject>();
            foreach (var pair in _aiObjectIds)
            {
                if (pair.Value == aiId)
                    stale.Add(pair.Key);
            }
            foreach (var key in stale)
            {
                if (key != null)
                    UnityEngine.Object.Destroy(key);
                _aiObjectIds.Remove(key);
            }
        }

        /// <summary>True when this client simulates (owns) the given AI.</summary>
        internal bool OwnsAi(uint aiId) => _realAi.ContainsKey(aiId);

        /// <summary>Resolve a AI id from the cooperative AI dictionaries or the object name.</summary>
        internal bool TryResolveAiId(GameObject candidate, out uint aiId)
        {
            aiId = 0;
            if (candidate == null)
                return false;

            for (var transform = candidate.transform; transform != null; transform = transform.parent)
            {
                var go = transform.gameObject;
                if (_aiObjectIds.TryGetValue(go, out aiId))
                    return true;
                foreach (var pair in _realAi)
                {
                    if (pair.Value == go)
                    {
                        aiId = pair.Key;
                        return true;
                    }
                }

                foreach (var pair in _aiGhosts)
                {
                    if (pair.Value == go)
                    {
                        aiId = pair.Key;
                        return true;
                    }
                }
            }

            for (var transform = candidate.transform; transform != null; transform = transform.parent)
            {
                var name = transform.name;
                if (name.StartsWith("CoopAi_", StringComparison.Ordinal) &&
                    TryParseIdPrefix(name.Substring("CoopAi_".Length), out aiId))
                    return true;
                if (name.StartsWith("DuckovCoop_AiReplica_", StringComparison.Ordinal) &&
                    TryParseIdPrefix(name.Substring("DuckovCoop_AiReplica_".Length), out aiId))
                    return true;
            }

            return false;
        }

        private static bool TryParseIdPrefix(string value, out uint id)
        {
            id = 0;
            var length = 0;
            while (length < value.Length && char.IsDigit(value[length]))
                length++;
            return length > 0 && uint.TryParse(value.Substring(0, length), out id);
        }

        /// <summary>Resolve a remote player id from a replica/proxy object name.</summary>
        internal bool TryResolvePlayerIdFromObject(GameObject candidate, out uint playerId)
        {
            playerId = 0;
            if (candidate == null)
                return false;

            for (var transform = candidate.transform; transform != null; transform = transform.parent)
            {
                var name = transform.name;
                const string prefix = "DuckovCoop_Remote_";
                if (name.StartsWith(prefix, StringComparison.Ordinal) &&
                    uint.TryParse(name.Substring(prefix.Length), out playerId))
                    return true;
            }

            return false;
        }
    }
}
