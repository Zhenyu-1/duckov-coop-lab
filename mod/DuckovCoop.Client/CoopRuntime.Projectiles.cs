using System;
using System.Collections.Generic;
using System.Reflection;
using DuckovCoop.Client;
using HarmonyLib;
using ItemStatsSystem;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// Visual-only thrown-projectile sync. The protocol is untouched: a throw reuses the
    /// existing CPlayerShot/SPlayerShot fact with a dedicated flag. The speed and fuse are
    /// carried in the existing BulletId/Seed fields, so remote clients can reproduce the
    /// owner's initial ballistic state with a native dropped-item rigidbody.
    ///
    /// This owns presentation only. Damage is still the authoritative SExplosion + hit-event
    /// path; the visual grenade is destroyed when that explosion arrives (or on a timeout).
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private const byte PlayerShotFlagThrow = 1 << 1;
        private static readonly FieldInfo GrenadeDelayTimer =
            AccessTools.Field(typeof(Grenade), "delayTimer");

        private sealed class RemoteProjectile
        {
            internal ushort Owner;
            internal Item Item;
            internal ItemAgent Agent;
            internal float SpawnAt;
            internal float FuseSeconds;
            internal float Lifetime;
        }

        private sealed class PendingThrow
        {
            internal Vector3 Start;
            internal Vector3 Velocity;
            internal CharacterMainControl Thrower;
            internal float At;
        }

        private readonly Dictionary<int, PendingThrow> _pendingGrenadeThrows =
            new Dictionary<int, PendingThrow>();
        private readonly Dictionary<ulong, RemoteProjectile> _remoteProjectiles =
            new Dictionary<ulong, RemoteProjectile>();
        private float _nextProjectileLogAt;
        private int _projectileLogCount;

        /// <summary>
        /// Grenade.Launch runs before SetWeaponIdInfo, so the item id is still 0 here. Park
        /// the launch state on the grenade instance; SetWeaponIdInfo completes the report.
        /// </summary>
        internal void NoteGrenadeLaunch(Grenade grenade, Vector3 startPoint, Vector3 velocity,
                                        CharacterMainControl thrower)
        {
            if (grenade == null || thrower == null)
                return;
            if (thrower != CharacterMainControl.Main && !thrower.IsMainCharacter)
                return;
            if (velocity.sqrMagnitude < 0.04f)
                return;

            _pendingGrenadeThrows[grenade.GetInstanceID()] = new PendingThrow
            {
                Start = startPoint,
                Velocity = velocity,
                Thrower = thrower,
                At = Time.unscaledTime,
            };

            PrunePendingThrows();
            if (_projectileLogCount < 12 && Time.unscaledTime >= _nextProjectileLogAt)
            {
                _nextProjectileLogAt = Time.unscaledTime + 1f;
                _projectileLogCount++;
                Log($"[proj] launch captured speed={velocity.magnitude:F2}m/s " +
                    $"pos=({startPoint.x:F2},{startPoint.y:F2},{startPoint.z:F2})");
            }
        }

        internal void CompleteGrenadeLaunch(Grenade grenade, int itemId)
        {
            if (grenade == null)
                return;
            if (itemId <= 0)
            {
                // Grenade.Launch ran but SetWeaponIdInfo never produced an item id: the
                // remote replica cannot be reported. Surface it instead of failing silently.
                if (_projectileLogCount < 12)
                {
                    _projectileLogCount++;
                    Log($"[proj] throw send skipped: item id missing grenade={grenade.name}");
                }
                return;
            }
            var key = grenade.GetInstanceID();
            if (!_pendingGrenadeThrows.TryGetValue(key, out var pending))
            {
                if (_projectileLogCount < 12)
                {
                    _projectileLogCount++;
                    Log($"[proj] throw send skipped: no captured launch item={itemId}");
                }
                return;
            }
            _pendingGrenadeThrows.Remove(key);
            if (pending == null || _client == null || _client.State != ClientState.InGame ||
                !_inRaidScene)
                return;

            var velocity = pending.Velocity;
            var startPoint = pending.Start;
            var direction = velocity.normalized;
            var speedCm = Mathf.Clamp(Mathf.RoundToInt(velocity.magnitude * 100f), 1, 6000);
            var delay = grenade.delayTime;
            if (delay <= 0f && GrenadeDelayTimer != null)
                delay = (float)GrenadeDelayTimer.GetValue(grenade);
            var fuseMs = Mathf.Clamp(Mathf.RoundToInt(delay * 1000f), 0, 60000);

            _client.SendPlayerShot(new PlayerShot
            {
                RaidEpoch = _client.RaidEpoch,
                ShotId = ++_playerShotSeq,
                WeaponId = unchecked((uint)itemId),
                BulletId = unchecked((uint)speedCm),
                OriginX = Mathf.RoundToInt(startPoint.x * 100f),
                OriginY = Mathf.RoundToInt(startPoint.y * 100f),
                OriginZ = Mathf.RoundToInt(startPoint.z * 100f),
                DirX = (short)Mathf.Clamp(Mathf.RoundToInt(direction.x * 32767f),
                    short.MinValue, short.MaxValue),
                DirY = (short)Mathf.Clamp(Mathf.RoundToInt(direction.y * 32767f),
                    short.MinValue, short.MaxValue),
                DirZ = (short)Mathf.Clamp(Mathf.RoundToInt(direction.z * 32767f),
                    short.MinValue, short.MaxValue),
                Seed = unchecked((uint)fuseMs),
                Flags = PlayerShotFlagThrow,
            });

            // One line per throw. This used to sit behind the shared 1s rate limit, which
            // swallowed it whenever "launch captured" had just logged in the same second
            // (2026-11-15: the throw looked unreported when it had actually been sent).
            Log($"[proj] throw send item={itemId} speed={speedCm}cm/s fuse={fuseMs}ms " +
                $"pos=({startPoint.x:F2},{startPoint.y:F2},{startPoint.z:F2})");
        }

        private void PrunePendingThrows()
        {
            if (_pendingGrenadeThrows.Count == 0)
                return;
            List<int> stale = null;
            foreach (var pair in _pendingGrenadeThrows)
            {
                if (Time.unscaledTime - pair.Value.At < 5f)
                    continue;
                if (stale == null)
                    stale = new List<int>();
                stale.Add(pair.Key);
            }
            if (stale == null)
                return;
            foreach (var key in stale)
                _pendingGrenadeThrows.Remove(key);
        }

        private void SpawnRemoteProjectile(ushort owner, PlayerShot shot, Vector3 origin,
                                           Vector3 direction)
        {
            if (direction.sqrMagnitude < 0.0001f || shot.WeaponId == 0)
                return;

            var key = ((ulong)owner << 32) | shot.ShotId;
            if (_remoteProjectiles.ContainsKey(key))
                return;

            var itemId = unchecked((int)shot.WeaponId);
            var speed = shot.BulletId / 100f;
            var fuse = shot.Seed / 1000f;
            if (speed < 0.5f || speed > 60f)
                speed = 12f;

            Item item = null;
            ItemAgent agent = null;
            try
            {
                item = ItemAssetsCollection.InstantiateSync(itemId);
                if (item == null)
                {
                    Log($"[proj] remote item missing item={itemId}");
                    return;
                }

                item.FromInfoKey = "Ground";
                agent = item.Drop(origin, true, direction, 360f);
                if (agent == null)
                {
                    UnityEngine.Object.Destroy(item.gameObject);
                    Log($"[proj] remote drop failed item={itemId}");
                    return;
                }

                // A dropped item is interactable by default; the flying replica must not be
                // pickable. Keep its colliders (ground collision), only kill the interaction.
                var pickup = agent.GetComponent<InteractablePickup>();
                if (pickup != null)
                    pickup.enabled = false;

                var rb = agent.GetComponent<Rigidbody>() ?? agent.GetComponentInChildren<Rigidbody>();
                if (rb == null)
                    rb = agent.gameObject.AddComponent<Rigidbody>();
                if (agent.GetComponentInChildren<Collider>(true) == null)
                {
                    var sphere = agent.gameObject.AddComponent<SphereCollider>();
                    sphere.radius = 0.08f;
                }
                rb.isKinematic = false;
                rb.useGravity = true;
                rb.velocity = direction * speed;
                rb.angularVelocity = UnityEngine.Random.onUnitSphere * 6f;

                _remoteProjectiles[key] = new RemoteProjectile
                {
                    Owner = owner,
                    Item = item,
                    Agent = agent,
                    SpawnAt = Time.time,
                    FuseSeconds = Mathf.Clamp(fuse, 0.5f, 30f),
                    Lifetime = Mathf.Clamp(fuse, 0.5f, 30f) + 6f,
                };

                if (_projectileLogCount < 12 && Time.unscaledTime >= _nextProjectileLogAt)
                {
                    _nextProjectileLogAt = Time.unscaledTime + 1f;
                    _projectileLogCount++;
                    Log($"[proj] remote spawn owner={owner} item={itemId} speed={speed:F2}m/s " +
                        $"fuse={fuse:F2}s pos=({origin.x:F2},{origin.y:F2},{origin.z:F2})");
                }
            }
            catch (Exception ex)
            {
                Log("[proj] remote spawn failed: " + ex.GetType().Name + ": " + ex.Message);
                if (agent != null)
                    UnityEngine.Object.Destroy(agent.gameObject);
                else if (item != null)
                    UnityEngine.Object.Destroy(item.gameObject);
            }
        }

        private void UpdateRemoteProjectiles()
        {
            if (!_inRaidScene)
            {
                if (_remoteProjectiles.Count > 0)
                    ClearRemoteProjectiles("left raid");
                return;
            }

            if (_remoteProjectiles.Count == 0)
                return;

            List<ulong> dead = null;
            foreach (var pair in _remoteProjectiles)
            {
                var projectile = pair.Value;
                if (projectile == null || projectile.Agent == null ||
                    Time.time - projectile.SpawnAt > projectile.Lifetime)
                {
                    if (dead == null)
                        dead = new List<ulong>();
                    dead.Add(pair.Key);
                }
            }

            if (dead == null)
                return;
            foreach (var key in dead)
                DestroyRemoteProjectile(key, "timeout");
        }

        private void DestroyRemoteProjectilesNear(uint sourceKind, uint sourceId, Vector3 center,
                                                  ushort radiusCm)
        {
            if (sourceKind != 0 || _remoteProjectiles.Count == 0)
                return;

            var reach = radiusCm / 100f + 4f;
            var reachSqr = reach * reach;
            List<ulong> dead = null;
            foreach (var pair in _remoteProjectiles)
            {
                var projectile = pair.Value;
                if (projectile == null || projectile.Owner != sourceId || projectile.Agent == null)
                    continue;
                if ((projectile.Agent.transform.position - center).sqrMagnitude > reachSqr)
                    continue;
                if (dead == null)
                    dead = new List<ulong>();
                dead.Add(pair.Key);
            }

            if (dead == null)
                return;
            foreach (var key in dead)
                DestroyRemoteProjectile(key, "explosion");
        }

        private void DestroyRemoteProjectile(ulong key, string reason)
        {
            if (!_remoteProjectiles.TryGetValue(key, out var projectile))
                return;
            _remoteProjectiles.Remove(key);
            try
            {
                if (projectile.Agent != null)
                    UnityEngine.Object.Destroy(projectile.Agent.gameObject);
                else if (projectile.Item != null)
                    UnityEngine.Object.Destroy(projectile.Item.gameObject);
            }
            catch
            {
                // The object may already be destroyed by the native drop cleanup.
            }
            Log($"[proj] destroy reason={reason} key={key}");
        }

        private void ClearRemoteProjectiles(string reason)
        {
            if (_remoteProjectiles.Count == 0)
                return;
            var keys = new List<ulong>(_remoteProjectiles.Keys);
            foreach (var key in keys)
                DestroyRemoteProjectile(key, reason);
        }
    }

    /// <summary>Installs the Grenade.Launch hook that reports the owner's throw.</summary>
    internal static class ProjectileSyncGuard
    {
        private const string HarmonyId = "duckovcoop.projectiles";
        private static bool _patched;

        internal static void Init()
        {
            if (_patched)
                return;
            _patched = true;
            try
            {
                var launch = AccessTools.Method(typeof(Grenade), "Launch");
                var weaponId = AccessTools.Method(typeof(Grenade), "SetWeaponIdInfo",
                    new[] { typeof(int) });
                if (launch == null || weaponId == null)
                {
                    CoopRuntime.Log("[proj] Grenade.Launch/SetWeaponIdInfo not found");
                    return;
                }
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(launch, postfix: new HarmonyMethod(typeof(ProjectileSyncGuard)
                    .GetMethod(nameof(LaunchPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(weaponId, postfix: new HarmonyMethod(typeof(ProjectileSyncGuard)
                    .GetMethod(nameof(WeaponIdPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                CoopRuntime.Log("[proj] grenade launch hooks installed");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[proj] hook failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void LaunchPostfix(Grenade __instance, Vector3 startPoint, Vector3 velocity,
                                          CharacterMainControl fromCharacter, bool canHurtSelf)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || __instance == null)
                return;
            runtime.NoteGrenadeLaunch(__instance, startPoint, velocity, fromCharacter);
        }

        private static void WeaponIdPostfix(Grenade __instance, int typeId)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || __instance == null)
                return;
            runtime.CompleteGrenadeLaunch(__instance, typeId);
        }
    }
}
