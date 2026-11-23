using System;
using System.Collections.Generic;
using System.Reflection;
using DuckovCoop.Client;
using HarmonyLib;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// M2-v10 scene objects + explosion presentation.
    ///
    /// Doors and Breakables are local objects in the base game: nothing tells other clients
    /// that one was opened or destroyed, and a grenade explosion only exists on the thrower's
    /// machine. This file funnels those local facts through the server:
    ///
    ///   Door.SetClosed        -> CSceneObjectState -> server map -> SObjectState (broadcast
    ///                            + replay on spawn)
    ///   Breakable.OnDead      -> CSceneObjectState (irreversible)
    ///   ExplosionManager      -> CExplosion -> SExplosion (FX only for non-throwers; the
    ///   .CreateExplosion         server also uses it as the damage source for the
    ///                            explosion-flagged hit events that follow it)
    ///
    /// Keys are opaque client-side hashes (kind + 10cm-quantized position); both clients see
    /// the same authored scene, so they compute the same key for the same object.
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private const string SceneObjectHarmonyId = "duckov.coop.sceneobjects";
        private static bool _sceneObjectPatched;
        private static CoopRuntime _sceneObjectRuntime;
        private static bool _applyingRemoteSceneObject;
        private static bool _applyingRemoteExplosion;

        private readonly Dictionary<uint, Door> _sceneDoors = new Dictionary<uint, Door>();
        private readonly Dictionary<uint, object> _sceneBreakables = new Dictionary<uint, object>();
        private readonly Dictionary<uint, HalfObsticle> _sceneCovers =
            new Dictionary<uint, HalfObsticle>();
        private readonly Dictionary<uint, byte> _sceneObjectStates = new Dictionary<uint, byte>();
        private string _sceneObjectScanScene;
        private float _sceneObjectNextScan;
        private int _sceneObjectLogs;

        internal static uint SceneObjectKey(byte kind, Vector3 position)
        {
            var x = Mathf.RoundToInt(position.x * 10f);
            var y = Mathf.RoundToInt(position.y * 10f);
            var z = Mathf.RoundToInt(position.z * 10f);
            unchecked
            {
                var hash = 2166136261u;
                hash = (hash ^ kind) * 16777619u;
                hash = (hash ^ (uint)x) * 16777619u;
                hash = (hash ^ (uint)y) * 16777619u;
                hash = (hash ^ (uint)z) * 16777619u;
                return hash;
            }
        }

        private void EnsureSceneObjectPatches()
        {
            _sceneObjectRuntime = this;
            if (_sceneObjectPatched)
                return;
            _sceneObjectPatched = true;
            try
            {
                var harmony = new Harmony(SceneObjectHarmonyId);
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var doorSet = AccessTools.Method(typeof(Door), "SetClosed");
                if (doorSet != null)
                    harmony.Patch(doorSet,
                        postfix: new HarmonyMethod(typeof(CoopRuntime).GetMethod(
                            nameof(OnDoorSetClosed), flags)));
                var breakableDead = AccessTools.Method(typeof(Breakable), "OnDead");
                if (breakableDead != null)
                    harmony.Patch(breakableDead,
                        postfix: new HarmonyMethod(typeof(CoopRuntime).GetMethod(
                            nameof(OnBreakableDead), flags)));
                var explosion = AccessTools.Method(typeof(ExplosionManager), "CreateExplosion");
                if (explosion != null)
                {
                    harmony.Patch(explosion,
                        prefix: new HarmonyMethod(typeof(CoopRuntime).GetMethod(
                            nameof(OnCreateExplosion), flags)));
                    harmony.Patch(explosion,
                        postfix: new HarmonyMethod(typeof(CoopRuntime).GetMethod(
                            nameof(OnCreateExplosionPostfix), flags)));
                }
                var breakableAwake = AccessTools.Method(typeof(Breakable), "Awake");
                if (breakableAwake != null)
                    harmony.Patch(breakableAwake,
                        prefix: new HarmonyMethod(typeof(CoopRuntime).GetMethod(
                            nameof(OnBreakableAwake), flags)));
                var coverDead = AccessTools.Method(typeof(HalfObsticle), "Dead");
                if (coverDead != null)
                    harmony.Patch(coverDead,
                        postfix: new HarmonyMethod(typeof(CoopRuntime).GetMethod(
                            nameof(OnCoverDead), flags)));
                Log("[sceneobj] patches ready");
            }
            catch (Exception ex)
            {
                Log("[sceneobj] patch failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void UpdateSceneObjects()
        {
            EnsureSceneObjectPatches();
            EnsureSceneObjectsScanned();
            if (_client == null)
                return;

            while (_client.SceneObjectUpdates.Count > 0)
                ApplySceneObjectState(_client.SceneObjectUpdates.Dequeue());
            while (_client.ExplosionEvents.Count > 0)
                ApplyRemoteExplosion(_client.ExplosionEvents.Dequeue());
        }

        private void EnsureSceneObjectsScanned()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene == _sceneObjectScanScene && Time.unscaledTime < _sceneObjectNextScan)
                return;
            var sceneChanged = scene != _sceneObjectScanScene;
            _sceneObjectNextScan = Time.unscaledTime + 5f;
            _sceneObjectScanScene = scene;
            _sceneDoors.Clear();
            _sceneBreakables.Clear();
            _sceneCovers.Clear();
            foreach (var door in UnityEngine.Object.FindObjectsOfType<Door>())
            {
                if (door == null)
                    continue;
                _sceneDoors[SceneObjectKey(SceneObjectState.KindDoor, door.transform.position)] = door;
            }
            foreach (var breakable in UnityEngine.Object.FindObjectsOfType<Breakable>())
            {
                if (breakable == null)
                    continue;
                _sceneBreakables[SceneObjectKey(SceneObjectState.KindBreakable,
                    breakable.transform.position)] = breakable;
            }
            foreach (var cover in UnityEngine.Object.FindObjectsOfType<HalfObsticle>())
            {
                if (cover == null)
                    continue;
                _sceneCovers[SceneObjectKey(SceneObjectState.KindCover, cover.transform.position)] =
                    cover;
            }
            // Log every scene change (not a global cap): the old cap was consumed by the
            // menu/base scenes, which hid the raid counts entirely.
            if (sceneChanged)
                Log($"[sceneobj] scanned scene={scene} doors={_sceneDoors.Count} " +
                    $"breakables={_sceneBreakables.Count} covers={_sceneCovers.Count}");
        }

        private void ApplySceneObjectState(SceneObjectState state)
        {
            _sceneObjectStates[state.Key] = state.State;
            try
            {
                if (state.Kind == SceneObjectState.KindDoor)
                {
                    if (!_sceneDoors.TryGetValue(state.Key, out var door) || door == null)
                        return;
                    var open = state.State == 1;
                    if (door.IsOpen == open)
                        return;
                    _applyingRemoteSceneObject = true;
                    door.ForceSetClosed(!open, false);
                }
                else if (state.Kind == SceneObjectState.KindBreakable)
                {
                    if (!_sceneBreakables.TryGetValue(state.Key, out var breakable) ||
                        breakable == null)
                        return;
                    ApplyBreakableBreaked(breakable);
                }
                else if (state.Kind == SceneObjectState.KindCover)
                {
                    if (!_sceneCovers.TryGetValue(state.Key, out var cover) || cover == null)
                        return;
                    ApplyCoverDead(cover);
                }
                if (_sceneObjectLogs++ < 40)
                    Log($"[sceneobj] applied kind={state.Kind} key={state.Key} " +
                        $"state={state.State}");
            }
            catch (Exception ex)
            {
                Log("[sceneobj] apply failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _applyingRemoteSceneObject = false;
            }
        }

        private static void ApplyBreakableBreaked(object breakable)
        {
            if (breakable is not Component component || component == null)
                return;
            MapVariantSetEnumMember(component, "breakableState", "Breaked");
            MapVariantSetGameObjectMember(component, "normalVisual", false);
            MapVariantSetGameObjectMember(component, "dangerVisual", false);
            MapVariantSetGameObjectMember(component, "breakedVisual", true);
            MapVariantDisableColliderMember(component, "mainCollider");
            MapVariantDisableComponentMember(component, "simpleHealth");
            var receiver = component.GetComponentInChildren<DamageReceiver>(true);
            if (receiver != null)
                receiver.enabled = false;
        }

        private static void ApplyCoverDead(HalfObsticle cover)
        {
            if (cover == null)
                return;
            try
            {
                AccessTools.Field(typeof(HalfObsticle), "dead")?.SetValue(cover, true);
            }
            catch
            {
                // field may be renamed; visuals still apply below
            }
            if (cover.defaultVisuals != null)
                cover.defaultVisuals.SetActive(false);
            if (cover.deadVisuals != null)
                cover.deadVisuals.SetActive(true);
        }

        /// <summary>
        /// Coop disables the native save load/store for breakables. The local save decided
        /// whether a barrel was already broken, so the two clients could see different
        /// barrel counts at the same position ("刷新数量对不上"). Server state is the only
        /// authority now; every raid starts from the authored scene.
        /// </summary>
        private static void OnBreakableAwake(Breakable __instance)
        {
            if (__instance == null)
                return;
            __instance.save = false;
        }

        private static void OnCoverDead(HalfObsticle __instance)
        {
            var runtime = _sceneObjectRuntime;
            if (runtime == null || _applyingRemoteSceneObject || __instance == null)
                return;
            if (runtime._client == null || runtime._client.State != ClientState.InGame)
                return;
            var key = SceneObjectKey(SceneObjectState.KindCover, __instance.transform.position);
            runtime._client.SendSceneObjectState(SceneObjectState.KindCover, key, 1,
                runtime._client.RaidEpoch);
        }

        /// <summary>
        /// Diagnostics for "barrel explosion did no damage": reports exactly which Health
        /// instances the native sphere found (the ExplosionManager only fills this list for
        /// targets that actually have a Health; breakables use HealthSimpleBase and are not
        /// listed).
        /// </summary>
        private static void OnCreateExplosionPostfix(ExplosionManager __instance)
        {
            var runtime = _sceneObjectRuntime;
            if (runtime == null || runtime._client == null || _clientLogBudget(runtime) <= 0)
                return;
            try
            {
                var list = AccessTools.Field(typeof(ExplosionManager), "damagedHealth")
                    ?.GetValue(__instance) as System.Collections.IList;
                var count = list != null ? list.Count : -1;
                var names = string.Empty;
                if (list != null)
                {
                    var limit = Mathf.Min(count, 5);
                    for (var i = 0; i < limit; i++)
                    {
                        var health = list[i] as Health;
                        names += (i > 0 ? "," : string.Empty) +
                                 (health != null ? health.gameObject.name : "null");
                    }
                }
                CoopRuntime.Log($"[sceneobj] explosion victims={count} names={names}");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[sceneobj] victim diag failed: " + ex.GetType().Name);
            }
        }

        private static int _clientLogBudget(CoopRuntime runtime)
        {
            if (runtime._sceneObjectLogs >= 60)
                return 0;
            runtime._sceneObjectLogs++;
            return 1;
        }


        private static void OnDoorSetClosed(Door __instance)
        {
            var runtime = _sceneObjectRuntime;
            if (runtime == null || _applyingRemoteSceneObject || __instance == null)
                return;
            if (runtime._client == null || runtime._client.State != ClientState.InGame)
                return;
            var key = SceneObjectKey(SceneObjectState.KindDoor, __instance.transform.position);
            runtime._client.SendSceneObjectState(SceneObjectState.KindDoor, key,
                (byte)(__instance.IsOpen ? 1 : 0), runtime._client.RaidEpoch);
        }

        private static void OnBreakableDead(Breakable __instance)
        {
            var runtime = _sceneObjectRuntime;
            if (runtime == null || _applyingRemoteSceneObject || __instance == null)
                return;
            if (runtime._client == null || runtime._client.State != ClientState.InGame)
                return;
            var key = SceneObjectKey(SceneObjectState.KindBreakable, __instance.transform.position);
            runtime._client.SendSceneObjectState(SceneObjectState.KindBreakable, key, 1,
                runtime._client.RaidEpoch);
        }

        private static void OnCreateExplosion(Vector3 center, float radius, DamageInfo dmgInfo)
        {
            var runtime = _sceneObjectRuntime;
            if (runtime == null || _applyingRemoteExplosion)
                return;
            if (runtime._client == null || runtime._client.State != ClientState.InGame)
                return;
            try
            {
                var source = dmgInfo.fromCharacter;
                byte sourceKind;
                uint sourceId;
                if (source == null)
                    return;
                if (source.IsMainCharacter)
                {
                    sourceKind = 0;
                    sourceId = runtime._client.PlayerId;
                }
                else if (runtime.TryResolveAiId(source.gameObject, out var aiId))
                {
                    sourceKind = 1;
                    sourceId = aiId;
                }
                else
                {
                    return;
                }

                runtime._client.SendExplosion(new ExplosionEvent
                {
                    X = Mathf.RoundToInt(center.x * 100f),
                    Y = Mathf.RoundToInt(center.y * 100f),
                    Z = Mathf.RoundToInt(center.z * 100f),
                    RadiusCm = (ushort)Mathf.Clamp(Mathf.RoundToInt(radius * 100f), 0, 65535),
                    Damage = (ushort)Mathf.Clamp(Mathf.RoundToInt(dmgInfo.damageValue), 0, 65535),
                    WeaponId = unchecked((uint)dmgInfo.fromWeaponItemID),
                    SourceKind = sourceKind,
                    SourceId = sourceId,
                    FxType = 0,
                    RaidEpoch = runtime._client.RaidEpoch,
                });
            }
            catch (Exception ex)
            {
                Log("[sceneobj] explosion report failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void ApplyRemoteExplosion(ExplosionEvent explosion)
        {
            try
            {
                if (_sceneObjectLogs++ < 60)
                    Log($"[sceneobj] explosion recv id={explosion.ExplosionId} " +
                        $"at=({explosion.X},{explosion.Y},{explosion.Z}) " +
                        $"radius={explosion.RadiusCm} damage={explosion.Damage} " +
                        $"source={explosion.SourceKind}:{explosion.SourceId} fx={explosion.FxType}");
                // The thrower already played the native explosion locally; only replay the
                // FX for the other clients (and never for our own AI-owned explosions).
                if (explosion.SourceKind == 0 && _client != null &&
                    explosion.SourceId == _client.PlayerId)
                    return;
                if (explosion.SourceKind == 1 && OwnsAi(explosion.SourceId))
                    return;

                var center = new Vector3(explosion.X / 100f, explosion.Y / 100f,
                    explosion.Z / 100f);
                // Presentation-only grenade replicas die with their authoritative blast.
                DestroyRemoteProjectilesNear(explosion.SourceKind, explosion.SourceId, center,
                    explosion.RadiusCm);
                var main = LocalMainCharacter();
                if (main != null && Vector3.Distance(center, main.transform.position) < 30f)
                {
                    var direction = (center - main.transform.position).normalized;
                    CameraShaker.Shake(direction * 0.4f, CameraShaker.CameraShakeTypes.explosion);
                }

                var manager = LevelManager.Instance != null
                    ? LevelManager.Instance.ExplosionManager
                    : null;
                if (manager == null)
                    return;
                var prefab = explosion.FxType == 1 ? manager.flashFxPfb : manager.normalFxPfb;
                if (prefab != null)
                    UnityEngine.Object.Instantiate(prefab, center, Quaternion.identity);
                if (_sceneObjectLogs++ < 20)
                    Log($"[sceneobj] remote explosion at={center} radius={explosion.RadiusCm} " +
                        $"damage={explosion.Damage} source={explosion.SourceKind}:{explosion.SourceId}");
            }
            catch (Exception ex)
            {
                Log("[sceneobj] explosion apply failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
