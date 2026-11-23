using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DuckovCoop.Client;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private readonly Dictionary<uint, ushort> _appliedAiTargets = new Dictionary<uint, ushort>();
        private static MethodInfo _setAiTargetMethod;
        private static bool _setAiTargetLookupDone;
        private static FieldInfo _aiSearchedEnemyField;
        private static bool _aiSearchedEnemyLookupDone;
        private readonly Dictionary<ushort, RemoteAiProxy> _remoteAiProxies = new Dictionary<ushort, RemoteAiProxy>();
        private readonly Dictionary<uint, RemoteTargetState> _remoteTargetStates = new Dictionary<uint, RemoteTargetState>();
        private readonly Dictionary<uint, GameObject> _aiGhosts = new Dictionary<uint, GameObject>();
        private readonly HashSet<int> _aiGhostOwnersSeenInRaid = new HashSet<int>();
        private readonly HashSet<uint> _processedAiSpawns = new HashSet<uint>();
        private readonly HashSet<ulong> _processedAiDespawns = new HashSet<ulong>();
        // M2-v11: server-provided per-AI streaming radii (metres). 0 / missing = default.
        private readonly Dictionary<uint, float> _aiActivationRadius = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> _aiDeactivationRadius = new Dictionary<uint, float>();
        private uint _aiSpawnEpoch;
        private bool _aiSpawnReplayPending;
        private int _pendingRealAiSpawns;
        private readonly Dictionary<uint, GameObject> _realAi = new Dictionary<uint, GameObject>();
        private readonly Dictionary<uint, object> _realAiHealth = new Dictionary<uint, object>();
        private readonly Dictionary<uint, float> _realAiLastHp = new Dictionary<uint, float>();
        private float _nextAiStateSend;
        // 2026-11-16 diagnostics: is the AI actually translating on the host / client?
        private readonly Dictionary<uint, Vector3> _realAiDiagPos = new Dictionary<uint, Vector3>();
        private readonly Dictionary<uint, Vector3> _ghostDiagTarget = new Dictionary<uint, Vector3>();
        private float _nextAiMotionLog;
        // 2026-11-16: the client motion probe used _nextAiMotionLog, which UpdateNativeAi
        // consumes every 3 s even with zero real AI, so non-owner clients never logged it.
        private float _nextClientMotionLog;
        private int _lastAiSpawnCount;

        // Reference mod uses an activation radius and MaxReplicaSpawnsPerFrame=1.
        // GroundZero spawns 14 heavy full-model replicas; creating all of them in one
        // frame exhausts GPU resources on the non-host client (D3D11 DEVICE_REMOVED).
        // v1 spawn activation defaults (metres). Native SetActiveByPlayerDistance is
        // patched with hysteresis: wake at 50 m, sleep at 60 m.
        internal const float AiActivationRadiusMeters = 50f;
        internal const float AiDeactivationRadiusMeters = 60f;
        private const float ReplicaActivationRadius = AiActivationRadiusMeters;
        private const float ReplicaDeactivationRadius = AiDeactivationRadiusMeters;
        private const float ReplicaSweepInterval = 0.2f;
        private float _nextReplicaSweep;
        // 2026-11-16: building a native character model costs 300-600 ms on slow machines
        // (21-28 renderers each). Creating 16 of them in a couple of seconds froze the
        // second client for 5-6 s. Models are now streamed: a cheap capsule appears
        // immediately, and at most one model is upgraded every 0.55 s within 30 m.
        private const float ReplicaModelRadius = 30f;
        private const float ReplicaModelInterval = 0.55f;
        private float _nextReplicaModelAt;

        private float ActivationRadiusFor(uint aiId)
        {
            return _aiActivationRadius.TryGetValue(aiId, out var value) && value > 0f
                ? value
                : ReplicaActivationRadius;
        }

        private float DeactivationRadiusFor(uint aiId)
        {
            return _aiDeactivationRadius.TryGetValue(aiId, out var value) && value > 0f
                ? value
                : ReplicaDeactivationRadius;
        }

        // 2026-11-16: SetActiveByPlayerDistance operates on *parent* containers (spawn group
        // roots), so an owned AI can end up inactiveInHierarchy while activeSelf stays true.
        // The server already owns the AI lifecycle (AOI spawn / Cleanup despawn), so every
        // ancestor of an owned real AI must stay awake regardless of the owner's distance.
        private readonly HashSet<int> _realAiAncestors = new HashSet<int>();
        private float _nextRealAiAncestorRefresh;

        internal bool IsProtectedActivationRoot(GameObject item)
        {
            if (item == null)
                return false;
            if (Time.unscaledTime >= _nextRealAiAncestorRefresh)
            {
                _nextRealAiAncestorRefresh = Time.unscaledTime + 1f;
                _realAiAncestors.Clear();
                foreach (var pair in _realAi)
                {
                    var ai = pair.Value;
                    if (ai == null)
                        continue;
                    var transform = ai.transform;
                    while (transform != null)
                    {
                        _realAiAncestors.Add(transform.gameObject.GetInstanceID());
                        transform = transform.parent;
                    }
                }
            }
            return _realAiAncestors.Contains(item.GetInstanceID());
        }


        private sealed class RemoteAiProxy
        {
            public GameObject Root;
            public GameObject RemoteRoot;
            public Health Health;
            public DamageReceiver Receiver;
        }

        private sealed class RemoteTargetState
        {
            public ushort PlayerId;
            public DamageReceiver Receiver;
            public float NoticedAt;
            public bool SearchChecked;
            public bool SearchFallback;
        }

        // ---------- D5: server aggro director ----------

        private const float AiTargetSearchFallbackDelay = 1f;

        private void UpdateAiTargets()
        {
            if (_client == null || _client.AiTargets.Count == 0)
                return;

            foreach (var pair in _client.AiTargets)
            {
                if (!_realAi.TryGetValue(pair.Key, out var ai) || ai == null)
                    continue;

                if (_appliedAiTargets.TryGetValue(pair.Key, out var applied) &&
                    applied == pair.Value.PlayerId)
                    continue;

                if (!ApplyAiTarget(pair.Key, ai, pair.Value.PlayerId))
                    continue;

                _appliedAiTargets[pair.Key] = pair.Value.PlayerId;
            }

            MaintainRemoteAiTargets();
        }

        private bool ApplyAiTarget(uint aiId, GameObject ai, ushort playerId)
        {
            try
            {
                var controller = ai.GetComponent<AICharacterController>();
                if (controller == null)
                    return false;

                if (!_setAiTargetLookupDone)
                {
                    _setAiTargetLookupDone = true;
                    _setAiTargetMethod = typeof(AICharacterController).GetMethod(
                        "SetTarget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (_setAiTargetMethod == null)
                {
                    Log("ai target: AICharacterController.SetTarget not found");
                    return false;
                }

                if (playerId == 0)
                {
                    _setAiTargetMethod.Invoke(controller, new object[] { null });
                    SetSearchedEnemy(controller, null);
                    _remoteTargetStates.Remove(aiId);
                    Log($"ai target cleared: ai={ai.name}");
                    return true;
                }

                Transform target = null;
                if (playerId == _client.PlayerId)
                {
                    var main = CharacterMainControl.Main;
                    if (main != null)
                        target = main.transform;
                }
                else if (_remotes.TryGetValue(playerId, out var remote) && remote != null)
                {
                    target = remote.transform;
                }

                if (target == null)
                {
                    Log($"ai target: player {playerId} has no transform yet");
                    return false;
                }

                _setAiTargetMethod.Invoke(controller, new object[] { target });
                _remoteTargetStates.Remove(aiId);

                if (playerId != _client.PlayerId)
                {
                    var proxy = EnsureRemoteAiProxy(playerId, target.gameObject);
                    if (proxy == null || proxy.Receiver == null)
                        return false;

                    // BUG-003: SetTarget only writes aimTarget. Notify the AI about the
                    // remote proxy as a real DamageReceiver, then verify after a short
                    // grace period whether its combat target is actually the proxy.
                    controller.SetNoticedToTarget(proxy.Receiver);
                    // SetNoticedToTarget only fills notice metadata; the native
                    // OnSound/OnHurt paths are what set `noticed`. Without this the
                    // CheckNoticed behavior-tree conditions never open.
                    controller.noticed = true;
                    _remoteTargetStates[aiId] = new RemoteTargetState
                    {
                        PlayerId = playerId,
                        Receiver = proxy.Receiver,
                        NoticedAt = Time.unscaledTime,
                    };
                    Log($"ai target noticed: ai={ai.name} player={playerId} proxy={proxy.Root.name} " +
                        $"noticed={controller.noticed}");
                }
                else
                {
                    // a previous remote fallback must not keep the AI on the old proxy
                    SetSearchedEnemy(controller, null);
                }

                Log($"ai target applied: ai={ai.name} player={playerId} " +
                    $"remote={playerId != _client.PlayerId} target={target.name}");
                return true;
            }
            catch (Exception ex)
            {
                Log("ai target failed: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        private RemoteAiProxy EnsureRemoteAiProxy(ushort playerId, GameObject remoteRoot)
        {
            if (remoteRoot == null)
                return null;

            if (_remoteAiProxies.TryGetValue(playerId, out var existing) &&
                existing.RemoteRoot == remoteRoot && existing.Health != null && existing.Receiver != null)
                return existing;

            if (existing != null && existing.RemoteRoot != remoteRoot)
            {
                // 2026-11-16: a replaced proxy starts with no damage history; name it so the
                // "freshly spawned player is invulnerable" window can be correlated.
                Log($"remote ai proxy replaced: player={playerId} " +
                    $"old={(existing.RemoteRoot != null ? existing.RemoteRoot.name : "<null>")} " +
                    $"new={remoteRoot.name}");
            }

            // Reference-mod lesson: a real CharacterMainControl is much more likely to
            // satisfy the native AI's search/behaviour-tree conditions than a bare
            // DamageReceiver. Try a full passive character first, then fall back.
            var full = TryCreateFullRemoteAiProxy(playerId, remoteRoot);
            if (full != null)
            {
                _remoteAiProxies[playerId] = full;
                Log($"remote ai proxy ready: player={playerId} root={full.Root.name} " +
                    $"fullCharacter=true hp={full.Health.MaxHealth:F0}");
                return full;
            }

            return CreateMinimalRemoteAiProxy(playerId, remoteRoot);
        }

        private RemoteAiProxy TryCreateFullRemoteAiProxy(ushort playerId, GameObject remoteRoot)
        {
            try
            {
                var main = CharacterMainControl.Main;
                if (main == null || main.characterModel == null)
                    return null;

                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var levelManagerType = FindTypeByName("LevelManager");
                if (levelManagerType == null)
                    return null;
                var levelManager = levelManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                var creator = levelManagerType.GetProperty("CharacterCreator", any)?.GetValue(levelManager) ??
                              levelManagerType.GetField("characterCreator", any)?.GetValue(levelManager);
                if (creator == null)
                    return null;

                var typeId = ReadCharacterItemTypeId(main);
                if (typeId <= 0)
                    return null;

                var itemAssetsType = FindTypeByName("ItemAssetsCollection");
                var itemAssets = itemAssetsType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                var instantiate = itemAssetsType?.GetMethod("InstantiateSync",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
                var item = instantiate?.Invoke(null, new object[] { typeId });
                if (item == null)
                    return null;

                var create = creator.GetType().GetMethod("CreateCharacter", any);
                if (create == null)
                    return null;

                var task = create.Invoke(creator, new object[]
                {
                    item,
                    main.characterModel,
                    remoteRoot.transform.position,
                    remoteRoot.transform.rotation,
                });
                var character = AwaitUniTaskResult(task) as CharacterMainControl;
                if (character == null)
                {
                    Log($"remote ai proxy full character failed: player={playerId} (task did not complete)");
                    return null;
                }

                var proxy = ConfigureFullRemoteAiProxy(playerId, remoteRoot, character);
                if (proxy == null && character != null)
                    UnityEngine.Object.Destroy(character.gameObject);
                return proxy;
            }
            catch (Exception ex)
            {
                Log($"remote ai proxy full character failed: player={playerId} {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private RemoteAiProxy ConfigureFullRemoteAiProxy(ushort playerId, GameObject remoteRoot,
            CharacterMainControl character)
        {
            var root = character.gameObject;
            root.name = "DuckovCoop_RemoteProxy_" + playerId;
            root.transform.SetParent(remoteRoot.transform, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;

            foreach (var controller in root.GetComponentsInChildren<CharacterController>(true))
                controller.enabled = false;
            foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour != null && behaviour.GetType().FullName == "UnityEngine.AI.NavMeshAgent")
                    behaviour.enabled = false;
            }
            foreach (var body in root.GetComponentsInChildren<Rigidbody>(true))
            {
                body.isKinematic = true;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            foreach (var ai in root.GetComponentsInChildren<AICharacterController>(true))
                ai.enabled = false;
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                animator.applyRootMotion = false;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = false;
            foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                camera.enabled = false;
            foreach (var listener in root.GetComponentsInChildren<AudioListener>(true))
                listener.enabled = false;

            var main = CharacterMainControl.Main;
            character.SetTeam(main != null ? main.Team : Teams.player);

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var health = character.Health;
            if (health == null)
                return null;

            health.autoInit = false;
            // The proxy is an invisible AI target with 1000000 HP; never let the game (or
            // our friendly-bar pass) give it a health bar. It lives under the mirror root,
            // so a bar for it used to overlap the player's real bar as a red strip.
            health.showHealthBar = false;
            typeof(Health).GetField("defaultMaxHealth", any)?.SetValue(health, 1000000);
            typeof(Health).GetField("DestroyOnDead", any)?.SetValue(health, false);
            health.SetInvincible(true);
            health.team = main != null ? main.Team : Teams.player;
            health.Init();

            var receiver = character.mainDamageReceiver;
            if (receiver == null)
                receiver = character.GetComponentInChildren<DamageReceiver>(true);
            if (receiver == null)
                return null;

            receiver.health = health;
            receiver.useSimpleHealth = false;

            var damageLayer = LayerMask.NameToLayer("DamageReceiver");
            if (damageLayer >= 0)
                receiver.gameObject.layer = damageLayer;

            var receiverColliders = receiver.GetComponents<Collider>();
            var hasCollider = false;
            foreach (var collider in receiverColliders)
            {
                if (collider == null || collider is CharacterController)
                    continue;
                collider.enabled = true;
                if (damageLayer >= 0)
                    collider.gameObject.layer = damageLayer;
                hasCollider = true;
            }
            if (!hasCollider)
            {
                var collider = receiver.gameObject.AddComponent<CapsuleCollider>();
                collider.center = new Vector3(0f, 0.9f, 0f);
                collider.height = 1.8f;
                collider.radius = 0.35f;
                collider.isTrigger = false;
                if (damageLayer >= 0)
                    collider.gameObject.layer = damageLayer;
            }

            var proxy = new RemoteAiProxy
            {
                Root = root,
                RemoteRoot = remoteRoot,
                Health = health,
                Receiver = receiver,
            };
            return proxy;
        }

        private RemoteAiProxy CreateMinimalRemoteAiProxy(ushort playerId, GameObject root)
        {
            var health = root.GetComponent<Health>();
            if (health == null)
                health = root.AddComponent<Health>();

            // Fallback for builds/assets where CreateCharacter is unavailable:
            // keep the proxy alive long enough for the chase; no hit reports are sent.
            const BindingFlags anyField = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            typeof(Health).GetField("defaultMaxHealth", anyField)?.SetValue(health, 1000000);
            typeof(Health).GetField("DestroyOnDead", anyField)?.SetValue(health, false);
            health.autoInit = true;
            health.SetInvincible(true);
            var main = CharacterMainControl.Main;
            health.team = main != null ? main.Team : Teams.player;
            health.Init();

            var receiver = root.GetComponent<DamageReceiver>();
            if (receiver == null)
                receiver = root.AddComponent<DamageReceiver>();
            receiver.health = health;
            receiver.useSimpleHealth = false;

            var collider = root.GetComponent<CapsuleCollider>();
            if (collider == null)
                collider = root.AddComponent<CapsuleCollider>();
            collider.center = new Vector3(0f, 0.9f, 0f);
            collider.height = 1.8f;
            collider.radius = 0.35f;
            collider.isTrigger = false;
            var damageLayer = LayerMask.NameToLayer("DamageReceiver");
            if (damageLayer >= 0)
                root.layer = damageLayer;

            var proxy = new RemoteAiProxy
            {
                Root = root,
                RemoteRoot = root,
                Health = health,
                Receiver = receiver,
            };
            _remoteAiProxies[playerId] = proxy;
            Log($"remote ai proxy ready: player={playerId} root={root.name} fullCharacter=false " +
                $"hp={health.MaxHealth:F0} collider=1.8x0.35 layer={root.layer}");
            return proxy;
        }

        private static int ReadCharacterItemTypeId(CharacterMainControl main)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var item = typeof(CharacterMainControl).GetProperty("CharacterItem", any)?.GetValue(main);
            if (item == null)
                item = typeof(CharacterMainControl).GetField("characterItem", any)?.GetValue(main);
            if (item == null)
                return 0;
            var value = item.GetType().GetProperty("TypeID", any)?.GetValue(item) ??
                        item.GetType().GetField("typeID", any)?.GetValue(item);
            return value is int typeId ? typeId : 0;
        }

        private static object AwaitUniTaskResult(object task)
        {
            if (task == null)
                return null;

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var awaiter = task.GetType().GetMethod("GetAwaiter", any)?.Invoke(task, null);
            if (awaiter == null)
                return null;

            var completed = awaiter.GetType().GetProperty("IsCompleted", any)?.GetValue(awaiter);
            if (!(completed is bool done && done))
                return null;

            return awaiter.GetType().GetMethod("GetResult", any)?.Invoke(awaiter, null);
        }

        private void MaintainRemoteAiTargets()
        {
            if (_client == null)
                return;

            foreach (var pair in _client.AiTargets)
            {
                var target = pair.Value;
                if (target.PlayerId == 0 || target.PlayerId == _client.PlayerId)
                    continue;

                if (!_realAi.TryGetValue(pair.Key, out var ai) || ai == null)
                    continue;

                if (!_remoteTargetStates.TryGetValue(pair.Key, out var state) ||
                    state.PlayerId != target.PlayerId || state.Receiver == null)
                {
                    _appliedAiTargets.Remove(pair.Key);
                    _remoteTargetStates.Remove(pair.Key);
                    continue;
                }

                if (!_remotes.TryGetValue(target.PlayerId, out var remote) || remote == null)
                {
                    _appliedAiTargets.Remove(pair.Key);
                    _remoteTargetStates.Remove(pair.Key);
                    continue;
                }

                var controller = ai.GetComponent<AICharacterController>();
                if (controller == null)
                    continue;

                if (!state.SearchFallback && !state.SearchChecked &&
                    Time.unscaledTime - state.NoticedAt >= AiTargetSearchFallbackDelay)
                {
                    state.SearchChecked = true;
                    var current = GetSearchedEnemy(controller);
                    Log($"ai target search check: ai={ai.name} player={target.PlayerId} " +
                        $"searched={(current != null ? current.name : "null")} " +
                        $"noticed={controller.noticed} alert={controller.alert} " +
                        $"aiPos={ai.transform.position:F1} targetPos={state.Receiver.transform.position:F1}");

                    if (!ReferenceEquals(current, state.Receiver))
                    {
                        state.SearchFallback = true;
                        Log($"ai target: searchedEnemy fallback enabled: ai={ai.name} player={target.PlayerId}");
                    }
                }

                if (state.SearchFallback)
                    SetSearchedEnemy(controller, state.Receiver);
            }
        }

        private static FieldInfo GetSearchedEnemyField()
        {
            if (_aiSearchedEnemyLookupDone)
                return _aiSearchedEnemyField;

            _aiSearchedEnemyLookupDone = true;
            _aiSearchedEnemyField = typeof(AICharacterController).GetField(
                "searchedEnemy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_aiSearchedEnemyField == null)
                Log("ai target: AICharacterController.searchedEnemy not found");
            return _aiSearchedEnemyField;
        }

        private static DamageReceiver GetSearchedEnemy(AICharacterController controller)
        {
            var field = GetSearchedEnemyField();
            return field != null ? field.GetValue(controller) as DamageReceiver : null;
        }

        private static void SetSearchedEnemy(AICharacterController controller, DamageReceiver receiver)
        {
            var field = GetSearchedEnemyField();
            if (field != null)
                field.SetValue(controller, receiver);
        }

        private void SyncAiGhosts()
        {
            if (!_inRaidScene)
            {
                _aiSpawnReplayPending = true;
                Log("ai spawn replay deferred: local player is not in raid scene");
                return;
            }

            _aiSpawnReplayPending = false;
            DespawnFarReplicas();

            var main = CharacterMainControl.Main;
            var spawnedThisSweep = 0;
            foreach (var pair in _client.AiSpawns)
            {
                var spawn = pair.Value;
                EnsureAiEpoch(spawn.RaidEpoch);
                if (spawn.RaidEpoch != 0 && spawn.RaidEpoch != _aiSpawnEpoch)
                    continue;

                // M2-v11: the server owns the streaming radii (cm); fall back to the old
                // client defaults when an older server or a manual spawn omits them.
                if (spawn.ActivationRadius > 0)
                    _aiActivationRadius[spawn.AiId] = spawn.ActivationRadius / 100f;
                if (spawn.DeactivationRadius > 0)
                    _aiDeactivationRadius[spawn.AiId] = spawn.DeactivationRadius / 100f;

                // The reliable spawn supersedes the previous despawn record for this aiId.
                // Drop the stale record so it cannot remove the re-created object, and clear
                // its dedupe key so a later AOI sleep is processed again.
                var despawnKey = ((ulong)spawn.RaidEpoch << 32) | spawn.AiId;
                if (_client.AiDespawns.Remove(spawn.AiId))
                    _processedAiDespawns.Remove(despawnKey);

                if (_aiGhosts.TryGetValue(pair.Key, out var existingGhost) && existingGhost != null)
                {
                    // M2-v12 owner migration: the reliable despawn normally removes the old
                    // object first. If a spawn arrives without that despawn (or both were
                    // coalesced), force a rebuild when the local role changed: a replica
                    // must become a real AI when we become owner, and vice versa.
                    var localIsOwner = spawn.OwnerPlayerId == _client.PlayerId;
                    var hasRealAi = _realAi.ContainsKey(spawn.AiId);
                    if (localIsOwner != hasRealAi)
                    {
                        Log($"[ai] owner transition rebuild: id={spawn.AiId} " +
                            $"localOwner={localIsOwner} hadReal={hasRealAi}");
                        RemoveAiImmediate(spawn.AiId);
                        _processedAiSpawns.Remove(spawn.AiId);
                    }
                    else
                    {
                        // A capsule placeholder (or a failed model build) is upgraded when the
                        // player is close enough and the global model budget allows it.
                        if (spawn.OwnerPlayerId != _client.PlayerId)
                            MaybeUpgradeCapsule(pair.Key, spawn, existingGhost);
                        continue;
                    }
                }

                if (spawn.OwnerPlayerId != _client.PlayerId)
                {
                    // Non-owner full-model replicas are expensive (model, animator,
                    // renderers, health bar). Show a cheap capsule immediately and only
                    // upgrade within 30 m at <=1 model / 0.55 s; otherwise 16 models in a
                    // row froze slow clients for seconds.
                    var distance = main != null
                        ? DistanceToLocal(GetAiRangePosition(pair.Key, spawn), main.transform.position)
                        : 0f;
                    if (distance > ActivationRadiusFor(pair.Key))
                        continue;
                    if (distance > ReplicaModelRadius || Time.unscaledTime < _nextReplicaModelAt)
                    {
                        if (!_processedAiSpawns.Add(spawn.AiId))
                            continue;
                        CreateAiGhost(spawn);
                        Log($"[ai] capsule placeholder: id={spawn.AiId} dist={distance:F0}m");
                        continue;
                    }
                }
                else if (spawnedThisSweep >= 1)
                {
                    // Owner: a real AI is a native character too. One per sweep keeps the
                    // 20-50 ms creation cost off a single frame when a group activates.
                    return;
                }

                if (!_processedAiSpawns.Add(spawn.AiId))
                    continue;

                if (spawn.OwnerPlayerId == _client.PlayerId)
                {
                    if (TrySpawnRealAi(spawn))
                    {
                        spawnedThisSweep++;
                        continue;
                    }
                }

                // Non-owner replica: use the same native preset spawn the host uses, so
                // the client gets the preset's own appearance (skin tint) and equipment.
                // The character is created far below the map, then stripped and moved.
                var replicaPreset = FindPresetForMonster(FindMonster(spawn.MonsterId));
                if (replicaPreset != null && StartPresetReplica(spawn, replicaPreset))
                {
                    spawnedThisSweep++;
                    _nextReplicaModelAt = Time.unscaledTime + ReplicaModelInterval;
                    continue;
                }

                if (TryCreateFullAiReplica(spawn, out var replica))
                {
                    ReplaceGhost(spawn.AiId, replica);
                    spawnedThisSweep++;
                    _nextReplicaModelAt = Time.unscaledTime + ReplicaModelInterval;
                    Log($"ai replica spawned: id={spawn.AiId} pos={replica.transform.position}");
                    continue;
                }

                CreateAiGhost(spawn);
                spawnedThisSweep++;
            }
        }

        private readonly Dictionary<uint, float> _capsuleUpgradeAt = new Dictionary<uint, float>();
        private readonly Dictionary<uint, int> _capsuleUpgradeAttempts = new Dictionary<uint, int>();
        private const int MaxCapsuleUpgradeAttempts = 5;

        /// <summary>
        /// A ghost without a CharacterMainControl is the primitive-capsule fallback used when
        /// the native replica build failed. Retry it with a 2 s backoff, capped so a broken
        /// preset cannot spin forever. Returns true when a model was created this sweep.
        /// </summary>
        private bool MaybeUpgradeCapsule(uint aiId, AiSpawn spawn, GameObject ghost)
        {
            if (_client == null || ghost == null || spawn == null)
                return false;
            if (ghost.GetComponentInChildren<CharacterMainControl>(true) != null)
                return false;
            if (spawn.OwnerPlayerId == _client.PlayerId)
                return false;
            if (Time.unscaledTime < _nextReplicaModelAt)
                return false;
            var main = CharacterMainControl.Main;
            if (main != null &&
                DistanceToLocal(GetAiRangePosition(aiId, spawn), main.transform.position) > ReplicaModelRadius)
                return false;
            _capsuleUpgradeAttempts.TryGetValue(aiId, out var attempts);
            if (attempts >= MaxCapsuleUpgradeAttempts)
                return false;
            _capsuleUpgradeAt.TryGetValue(aiId, out var nextAt);
            if (Time.unscaledTime < nextAt)
                return false;
            _capsuleUpgradeAt[aiId] = Time.unscaledTime + 5f;
            _capsuleUpgradeAttempts[aiId] = attempts + 1;
            _nextReplicaModelAt = Time.unscaledTime + ReplicaModelInterval;

            // Native preset spawning is asynchronous (CreateCharacterAsync). The previous
            // synchronous path treated "task not finished yet" as a permanent failure, so a
            // capsule could never upgrade (2026-11-16: ~10 frozen AI around the client).
            // The preset worker awaits the task properly and ReplaceGhost swaps the capsule.
            var preset = FindPresetForMonster(FindMonster(spawn.MonsterId));
            if (preset != null)
            {
                if (!StartPresetReplica(spawn, preset))
                    return false;
                Log($"[ai] capsule upgrade queued: id={aiId} preset={preset.name}");
                return true;
            }

            if (!TryCreateFullAiReplica(spawn, out var upgraded) || upgraded == null)
                return false;

            var position = ghost.transform.position;
            var rotation = ghost.transform.rotation;
            upgraded.transform.position = position;
            upgraded.transform.rotation = rotation;
            ReplaceGhost(aiId, upgraded);
            _capsuleUpgradeAttempts.Remove(aiId);
            _capsuleUpgradeAt.Remove(aiId);
            Log($"[ai] capsule upgraded to model: id={aiId} attempts={attempts + 1}");
            return true;
        }

        /// <summary>
        /// Swaps the ghost entry for an aiId, destroying a previous capsule placeholder so the
        /// upgrade cannot leave an invisible (or double) object in the scene.
        /// </summary>
        private void ReplaceGhost(uint aiId, GameObject newRoot)
        {
            if (newRoot == null)
                return;
            if (_aiGhosts.TryGetValue(aiId, out var previous) && previous != null && previous != newRoot)
            {
                UnityEngine.Object.Destroy(previous);
                Log($"[ai] capsule placeholder replaced: id={aiId}");
            }
            _aiGhosts[aiId] = newRoot;
        }

        private Vector3 GetAiRangePosition(uint aiId, AiSpawn spawn)
        {
            if (_client != null && _client.AiStates.TryGetValue(aiId, out var state) &&
                state != null)
                return new Vector3(state.X / 100f, state.Y / 100f, state.Z / 100f);
            return new Vector3(spawn.X / 100f, spawn.Y / 100f, spawn.Z / 100f);
        }

        private static float DistanceToLocal(Vector3 position, Vector3 local)
        {
            return Vector3.Distance(position, local);
        }

        private void DespawnFarReplicas()
        {
            if (_client == null)
                return;
            var main = CharacterMainControl.Main;
            if (main == null)
                return;

            List<uint> stale = null;
            foreach (var pair in _aiGhosts)
            {
                if (!_client.AiSpawns.TryGetValue(pair.Key, out var spawn))
                    continue;
                if (spawn.OwnerPlayerId == _client.PlayerId)
                    continue;
                if (DistanceToLocal(GetAiRangePosition(pair.Key, spawn), main.transform.position) <= DeactivationRadiusFor(pair.Key))
                    continue;
                if (stale == null)
                    stale = new List<uint>();
                stale.Add(pair.Key);
            }

            if (stale == null)
                return;
            foreach (var aiId in stale)
            {
                RemoveAi(aiId);
                _processedAiSpawns.Remove(aiId);
                Log($"ai replica released (out of range): id={aiId}");
            }
        }

        private void EnsureAiEpoch(uint epoch)
        {
            if (epoch == 0 || epoch == _aiSpawnEpoch)
                return;

            if (_aiSpawnEpoch != 0)
            {
                var ids = new List<uint>(_realAi.Keys);
                foreach (var id in _aiGhosts.Keys)
                    if (!ids.Contains(id))
                        ids.Add(id);
                foreach (var id in ids)
                    RemoveAi(id);
                _processedAiSpawns.Clear();
                _processedAiDespawns.Clear();
                _aiActivationRadius.Clear();
                _aiDeactivationRadius.Clear();
                _client.AiDespawns.Clear();
                Log($"ai epoch changed: {_aiSpawnEpoch} -> {epoch}, cleared old AI");
            }

            _aiSpawnEpoch = epoch;
        }

        private void ConsumeAiDespawns()
        {
            if (_client == null || _client.AiDespawns.Count == 0)
                return;

            foreach (var pair in _client.AiDespawns)
            {
                var despawn = pair.Value;
                var key = ((ulong)despawn.RaidEpoch << 32) | despawn.AiId;
                if (!_processedAiDespawns.Add(key))
                    continue;
                if (despawn.RaidEpoch != 0 && _aiSpawnEpoch != 0 &&
                    despawn.RaidEpoch != _aiSpawnEpoch)
                    continue;

                Log($"ai despawn: id={despawn.AiId} epoch={despawn.RaidEpoch} reason={despawn.Reason}");
                // The server's Death despawn is the single source for "this AI died", so
                // non-owner replicas play the same fall + native dead box instead of popping.
                RemoveAi(despawn.AiId, despawn.Reason);
                // M2-v12 owner migration sends despawn+spawn back to back. Both can land in
                // the same client Tick, so do not delete a spawn that arrived after this
                // despawn: SyncAiGhosts must see it and rebuild the replica for the new owner.
                if (!_client.AiSpawnIsNewerThanDespawn(despawn.AiId))
                    _client.AiSpawns.Remove(despawn.AiId);
                // M2-v11: an AOI sleep despawns the AI; the same aiId wakes again later.
                // Allow the re-creation, but KEEP the despawn dedupe key: the despawn message
                // stays in CoopClient.AiDespawns until a new spawn replaces it, and removing
                // the key re-processed it every frame (2026-11-16: 900+ RemoveAi/frame and
                // 150-220 ms frames on both clients). The key is dropped when the next
                // SAiSpawn for this aiId arrives (see SyncAiGhosts).
                _processedAiSpawns.Remove(despawn.AiId);
            }
        }

        private void CreateAiGhost(AiSpawn spawn)
        {
            var ghost = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            ghost.name = "DuckovCoop_Ai_" + spawn.AiId;
            ghost.transform.SetParent(transform, false);
            ghost.transform.position = new Vector3(spawn.X / 100f, spawn.Y / 100f, spawn.Z / 100f);
            ghost.transform.localScale = new Vector3(0.9f, 1.1f, 0.9f);

            var collider = ghost.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.Destroy(collider);

            var renderer = ghost.GetComponent<Renderer>();
            if (renderer != null)
            {
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                if (shader != null)
                {
                    renderer.material = new Material(shader);
                    renderer.material.color = new Color(1f, 0.25f, 0.2f, 1f);
                }
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            ReplaceGhost(spawn.AiId, ghost);
            Log($"ai ghost spawned: id={spawn.AiId} pos={ghost.transform.position} hp={spawn.Hp}");
        }

        private bool TrySpawnRealAi(AiSpawn spawn)
        {
            try
            {
                var spawners = UnityEngine.Object.FindObjectsOfType<RandomCharacterSpawner>();
                if (spawners == null || spawners.Length == 0)
                {
                    Log("real ai: no RandomCharacterSpawner in scene");
                    return false;
                }

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                RandomCharacterSpawner spawner = null;
                object spawnerRoot = null;
                System.Collections.IList infos = null;

                foreach (var candidate in spawners)
                {
                    if (candidate == null)
                        continue;

                    var root = typeof(RandomCharacterSpawner).GetField("spawnerRoot", flags)?.GetValue(candidate);
                    if (root == null)
                        continue;

                    var presets = typeof(RandomCharacterSpawner).GetField("randomPresetInfos", flags)?.GetValue(candidate)
                                  as System.Collections.IList;
                    if (presets == null || presets.Count == 0)
                        continue;

                    spawner = candidate;
                    spawnerRoot = root;
                    infos = presets;
                    break;
                }

                if (spawner == null)
                {
                    Log("real ai: no initialised spawner with presets");
                    return false;
                }

                var group = typeof(RandomCharacterSpawner).GetField("masterGroup", flags)?.GetValue(spawner);
                if (group == null)
                    group = UnityEngine.Object.FindObjectOfType<CharacterSpawnerGroup>();

                object info = null;
                object preset = null;
                const BindingFlags anyFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var catalogEntry = FindMonster(spawn.MonsterId);
                var wantedAsset = catalogEntry != null ? catalogEntry.AssetName : string.Empty;

                if (!string.IsNullOrEmpty(wantedAsset))
                {
                    // The first initialized spawner only carries its own preset list.
                    // Search every scene spawner so Scav/USEC/Wolf presets are not
                    // silently replaced by the first spawner's mushroom preset.
                    foreach (var candidateSpawner in spawners)
                    {
                        if (candidateSpawner == null)
                            continue;
                        var candidateInfos = typeof(RandomCharacterSpawner)
                            .GetField("randomPresetInfos", flags)?.GetValue(candidateSpawner) as System.Collections.IList;
                        if (candidateInfos == null)
                            continue;

                        for (var i = 0; i < candidateInfos.Count; i++)
                        {
                            var candidateInfo = candidateInfos[i];
                            var candidatePreset = candidateInfo.GetType()
                                .GetField("randomPreset", anyFlags)?.GetValue(candidateInfo);
                            if (candidatePreset == null)
                                continue;
                            var presetName = (candidatePreset as UnityEngine.Object)?.name ?? string.Empty;
                            if (!string.Equals(presetName, wantedAsset, StringComparison.Ordinal))
                                continue;

                            info = candidateInfo;
                            preset = candidatePreset;
                            break;
                        }

                        if (preset != null)
                            break;
                    }
                }

                for (var i = 0; preset == null && i < infos.Count; i++)
                {
                    var candidateInfo = infos[i];
                    var candidatePreset = candidateInfo.GetType()
                        .GetField("randomPreset", anyFlags)
                        ?.GetValue(candidateInfo);

                    if (candidatePreset == null)
                        continue;

                    var presetName = (candidatePreset as UnityEngine.Object)?.name ?? string.Empty;
                    if (!string.IsNullOrEmpty(wantedAsset) &&
                        string.Equals(presetName, wantedAsset, StringComparison.Ordinal))
                    {
                        info = candidateInfo;
                        preset = candidatePreset;
                        break;
                    }
                }

                if (preset == null)
                {
                    for (var i = 0; i < infos.Count; i++)
                    {
                        var candidateInfo = infos[i];
                        var candidatePreset = candidateInfo.GetType()
                            .GetField("randomPreset", anyFlags)
                            ?.GetValue(candidateInfo);

                        if (candidatePreset == null)
                            continue;

                        var isBoss = candidatePreset.GetType().GetField("isBoss", anyFlags)?.GetValue(candidatePreset);
                        if (isBoss is bool boss && boss)
                            continue;

                        info = candidateInfo;
                        preset = candidatePreset;
                        break;
                    }
                }

                if (preset == null)
                {
                    // every preset is a boss; fall back to the first one
                    info = infos[0];
                    preset = info.GetType().GetField("randomPreset", anyFlags)?.GetValue(info);
                }

                if (preset == null)
                {
                    Log("real ai: preset is null");
                    return false;
                }
                Log($"real ai preset select: id={spawn.AiId} monster={spawn.MonsterId} " +
                    $"wanted={wantedAsset} selected={(preset as UnityEngine.Object)?.name ?? preset.GetType().Name}");

                var create = preset.GetType().GetMethod("CreateCharacterAsync", flags);
                if (create == null)
                {
                    Log("real ai: CreateCharacterAsync not found");
                    return false;
                }

                var position = new Vector3(spawn.X / 100f, spawn.Y / 100f, spawn.Z / 100f);

                var sceneIndex = 0;
                if (spawnerRoot != null)
                {
                    var related = spawnerRoot.GetType().GetField("relatedScene", flags)?.GetValue(spawnerRoot);
                    if (related is int i)
                        sceneIndex = i;
                }

                var local = CharacterMainControl.Main;
                var direction = local != null ? (position - local.transform.position).normalized : Vector3.forward;
                if (direction.sqrMagnitude < 0.01f)
                    direction = Vector3.forward;

                var beforeIds = new HashSet<int>();
                foreach (var ai in UnityEngine.Object.FindObjectsOfType<AICharacterController>())
                    beforeIds.Add(ai.GetInstanceID());

                var groupBefore = new HashSet<int>();
                foreach (var character in GetGroupCharacters(group))
                {
                    if (character is AICharacterController controller && controller != null)
                        groupBefore.Add(controller.GetInstanceID());
                }

                var result = create.Invoke(preset, new object[] { position, direction, sceneIndex, group, false });
                _pendingRealAiSpawns++;
                Log($"real ai preset spawn: id={spawn.AiId} preset={preset.GetType().Name} scene={sceneIndex} " +
                    $"group={(group != null)} groupBefore={groupBefore.Count} before={beforeIds.Count} result={result?.GetType().Name ?? "null"}");
                StartCoroutine(CheckPresetSpawn(spawn, group, groupBefore, position));
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException && ex.InnerException != null
                    ? ex.InnerException
                    : ex;
                Log($"real ai spawn failed: {inner.GetType().Name}: {inner.Message}");
                Log(inner.StackTrace ?? "<no stack>");
                return false;
            }
        }

        private System.Collections.IEnumerator VerifyRealAiSpawn(AiSpawn spawn, HashSet<int> beforeIds, Vector3 target)
        {
            yield return new WaitForSeconds(5f);

            var all = UnityEngine.Object.FindObjectsOfType<AICharacterController>();
            var fresh = new List<AICharacterController>();
            foreach (var ai in all)
            {
                if (ai != null && !beforeIds.Contains(ai.GetInstanceID()))
                    fresh.Add(ai);
            }

            Log($"real ai spawn check: id={spawn.AiId} before={beforeIds.Count} after={all.Length} fresh={fresh.Count}");

            if (fresh.Count == 0)
            {
                Log("real ai spawn produced nothing, falling back to ghost");
                CreateAiGhost(spawn);
                yield break;
            }

            var local = CharacterMainControl.Main;
            for (var i = 0; i < fresh.Count; i++)
            {
                var ai = fresh[i];
                var distance = local != null ? Vector3.Distance(ai.transform.position, local.transform.position) : -1f;
                var renderers = ai.GetComponentsInChildren<Renderer>(true);
                var enabledRenderers = 0;
                foreach (var renderer in renderers)
                    if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                        enabledRenderers++;

                Log($"real ai fresh: id={spawn.AiId} pos={ai.transform.position} dist={distance:F1} " +
                    $"active={ai.gameObject.activeInHierarchy} enabled={ai.enabled} layer={ai.gameObject.layer} " +
                    $"renderers={renderers.Length}/{enabledRenderers} " +
                    $"skinned={ai.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length}");

                if (distance > 30f || distance < 0f)
                {
                    ai.transform.position = target + new Vector3(1.2f * i, 0f, 0f);
                    Log($"real ai repositioned to {ai.transform.position}");
                }

                if (!ai.gameObject.name.StartsWith("CoopAi_"))
                    ai.gameObject.name = "CoopAi_" + spawn.AiId;

                if (enabledRenderers == 0)
                {
                    var attached = TryAttachCharacterModel(ai.gameObject);
                    Log($"ai model attached: id={spawn.AiId} ok={attached}");
                    renderers = ai.GetComponentsInChildren<Renderer>(true);
                    enabledRenderers = 0;
                    foreach (var renderer in renderers)
                        if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                            enabledRenderers++;
                    Log($"ai renderers after attach: {renderers.Length}/{enabledRenderers}");
                }

                if (i == 0)
                {
                    _realAi[spawn.AiId] = ai.gameObject;

                    const BindingFlags anyFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var receiver = ai.GetComponent<DamageReceiver>();
                    var health = receiver != null
                        ? typeof(DamageReceiver).GetField("health", anyFlags)?.GetValue(receiver)
                        : null;

                    if (health != null)
                    {
                        _realAiHealth[spawn.AiId] = health;
                        var hp = ReadHealth(health);
                        if (hp >= 0f)
                            _realAiLastHp[spawn.AiId] = hp;
                    }
                }
            }
        }

        private void CleanupAiGhostsOfUnavailableOwners()
        {
            if (_client == null || _aiGhosts.Count == 0)
                return;

            var snapshot = _client.LatestSnapshot;
            if (snapshot == null || _client.SnapshotsReceived == 0)
                return;

            // BUG-002: the server emits one SCombatEvent per AI when an owner dies or
            // leaves, but CoopClient keeps only the last event. Use the owner player's
            // snapshot state as the fallback cleanup signal. Do not remove a ghost until
            // we have seen its owner as an active raid participant at least once: at raid
            // start SAiSpawn can arrive before the owner's in-raid snapshot bit. The set is
            // keyed by ghost instance id so a re-created ghost/raid cannot inherit an old
            // owner-active decision.
            var liveGhostIds = new HashSet<int>();
            foreach (var ghost in _aiGhosts.Values)
            {
                if (ghost != null)
                    liveGhostIds.Add(ghost.GetInstanceID());
            }
            _aiGhostOwnersSeenInRaid.RemoveWhere(ghostId => !liveGhostIds.Contains(ghostId));

            // BUG-002 / M2-v12: an owner who is merely dead keeps the AI entities on the
            // server (they respawn in 5 s). Removing their ghosts here made every AI vanish
            // for the other player until an AOI sleep/wake cycle. Keep the owner valid while
            // the snapshot still carries a raid scene; only clear it when the player really
            // left/extracted (scene cleared) or disappeared from the snapshot.
            var activeOwners = new HashSet<ushort>();
            for (var i = 0; i < snapshot.Players.Length; i++)
            {
                var player = snapshot.Players[i];
                if (string.IsNullOrEmpty(player.Scene))
                    continue;
                if ((player.Flags & 0x04) != 0)
                    continue;
                activeOwners.Add(player.PlayerId);
            }

            var staleOwners = new HashSet<ushort>();
            foreach (var pair in _aiGhosts)
            {
                var ghost = pair.Value;
                if (ghost == null)
                    continue;

                if (!_client.AiSpawns.TryGetValue(pair.Key, out var spawn))
                    continue;

                var ownerPlayerId = spawn.OwnerPlayerId;
                if (ownerPlayerId == 0)
                    continue;

                var ghostId = ghost.GetInstanceID();
                if (activeOwners.Contains(ownerPlayerId))
                {
                    _aiGhostOwnersSeenInRaid.Add(ghostId);
                    continue;
                }

                if (!_aiGhostOwnersSeenInRaid.Contains(ghostId))
                    continue;

                staleOwners.Add(ownerPlayerId);
            }

            foreach (var ownerPlayerId in staleOwners)
                RemoveAiGhostsOfOwner(ownerPlayerId, "owner-not-in-raid");
        }

        private void RemoveAiGhostsOfOwner(ushort ownerPlayerId, string reason)
        {
            if (_client == null || ownerPlayerId == 0 || _aiGhosts.Count == 0)
                return;

            List<uint> stale = null;
            foreach (var pair in _aiGhosts)
            {
                if (!_client.AiSpawns.TryGetValue(pair.Key, out var spawn))
                    continue;
                if (spawn.OwnerPlayerId != ownerPlayerId)
                    continue;

                if (stale == null)
                    stale = new List<uint>();
                stale.Add(pair.Key);
            }

            if (stale == null)
                return;

            Log($"ai ghosts cleaned: owner={ownerPlayerId} count={stale.Count} reason={reason}");
            foreach (var aiId in stale)
                RemoveAi(aiId);
        }

        private void UpdateAiGhosts()
        {
            ConsumeAiDespawns();
            if (_inRaidScene && Time.unscaledTime >= _nextReplicaSweep)
            {
                _nextReplicaSweep = Time.unscaledTime + ReplicaSweepInterval;
                SyncAiGhosts();
            }
            CleanupAiGhostsOfUnavailableOwners();

            if (_client == null || _aiGhosts.Count == 0)
                return;

            if (GateAProbe.FreezeAiGhosts)
                return;

            var lerp = 1f - Mathf.Exp(-12f * Time.deltaTime);
            foreach (var pair in _aiGhosts)
            {
                var ghost = pair.Value;
                if (ghost == null)
                    continue;

                if (!_client.AiStates.TryGetValue(pair.Key, out var state))
                    continue;

                var target = new Vector3(state.X / 100f, state.Y / 100f, state.Z / 100f);
                ghost.transform.position = Vector3.Lerp(ghost.transform.position, target, lerp);

                var yaw = state.Yaw / 65535f * 360f;
                ghost.transform.rotation = Quaternion.Slerp(
                    ghost.transform.rotation,
                    Quaternion.Euler(0f, yaw, 0f),
                    1f - Mathf.Exp(-10f * Time.deltaTime));

                // Native character code keeps writing the aim yaw into modelRoot; lock the
                // root-relative offset and drive the visual model with the synced rotation.
                if (_aiReplicaModels.TryGetValue(pair.Key, out var modelTransform) && modelTransform != null)
                    modelTransform.rotation = ghost.transform.rotation;
                if (_aiReplicaModelRoots.TryGetValue(pair.Key, out var aimRoot) && aimRoot != null)
                    aimRoot.localRotation = Quaternion.identity;

                var replicaControl = ghost.GetComponentInChildren<CharacterMainControl>(true);
                if (replicaControl != null &&
                    !ApplyHostAiLoadoutV7(pair.Key, replicaControl))
                    ApplyHostAiLoadout(pair.Key, replicaControl);
            }

            if (Time.unscaledTime >= _nextClientMotionLog)
            {
                _nextClientMotionLog = Time.unscaledTime + 3f;
                var moved = 0f;
                var movers = 0;
                var withState = 0;
                foreach (var pair in _aiGhosts)
                {
                    if (!_client.AiStates.TryGetValue(pair.Key, out var state))
                        continue;
                    withState++;
                    var target = new Vector3(state.X / 100f, state.Y / 100f, state.Z / 100f);
                    if (_ghostDiagTarget.TryGetValue(pair.Key, out var previous))
                    {
                        var delta = Vector3.Distance(previous, target);
                        moved += delta;
                        if (delta > 0.05f)
                            movers++;
                    }
                    _ghostDiagTarget[pair.Key] = target;
                }
                Log($"[ai] client motion: ghosts={_aiGhosts.Count} withState={withState} movers={movers} moved={moved:F1}m");
            }
        }

        private readonly HashSet<uint> _aiDeathAnimating = new HashSet<uint>();
        private readonly List<(Vector3 Position, InteractableLootbox Prefab)> _pendingAiDeadPrefabs =
            new List<(Vector3, InteractableLootbox)>();

        /// <summary>
        /// reason == AiDespawnReason.Death (0) means the server settled a kill; anything
        /// else (owner left / raid end / cleanup) removes the AI without a death scene.
        /// </summary>
        private void RemoveAi(uint aiId, int reason = -1)
        {
            if (_aiDeathAnimating.Contains(aiId))
                return;
            if (_realAi.TryGetValue(aiId, out var ai) && ai != null)
            {
                _aiDeathAnimating.Add(aiId);
                StartCoroutine(PlayAiDeathAndRemove(aiId, ai));
                return;
            }
            // Non-owner replica. Before this, the body vanished instantly on every client
            // except the owner (S5: no fall, generic loot box). The despawn already carries
            // the death fact, so run the same presentation as the owner.
            if (reason == 0 && _aiGhosts.TryGetValue(aiId, out var ghost) && ghost != null)
            {
                _aiDeathAnimating.Add(aiId);
                var hasModel = ghost.GetComponentInChildren<CharacterMainControl>(true) != null;
                Log($"[ai] replica death presentation: id={aiId} model={hasModel}");
                StartCoroutine(PlayAiDeathAndRemove(aiId, ghost));
                return;
            }
            RemoveAiImmediate(aiId);
        }

        private CharacterMainControl FindAiControl(GameObject ai)
        {
            if (ai != null)
            {
                var direct = ai.GetComponentInChildren<CharacterMainControl>(true);
                if (direct != null)
                    return direct;
            }
            // Preset spawns sometimes keep the controller root and the visible character on
            // separate objects. Match by proximity, never pick the local player.
            CharacterMainControl best = null;
            var bestDistance = 4f;
            var origin = ai != null ? ai.transform.position : Vector3.zero;
            foreach (var control in Resources.FindObjectsOfTypeAll<CharacterMainControl>())
            {
                if (control == null || control == CharacterMainControl.Main)
                    continue;
                var distance = Vector3.Distance(control.transform.position, origin);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = control;
                }
            }
            return best;
        }

        private void InvokeNativeAiDeath(GameObject ai, CharacterMainControl control, uint aiId,
            bool allowProximityFallback = true)
        {
            if (control == null && allowProximityFallback)
                control = FindAiControl(ai);
            if (control == null)
            {
                Log($"[ai] native OnDead control not found id={aiId}");
                return;
            }
            try
            {
                // Mirror the reference AI-sync mod: raise the native Health.OnDeadEvent
                // instead of calling CharacterMainControl.OnDead directly. This runs the
                // native death listeners (VFX/audio/quests) and its OnDead creates the
                // dead lootbox visual, which CreateCorpseVisual then reuses.
                var health = _realAiHealth.TryGetValue(aiId, out var healthObject) &&
                             healthObject is Health knownHealth
                    ? knownHealth
                    : control.Health;
                if (health == null)
                    health = control.GetComponentInChildren<Health>(true);
                if (health == null)
                {
                    Log($"[ai] native death health missing id={aiId}");
                    return;
                }
                control.dropBoxOnDead = true;
                var handled = AccessTools.Field(typeof(CharacterMainControl), "hasHandledDeath");
                handled?.SetValue(control, false);
                var info = new DamageInfo
                {
                    damageValue = 1f,
                    finalDamage = 1f,
                    fromCharacter = CharacterMainControl.Main,
                    toDamageReceiver = control.mainDamageReceiver,
                };
                SpawnGuard.AllowNativeDeadLootbox = true;
                health.OnDeadEvent?.Invoke(info);
                // Also raise the static event used by native audio/kill bookkeeping.
                if (AccessTools.Field(typeof(Health), "OnDead")?.GetValue(null) is
                    Action<Health, DamageInfo> staticDead)
                    staticDead.Invoke(health, info);
                Log($"[ai] native OnDeadEvent invoked id={aiId}");
            }
            catch (Exception ex)
            {
                Log($"[ai] native OnDead failed id={aiId}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                SpawnGuard.AllowNativeDeadLootbox = false;
            }
        }

        /// <summary>
        /// Lightweight death presentation while the authoritative corpse container already
        /// exists: stop the AI, let the model fall over, then remove it. This fills the gap
        /// left by skipping native death handling without keeping a permanent body.
        /// </summary>
        private System.Collections.IEnumerator PlayAiDeathAndRemove(uint aiId, GameObject ai)
        {
            try
            {
                if (ai != null)
                {
                    // _realAi points at the AICharacterController root; CharacterMainControl
                    // (and its deadLootBoxPrefab) usually hangs off a child body.
                    // Only the owner path may fall back to a proximity search: a non-owner
                    // ghost without a control is a plain capsule, and FindAiControl would
                    // fire the death event of some unrelated nearby AI.
                    var control = ai.GetComponentInChildren<CharacterMainControl>(true);
                    var isRealAi = _realAi.ContainsKey(aiId);
                    InvokeNativeAiDeath(ai, control, aiId, isRealAi);
                    if (control == null && isRealAi)
                        control = FindAiControl(ai);
                    if (control != null && control.deadLootBoxPrefab != null)
                    {
                        _pendingAiDeadPrefabs.Add(
                            (ai.transform.position, control.deadLootBoxPrefab));
                        Log($"[ai] native dead prefab captured id={aiId}");
                    }
                    else
                    {
                        Log($"[ai] native dead prefab missing id={aiId} control={control != null}");
                    }
                    foreach (var controller in ai.GetComponentsInChildren<AICharacterController>(true))
                        if (controller != null)
                            controller.enabled = false;
                    foreach (var agent in ai.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true))
                        if (agent != null)
                            agent.enabled = false;
                    var startRotation = ai.transform.rotation;
                    var endRotation = Quaternion.Euler(90f, ai.transform.eulerAngles.y, 0f);
                    var startPosition = ai.transform.position;
                    var endPosition = startPosition + Vector3.down * 0.45f;
                    const float duration = 0.55f;
                    var elapsed = 0f;
                    while (elapsed < duration && ai != null)
                    {
                        elapsed += Time.deltaTime;
                        var t = Mathf.Clamp01(elapsed / duration);
                        ai.transform.rotation = Quaternion.Slerp(startRotation, endRotation, t);
                        ai.transform.position = Vector3.Lerp(startPosition, endPosition, t);
                        yield return null;
                    }
                }
            }
            finally
            {
                _aiDeathAnimating.Remove(aiId);
            }
            RemoveAiImmediate(aiId);
        }

        private void RemoveAiImmediate(uint aiId)
        {
            if (_realAiHealth.TryGetValue(aiId, out var healthObject) &&
                healthObject is Health deadHealth)
            {
                try
                {
                    foreach (var bar in Resources.FindObjectsOfTypeAll<Duckov.UI.HealthBar>())
                    {
                        if (bar != null && bar.target == deadHealth)
                            bar.gameObject.SetActive(false);
                    }
                }
                catch (Exception ex)
                {
                    Log($"[ai] health bar cleanup failed id={aiId}: {ex.GetType().Name}");
                }
            }
            var root = _realAi.TryGetValue(aiId, out var ai) && ai != null ? ai : null;
            var rootTransform = root != null ? root.transform : null;
            _aiReplicaModelRoots.TryGetValue(aiId, out var modelRoot);
            _aiReplicaModels.TryGetValue(aiId, out var model);
            var modelRootIsChild = rootTransform != null && modelRoot != null &&
                                   modelRoot.IsChildOf(rootTransform);
            var modelIsChild = rootTransform != null && model != null &&
                               model.IsChildOf(rootTransform);
            if (root != null)
                UnityEngine.Object.Destroy(root);
            // _aiReplicaModelRoots/_aiReplicaModels hold Transform, not GameObject.
            // Destroy(Transform) is illegal and corrupted the native character model,
            // crashing Unity after an out-of-range replica release. Destroy the owning
            // GameObject only when it is not already a child of the root we just removed.
            if (!modelRootIsChild && modelRoot != null)
                UnityEngine.Object.Destroy(modelRoot.gameObject);
            if (!modelIsChild && model != null)
                UnityEngine.Object.Destroy(model.gameObject);
            _realAi.Remove(aiId);
            // M2-v11: radii arrive again with the next SAiSpawn after an AOI wake.
            _aiActivationRadius.Remove(aiId);
            _aiDeactivationRadius.Remove(aiId);
            // Safety sweep: the native preset may keep the visible body under a separate root
            // (or a model clone) that is not the tracked controller object. Death must leave
            // only the loot container, matching native behavior.
            try
            {
                var prefix = "CoopAi_" + aiId;
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null)
                        continue;
                    if (go.name == prefix || go.name.StartsWith(prefix + "_", StringComparison.Ordinal))
                        UnityEngine.Object.Destroy(go);
                }
            }
            catch (Exception ex)
            {
                Log($"[ai] scene cleanup failed id={aiId}: {ex.GetType().Name}");
            }
            UnregisterAiObjects(aiId);
            ClearAiLoadout(aiId);
            ClearAiV6State(aiId);
            ClearAiV7State(aiId);
            _realAiHealth.Remove(aiId);
            _realAiLastHp.Remove(aiId);
            _appliedAiTargets.Remove(aiId);
            _remoteTargetStates.Remove(aiId);

            if (_aiGhosts.TryGetValue(aiId, out var ghost) && ghost != null)
            {
                _aiGhostOwnersSeenInRaid.Remove(ghost.GetInstanceID());
                UnityEngine.Object.Destroy(ghost);
            }
            _aiGhosts.Remove(aiId);
            _aiReplicaLastPositions.Remove(aiId);
            _aiReplicaModelRoots.Remove(aiId);
            _aiReplicaModels.Remove(aiId);
            _capsuleUpgradeAt.Remove(aiId);
            _capsuleUpgradeAttempts.Remove(aiId);

            Log($"ai removed: id={aiId}");
        }

        private static System.Collections.IList GetGroupCharacters(object group)
        {
            if (group == null)
                return null;

            return group.GetType()
                .GetField("characters", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(group) as System.Collections.IList;
        }

        private System.Collections.IEnumerator CheckPresetSpawn(AiSpawn spawn, object group, HashSet<int> groupBefore, Vector3 target)
        {
            // Poll for the new character and register its id as soon as it exists, so
            // hits during the (formerly 3s) settle window are not reported as "raw" and
            // cannot fall through to native damage.
            AICharacterController found = null;
            var waited = 0f;
            while (waited < 3f)
            {
                foreach (var character in GetGroupCharacters(group))
                {
                    if (character is AICharacterController controller && controller != null &&
                        !groupBefore.Contains(controller.GetInstanceID()))
                    {
                        found = controller;
                        break;
                    }
                }

                if (found != null)
                {
                    var earlyControl = found.CharacterMainControl;
                    if (earlyControl == null)
                        earlyControl = found.GetComponentInChildren<CharacterMainControl>(true);
                    var earlyRoot = earlyControl != null ? earlyControl.gameObject : found.gameObject;
                    found.gameObject.name = "CoopAi_" + spawn.AiId;
                    RegisterAiObject(found.gameObject, spawn.AiId);
                    if (earlyRoot != found.gameObject)
                    {
                        earlyRoot.name = "CoopAi_" + spawn.AiId + "_Body";
                        RegisterAiObject(earlyRoot, spawn.AiId);
                    }

                    break;
                }

                yield return new WaitForSeconds(0.2f);
                waited += 0.2f;
            }

            if (found != null && waited < 3f)
                yield return new WaitForSeconds(3f - waited);

            if (found == null)
            {
                Log($"preset spawn check: id={spawn.AiId} no new character in group, falling back to ghost");
                CreateAiGhost(spawn);
                _pendingRealAiSpawns = Mathf.Max(0, _pendingRealAiSpawns - 1);
                yield break;
            }

            // The group exposes the AI controller; the visible character (model, health,
            // aim root) hangs off AICharacterController.CharacterMainControl.
            var control = found.CharacterMainControl;
            if (control == null)
                control = found.GetComponentInChildren<CharacterMainControl>(true);
            var visualRoot = control != null ? control.gameObject : found.gameObject;

            var renderers = visualRoot.GetComponentsInChildren<Renderer>(true);
            var enabledRenderers = 0;
            foreach (var renderer in renderers)
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                    enabledRenderers++;

            var local = CharacterMainControl.Main;
            var distance = local != null ? Vector3.Distance(visualRoot.transform.position, local.transform.position) : -1f;
            var modelYaw = control != null && control.modelRoot != null ? control.modelRoot.eulerAngles.y : -1f;
            Log($"preset spawn check: id={spawn.AiId} found={found.name} body={visualRoot.name} " +
                $"pos={visualRoot.transform.position} dist={distance:F1} " +
                $"rootYaw={visualRoot.transform.eulerAngles.y:F1} modelYaw={modelYaw:F1} " +
                $"renderers={renderers.Length}/{enabledRenderers} skinned={visualRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length}");

            if (spawn.AiId <= 2)
                Log($"real ai color: id={spawn.AiId} {DescribeBodyColor(visualRoot)}");

            if (distance > 30f || distance < 0f)
            {
                visualRoot.transform.position = target;
                Log($"preset ai repositioned to {visualRoot.transform.position}");
            }

            found.gameObject.name = "CoopAi_" + spawn.AiId;
            if (visualRoot != found.gameObject)
                visualRoot.name = "CoopAi_" + spawn.AiId + "_Body";
            _realAi[spawn.AiId] = found.gameObject;
            RegisterAiObject(found.gameObject, spawn.AiId);
            if (visualRoot != found.gameObject)
                RegisterAiObject(visualRoot, spawn.AiId);

            // D5: report the generated loadout once the character (and its items) exists.
            SendAiLoadoutIfNeeded(spawn.AiId, (object)control ?? found.GetComponent<CharacterMainControl>());

            // A real character replaces any capsule fallback created for this id.
            if (_aiGhosts.TryGetValue(spawn.AiId, out var staleGhost))
            {
                if (staleGhost != null)
                    UnityEngine.Object.Destroy(staleGhost);
                _aiGhosts.Remove(spawn.AiId);
                Log($"preset spawn cleared ghost: id={spawn.AiId}");
            }

            var health = control != null && control.Health != null ? control.Health : FindAiHealth(found.gameObject);
            if (health != null)
            {
                _realAiHealth[spawn.AiId] = health;
                if (health is Health typedHealth)
                {
                    typedHealth.SetHealth(Mathf.Max(1f, spawn.Hp));
                    Log($"preset ai hp applied: id={spawn.AiId} hp={spawn.Hp}");
                }
                var hp = ReadHealth(health);
                if (hp >= 0f)
                    _realAiLastHp[spawn.AiId] = hp;
            }
            else
            {
                // The preset character builds its Health under the model root; binding is
                // retried from UpdateNativeAi so a kill can still be reported.
                Log($"preset ai health missing: id={spawn.AiId} (will retry)");
            }

            if (enabledRenderers == 0)
            {
                // the preset spawns a full model; never glue a player model onto it
                Log($"preset ai has no renderers yet: id={spawn.AiId} (leaving as-is)");
            }

            _pendingRealAiSpawns = Mathf.Max(0, _pendingRealAiSpawns - 1);
        }

        private void UpdateSpawnEvents()
        {
            if (_client != null && _client.AiSpawnCount != _lastAiSpawnCount)
            {
                _lastAiSpawnCount = _client.AiSpawnCount;
                Log($"ai spawn events received: {_client.AiSpawnCount}, known={_client.AiSpawns.Count}");
                SyncAiGhosts();
            }
        }
        /// <summary>Diagnostic: are the host's real AI actually translating/active?</summary>
        private void LogHostAiMotion()
        {
            if (_realAi.Count == 0)
                return;
            var main = CharacterMainControl.Main;
            var moved = 0f;
            var movers = 0;
            var inactive = 0;
            var detail = new List<string>();
            foreach (var pair in _realAi)
            {
                var ai = pair.Value;
                if (ai == null)
                    continue;
                if (!ai.activeInHierarchy)
                    inactive++;
                var position = ai.transform.position;
                var delta = -1f;
                if (_realAiDiagPos.TryGetValue(pair.Key, out var previous))
                {
                    delta = Vector3.Distance(previous, position);
                    moved += delta;
                    if (delta > 0.05f)
                        movers++;
                }
                _realAiDiagPos[pair.Key] = position;

                // M2-v11 diagnosis: a server-active AI that is deactivated (or pinned) on the
                // owner is exactly what a client sees as a frozen replica. Only name the
                // suspicious ones (far away and not moving, or inactive) to keep logs quiet.
                var distance = main != null
                    ? Vector3.Distance(main.transform.position, position)
                    : 0f;
                var suspicious = !ai.activeInHierarchy ||
                                 (delta >= 0f && delta <= 0.01f && distance > AiActivationRadiusMeters);
                if (!suspicious)
                    continue;
                var controller = ai.GetComponent<AICharacterController>();
                var agent = ai.GetComponent<UnityEngine.AI.NavMeshAgent>();
                detail.Add($"id={pair.Key} activeSelf={ai.activeSelf} hier={ai.activeInHierarchy} " +
                           $"ctrl={(controller != null && controller.enabled)} " +
                           $"agent={(agent != null && agent.enabled)} " +
                           $"dist={distance:F0}m moved3s={(delta >= 0f ? delta : 0f):F2}m");
            }
            Log($"[ai] host motion: total={_realAi.Count} inactive={inactive} movers={movers} moved={moved:F1}m");
            for (var i = 0; i < detail.Count && i < 12; i++)
                Log($"[ai] host detail {detail[i]}");
        }

        private void UpdateNativeAi()
        {
            if (Time.unscaledTime >= _nextAiMotionLog)
            {
                _nextAiMotionLog = Time.unscaledTime + 3f;
                LogHostAiMotion();
            }
            if (_client.State == ClientState.InGame && _realAi.Count > 0 &&
                Time.unscaledTime >= _nextAiStateSend)
            {
                _nextAiStateSend = Time.unscaledTime + 0.1f;
                foreach (var pair in _realAi)
                {
                    var ai = pair.Value;
                    if (ai == null)
                        continue;

                    var position = ai.transform.position;
                    var yaw = ai.transform.eulerAngles.y;
                    var visualCharacter = ai.GetComponent<CharacterMainControl>();
                    if (visualCharacter == null)
                    {
                        var controller = ai.GetComponent<AICharacterController>();
                        if (controller != null)
                            visualCharacter = controller.CharacterMainControl;
                    }
                    if (visualCharacter == null)
                        visualCharacter = ai.GetComponentInChildren<CharacterMainControl>(true);

                    // Sync the visual model rotation, like the reference mod does: the
                    // body is driven by characterModel/modelRoot, not by the root yaw.
                    if (visualCharacter != null)
                    {
                        if (visualCharacter.characterModel != null)
                            yaw = visualCharacter.characterModel.transform.eulerAngles.y;
                        else if (visualCharacter.modelRoot != null)
                            yaw = visualCharacter.modelRoot.eulerAngles.y;
                    }

                    _client.SendAiState(
                        pair.Key,
                        _client.PlayerId,
                        (int)(position.x * 100f),
                        (int)(position.y * 100f),
                        (int)(position.z * 100f),
                        (ushort)(Mathf.Repeat(yaw, 360f) / 360f * 65535f),
                        1);

                    if (!_realAiHealth.TryGetValue(pair.Key, out var health) || health == null)
                    {
                        // The preset character may build its Health after the 3s spawn
                        // check; bind lazily so kills are still reported to the server.
                        health = FindAiHealth(ai);
                        if (health != null)
                        {
                            var bound = ReadHealth(health);
                            _realAiHealth[pair.Key] = health;
                            _realAiLastHp[pair.Key] = bound;
                            Log($"ai health bound: id={pair.Key} hp={bound:F0}");
                        }
                    }

                    if (health != null && _realAiLastHp.TryGetValue(pair.Key, out var lastHp))
                    {
                        var hp = ReadHealth(health);
                        if (hp >= 0f && lastHp >= 0f && hp < lastHp - 0.5f)
                        {
                            var damage = Mathf.Max(1, Mathf.RoundToInt(lastHp - hp));
                            if (UseLegacyHitReport)
                        _client.SendHitReport(++_correlation, 1, pair.Key, damage);
                            Log($"ai hit reported: id={pair.Key} damage={damage} hp={hp:F0}/{lastHp:F0}");
                        }

                        _realAiLastHp[pair.Key] = hp;
                    }
                }
            }
        }

        private bool _aiActivationForced;

        /// <summary>
        /// v1 spawn activation: use the coop defaults (50 m wake / 60 m sleep).
        /// HarmonySpike applies the deactivation hysteresis over the native
        /// SetActiveByPlayerDistance component.
        /// </summary>
        private void ForceAiActivation()
        {
            if (_aiActivationForced)
                return;

            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var type = FindTypeByName("SetActiveByPlayerDistance");
                if (type == null)
                    return;

                object instance = null;
                var found = UnityEngine.Object.FindObjectsOfType(type);
                if (found != null && found.Length > 0)
                    instance = found[0];

                if (instance == null)
                {
                    var levelManagerType = ResolveGameType("LevelManager");
                    var levelManager = levelManagerType
                        ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                        ?.GetValue(null);
                    if (levelManager != null)
                    {
                        foreach (var field in levelManagerType.GetFields(flags))
                        {
                            if (!type.IsAssignableFrom(field.FieldType))
                                continue;
                            instance = field.GetValue(levelManager);
                            if (instance != null)
                                break;
                        }
                    }
                }

                if (instance == null)
                    return;

                var distance = type.GetField("distance", flags);
                if (distance != null && distance.FieldType == typeof(float))
                    distance.SetValue(instance, AiActivationRadiusMeters);
                else if (instance is Behaviour behaviour)
                    behaviour.enabled = false;

                _aiActivationForced = true;
                Log($"[ai] activation radius configured to {AiActivationRadiusMeters}m (sleep {AiDeactivationRadiusMeters}m)");
            }
            catch (Exception ex)
            {
                Log("[ai] activation force failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static object FindAiHealth(GameObject ai)
        {
            if (ai == null)
                return null;

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var controller = ai.GetComponent<AICharacterController>();
                var control = controller != null ? controller.CharacterMainControl : null;
                if (control != null && control.Health != null)
                    return control.Health;
            }
            catch
            {
                // fall through to the component scan
            }

            try
            {
                var receiver = ai.GetComponentInChildren<DamageReceiver>(true);
                var health = receiver != null
                    ? typeof(DamageReceiver).GetField("health", any)?.GetValue(receiver)
                    : null;
                if (health != null)
                    return health;
            }
            catch
            {
                // fall through to CharacterMainControl.Health
            }

            try
            {
                var character = ai.GetComponentInChildren<CharacterMainControl>(true);
                if (character != null && character.Health != null)
                    return character.Health;
            }
            catch
            {
                // no character on this object
            }

            return null;
        }
    }
}
