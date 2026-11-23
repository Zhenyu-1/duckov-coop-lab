using System;
using System.Collections.Generic;
using System.Reflection;
using DuckovCoop.Client;
using Duckov.Utilities;
using HarmonyLib;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// M2-v12 / contract 2.18.0: presentation-only bullet impact sync.
    ///
    /// The shooter owns hit resolution (the native <see cref="Projectile"/> really flies and
    /// collides), so it reports where a projectile died on a world surface. Other clients replay
    /// the same native hit FX with the per-bullet prefab. Actor hits are never reported here:
    /// they already travel through <c>SCombatEvent</c> (see <c>CoopRuntime.CombatD5</c>).
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        /// <summary>A projectile hit point is only trusted when a RaycastHit sits this close.</summary>
        private const float ImpactHitMatchSqr = 0.0025f; // 5 cm

        private static FieldInfo _projectileDeadField;
        private static FieldInfo _projectileHitsField;
        private static FieldInfo _projectileHitPointField;
        private static FieldInfo _projectileHitFxField;
        private static bool _projectileFieldsResolved;

        private readonly HashSet<int> _impactReported = new HashSet<int>();
        private readonly Dictionary<int, GameObject> _impactFxByPrefab = new Dictionary<int, GameObject>();
        private uint _impactShotSeq = unchecked((uint)Environment.TickCount);
        private int _impactLogCount;
        private bool _impactFxEnabled = true;
        private bool _impactFxConfigLoaded;

        /// <summary>Called from the <c>Projectile.UpdateMoveAndCheck</c> postfix.</summary>
        internal void NoteProjectileDied(Projectile projectile)
        {
            if (projectile == null || _client == null || !ImpactFxEnabled())
                return;
            try
            {
                EnsureProjectileFields();
                if (_projectileDeadField == null ||
                    !(bool)_projectileDeadField.GetValue(projectile))
                    return;
                if (_impactReported.Contains(projectile.GetInstanceID()))
                    return;

                var context = projectile.context;
                var shooter = context.realFromCharacter;
                if (shooter == null)
                    return;

                byte sourceKind;
                uint sourceId;
                if (shooter == CharacterMainControl.Main || shooter.IsMainCharacter)
                {
                    if (_client.PlayerId == 0)
                        return;
                    sourceKind = 0;
                    sourceId = _client.PlayerId;
                }
                else if (TryResolveAiId(shooter.gameObject, out var aiId) && OwnsAi(aiId))
                {
                    sourceKind = 1;
                    sourceId = aiId;
                }
                else
                {
                    return;
                }

                var hits = _projectileHitsField?.GetValue(projectile) as List<RaycastHit>;
                if (hits == null || hits.Count == 0 || _projectileHitPointField == null)
                    return;
                var hitPoint = (Vector3)_projectileHitPointField.GetValue(projectile);

                var best = default(RaycastHit);
                var bestSqr = float.MaxValue;
                for (var i = 0; i < hits.Count; i++)
                {
                    var distance = (hits[i].point - hitPoint).sqrMagnitude;
                    if (distance >= bestSqr)
                        continue;
                    bestSqr = distance;
                    best = hits[i];
                }
                if (bestSqr > ImpactHitMatchSqr || best.collider == null)
                    return;

                // Actor hits are settled by SCombatEvent; only world surfaces need this channel.
                if (GameplayDataSettings.LayersData.IsLayerInLayerMask(
                        best.collider.gameObject.layer,
                        GameplayDataSettings.Layers.damageReceiverLayerMask))
                    return;

                var blocked = best.collider.GetComponent<BulletBlocker>() != null;

                _client.SendImpactFx(new ImpactFx
                {
                    RaidEpoch = _client.RaidEpoch,
                    SourceKind = sourceKind,
                    SourceId = sourceId,
                    WeaponId = unchecked((uint)context.fromWeaponItemID),
                    ShotId = ++_impactShotSeq,
                    X = Mathf.RoundToInt(hitPoint.x * 100f),
                    Y = Mathf.RoundToInt(hitPoint.y * 100f),
                    Z = Mathf.RoundToInt(hitPoint.z * 100f),
                    NX = ToWireNormal(best.normal.x),
                    NY = ToWireNormal(best.normal.y),
                    NZ = ToWireNormal(best.normal.z),
                    Flags = blocked ? ImpactFx.FlagBlocked : (byte)0,
                });
                _impactReported.Add(projectile.GetInstanceID());

                if (_impactLogCount < 24)
                {
                    _impactLogCount++;
                    Log($"[impact] send src={sourceKind}:{sourceId} weapon={context.fromWeaponItemID} " +
                        $"at=({hitPoint.x:F1},{hitPoint.y:F1},{hitPoint.z:F1}) " +
                        $"n=({best.normal.x:F2},{best.normal.y:F2},{best.normal.z:F2}) blocked={blocked}");
                }
            }
            catch (Exception ex)
            {
                if (_impactLogCount < 24)
                {
                    _impactLogCount++;
                    Log("[impact] capture failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>Called from the <c>Projectile.Init(context)</c> postfix; projectiles are pooled.</summary>
        internal void ResetProjectileReport(Projectile projectile)
        {
            if (projectile != null)
                _impactReported.Remove(projectile.GetInstanceID());
        }

        private void UpdateImpactFx()
        {
            // Visual bullets destroy themselves after 1.2s; drop the dead entries even when no
            // impact arrives (e.g. the impact packet was lost).
            for (var i = _visualBullets.Count - 1; i >= 0; i--)
                if (_visualBullets[i] == null)
                    _visualBullets.RemoveAt(i);
            if (_visualBullets.Count > 256)
                _visualBullets.Clear();

            if (_client == null)
                return;
            while (_client.ImpactFxs.Count > 0)
                ApplyRemoteImpactFx(_client.ImpactFxs.Dequeue());
        }

        private void ApplyRemoteImpactFx(ImpactFx impact)
        {
            try
            {
                if (_client == null || impact == null)
                    return;
                // Our own report is echoed back; the native projectile already played the FX.
                if (impact.SourceKind == 0 && impact.SourceId == _client.PlayerId)
                    return;
                // Same for AI we own: the native projectile hit and played the FX on this host.
                if (impact.SourceKind == 1 && OwnsAi(impact.SourceId))
                    return;

                var fx = ResolveImpactFx(impact);
                if (fx == null)
                    return;

                var point = new Vector3(impact.X / 100f, impact.Y / 100f, impact.Z / 100f);
                var normal = new Vector3(impact.NX / 32767f, impact.NY / 32767f, impact.NZ / 32767f);
                if (normal.sqrMagnitude < 0.0001f)
                    normal = Vector3.up;
                RetireVisualBullet(impact, point);
                UnityEngine.Object.Instantiate(fx, point,
                    Quaternion.LookRotation(normal.normalized, Vector3.up));

                if (_impactLogCount < 24)
                {
                    _impactLogCount++;
                    Log($"[impact] apply src={impact.SourceKind}:{impact.SourceId} " +
                        $"weapon={impact.WeaponId} shot={impact.ShotId} " +
                        $"at=({point.x:F1},{point.y:F1},{point.z:F1}) blocked=" +
                        $"{(impact.Flags & ImpactFx.FlagBlocked) != 0}");
                }
            }
            catch (Exception ex)
            {
                if (_impactLogCount < 24)
                {
                    _impactLogCount++;
                    Log("[impact] apply failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// DQ-C: stop the remote visual bullet at the reported impact point instead of letting
        /// it fly through the wall for its full lifetime. Bullets from the same shooter are
        /// matched by their firing ray (the one that would reach the hit point first wins).
        /// </summary>
        private void RetireVisualBullet(ImpactFx impact, Vector3 point)
        {
            AiVisualBullet best = null;
            var bestAlong = float.MaxValue;
            for (var i = _visualBullets.Count - 1; i >= 0; i--)
            {
                var bullet = _visualBullets[i];
                if (bullet == null)
                {
                    _visualBullets.RemoveAt(i);
                    continue;
                }
                if (bullet.SourceKind != impact.SourceKind || bullet.SourceId != impact.SourceId)
                    continue;
                var direction = bullet.Velocity;
                if (direction.sqrMagnitude < 0.0001f)
                    continue;
                direction.Normalize();
                var toPoint = point - bullet.Origin;
                var along = Vector3.Dot(toPoint, direction);
                if (along < 0f)
                    continue;
                var perpendicular = (toPoint - direction * along).sqrMagnitude;
                if (perpendicular > 1f) // within 1 m of the firing ray
                    continue;
                if (along >= bestAlong)
                    continue;
                bestAlong = along;
                best = bullet;
            }

            if (best == null)
                return;
            _visualBullets.Remove(best);
            best.transform.position = point;
            UnityEngine.Object.Destroy(best.gameObject);
        }

        private GameObject ResolveImpactFx(ImpactFx impact)
        {
            var blocked = (impact.Flags & ImpactFx.FlagBlocked) != 0;
            if (blocked && GameplayDataSettings.Prefabs.BlockBulletFx != null)
                return GameplayDataSettings.Prefabs.BlockBulletFx;

            if (impact.WeaponId != 0)
            {
                var gun = ResolveShooterGun(impact.SourceKind, impact.SourceId);
                var prefab = gun != null && gun.GunItemSetting != null
                    ? gun.GunItemSetting.bulletPfb
                    : null;
                if (prefab != null)
                {
                    var key = prefab.GetInstanceID();
                    if (_impactFxByPrefab.TryGetValue(key, out var cached))
                    {
                        if (cached != null)
                            return cached;
                    }
                    else
                    {
                        var template = prefab.GetComponent<Projectile>();
                        var fx = template != null && _projectileHitFxField != null
                            ? _projectileHitFxField.GetValue(template) as GameObject
                            : null;
                        _impactFxByPrefab[key] = fx;
                        if (fx != null)
                            return fx;
                    }
                }
            }

            return GameplayDataSettings.Prefabs.BulletHitObsticleFx;
        }

        private ItemAgent_Gun ResolveShooterGun(byte sourceKind, uint sourceId)
        {
            GameObject owner = null;
            if (sourceKind == 0)
            {
                if (_remotes.TryGetValue((ushort)sourceId, out var remote))
                    owner = remote;
            }
            else if (sourceKind == 1)
            {
                if (!_realAi.TryGetValue(sourceId, out owner) || owner == null)
                    _aiGhosts.TryGetValue(sourceId, out owner);
            }

            var control = owner != null
                ? owner.GetComponentInChildren<CharacterMainControl>(true)
                : null;
            return control != null ? control.CurrentHoldItemAgent as ItemAgent_Gun : null;
        }

        private bool ImpactFxEnabled()
        {
            if (_impactFxConfigLoaded)
                return _impactFxEnabled;
            _impactFxConfigLoaded = true;
            try
            {
                var directory = System.IO.Path.GetDirectoryName(
                    typeof(CoopRuntime).Assembly.Location) ?? string.Empty;
                var path = System.IO.Path.Combine(directory, "coop.ini");
                if (!System.IO.File.Exists(path))
                    return _impactFxEnabled;
                var section = string.Empty;
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                        continue;
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        section = trimmed.Substring(1, trimmed.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }
                    var split = trimmed.IndexOf('=');
                    if (split <= 0 || section != "debug")
                        continue;
                    var key = trimmed.Substring(0, split).Trim().ToLowerInvariant();
                    if (key != "impactfx")
                        continue;
                    var value = trimmed.Substring(split + 1).Trim();
                    _impactFxEnabled = !(value == "0" || value.Equals("false",
                        StringComparison.OrdinalIgnoreCase));
                }
                Log("[impact] config impactFx=" + (_impactFxEnabled ? "1" : "0"));
            }
            catch
            {
                // keep the default (enabled)
            }
            return _impactFxEnabled;
        }

        private static short ToWireNormal(float value)
        {
            return (short)Mathf.Clamp(Mathf.RoundToInt(value * 32767f), short.MinValue,
                short.MaxValue);
        }

        private static void EnsureProjectileFields()
        {
            if (_projectileFieldsResolved)
                return;
            _projectileFieldsResolved = true;
            _projectileDeadField = AccessTools.Field(typeof(Projectile), "dead");
            _projectileHitsField = AccessTools.Field(typeof(Projectile), "hits");
            _projectileHitPointField = AccessTools.Field(typeof(Projectile), "hitPoint");
            _projectileHitFxField = AccessTools.Field(typeof(Projectile), "hitFx");
        }
    }

    /// <summary>
    /// Installs the <see cref="Projectile"/> hooks. The hit branch lives inside the private
    /// <c>UpdateMoveAndCheck</c>; a postfix reads the resolved hit without re-running physics.
    /// </summary>
    internal static class ImpactFxGuard
    {
        private const string HarmonyId = "duckovcoop.impactfx";
        private static bool _patched;

        internal static void Init()
        {
            if (_patched)
                return;
            _patched = true;
            try
            {
                var tick = AccessTools.Method(typeof(Projectile), "UpdateMoveAndCheck");
                var init = AccessTools.Method(typeof(Projectile), "Init",
                    new[] { typeof(ProjectileContext) });
                if (tick == null || init == null)
                {
                    CoopRuntime.Log("[impact] Projectile.UpdateMoveAndCheck/Init not found");
                    return;
                }
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(tick, postfix: new HarmonyMethod(typeof(ImpactFxGuard)
                    .GetMethod(nameof(TickPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(init, postfix: new HarmonyMethod(typeof(ImpactFxGuard)
                    .GetMethod(nameof(InitPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                CoopRuntime.Log("[impact] projectile hooks installed");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[impact] hook failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void TickPostfix(Projectile __instance)
        {
            var runtime = CoopRuntime.Active;
            if (runtime != null)
                runtime.NoteProjectileDied(__instance);
        }

        private static void InitPostfix(Projectile __instance)
        {
            var runtime = CoopRuntime.Active;
            if (runtime != null)
                runtime.ResetProjectileReport(__instance);
        }
    }
}
