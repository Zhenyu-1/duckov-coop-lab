using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DuckovCoop.Client;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private static readonly Dictionary<string, string> OpenMaps = new Dictionary<string, string>
        {
            { "Level_GroundZero_Main", "零号区" },
            { "Level_HiddenWarehouse_Main", "仓库区" },
            { "Level_StormZone_Main", "风暴区" },
            { "Level_Farm_Main", "农场镇" },
            { "Level_JLab_Main", "J-Lab实验室" },
            { "Level_SnowMilitaryBase_Main", "雪地军事基地" },
        };

        private struct SceneChoice
        {
            public string Id;
            public string Label;
        }

        private readonly List<string> _sceneLines = new List<string>();
        private readonly List<SceneChoice> _sceneChoices = new List<SceneChoice>();
        private int _sceneIndex;
        private int _lastSceneLoadCount;
        private bool _autoLoadScene = true;
        private bool _scenesFromGame;
        private float _nextSceneProbe;
        private string _lastActiveScene = string.Empty;
        private bool _spawnReported;
        private float _spawnReportAt;
        private float _sceneStableAt;
        // 2026-11-16: the CharacterItem wait used to delay the spawn report by up to 3 s. The
        // server now resends SInitialInventory until the client acks it (2a5e195), so the
        // report no longer has to wait: AI targeting needs the player's scene/position, and a
        // late report left the player unattackable for the whole wait.
        private bool _spawnItemWaitLogged;
        private float _nextSpawnReportPendingLog;
        private int _lastSpawnAssign;
        private Vector3 _teleportTarget;
        private float _teleportUntil;
        private bool _inRaidScene;
        private string _raidSceneName = string.Empty;
        private bool _autoExtractSent;
        private bool _suppressSpawns = true;
        private bool _clearAi = true;
        private float _nextSpawnerSweep;
        private float _nativeClearAt;
        private float _suppressAfter;
        private bool _resumePendingTeleport;
        private Vector3 _resumeTarget;
        private static float _lastSuppressLog;
        private static float _lastSpawnerCleanupLog;
        private readonly List<SceneChoice> _pendingChoices = new List<SceneChoice>();

        internal bool ShouldBlockNativeSpawner =>
            _suppressSpawns && _inRaidScene && _client != null && _client.State == ClientState.InGame;

        /// <summary>T5: the server owns authored containers as soon as the session is in game.</summary>
        internal bool ShouldBlockNativeContainers =>
            _client != null && _client.State == ClientState.InGame;

        private void LoadScenes()
        {
            _sceneChoices.Clear();
            _sceneLines.Clear();

            if (TryLoadScenesFromGame())
            {
                SelectDefaultScene();
                _scenesFromGame = true;
                Log($"scenes from game: {_sceneChoices.Count}");
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location);
                var path = Path.Combine(directory ?? string.Empty, "scenes.txt");
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var name = line.Trim();
                        if (name.Length > 0 && !name.StartsWith("#"))
                            _sceneLines.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("scenes.txt error: " + ex.Message);
            }

            foreach (var line in _sceneLines)
                _sceneChoices.Add(new SceneChoice { Id = line, Label = line });

            if (_sceneChoices.Count == 0)
            {
                foreach (var name in new[]
                         {
                             "Level_GroundZero_1", "Level_StormZone_Main", "Level_SnowMilitaryBase_Main",
                             "Level_Zombie_Main", "Level_StormPast_Main",
                         })
                    _sceneChoices.Add(new SceneChoice { Id = name, Label = name });
            }

            _sceneIndex = 0;
            Log($"scenes from fallback: {_sceneChoices.Count}");
        }

        /// <summary>
        /// The shipped server content is GroundZero-only. Selecting whatever scene the game
        /// happens to enumerate first (currently Farm) used to start an unsupported map and
        /// assign GroundZero spawn coordinates there, dropping the client through the world.
        /// Default the selector to GroundZero; other entries remain available for later data.
        /// </summary>
        private void SelectDefaultScene()
        {
            _sceneIndex = 0;
            for (var i = 0; i < _sceneChoices.Count; i++)
            {
                if (_sceneChoices[i].Id != "Level_GroundZero_Main")
                    continue;
                _sceneIndex = i;
                return;
            }
        }

        private bool TryLoadScenesFromGame()
        {
            try
            {
                var type = FindTypeByName("SceneInfoCollection");
                if (type == null)
                {
                    Log("SceneInfoCollection not found");
                    return false;
                }

                var instance = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (instance == null)
                {
                    var found = Resources.FindObjectsOfTypeAll(type);
                    if (found != null && found.Length > 0)
                    {
                        instance = found[0];
                        Log($"SceneInfoCollection.Instance null, found {found.Length} object(s)");
                    }
                }

                if (instance == null)
                {
                    Log("SceneInfoCollection.Instance is null");
                    return false;
                }

                var entries = type.GetProperty("Entries", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance)
                              as System.Collections.IEnumerable;

                if (entries == null)
                {
                    entries = type.GetField("entries", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(instance) as System.Collections.IEnumerable;
                }

                if (entries == null)
                {
                    Log($"SceneInfoCollection.Entries is null (instance type {instance.GetType().FullName})");
                    return false;
                }

                _pendingChoices.Clear();
                foreach (var entry in entries)
                {
                    if (entry == null)
                        continue;

                    var entryType = entry.GetType();
                    var id = entryType.GetField("id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(entry) as string;
                    var display = entryType.GetField("displayName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(entry) as string;

                    if (string.IsNullOrEmpty(id))
                    {
                        Log($"scene entry skipped (empty id): display='{display}'");
                        continue;
                    }

                    if (!OpenMaps.TryGetValue(id, out var label))
                        continue;

                    _pendingChoices.Add(new SceneChoice { Id = id, Label = label });
                }

                if (_pendingChoices.Count == 0)
                    return false;

                _sceneChoices.Clear();
                _sceneChoices.AddRange(_pendingChoices);
                SelectDefaultScene();
                return true;
            }
            catch (Exception ex)
            {
                Log("scene collection failed: " + ex.Message);
                return false;
            }
        }

        private void UpdateSpawnAnchor()
        {
            var activeScene = SceneManager.GetActiveScene().name;
            if (activeScene != _lastActiveScene)
            {
                _lastActiveScene = activeScene;
                _spawnReported = false;
                _spawnAnchorApplied = false;
                // 2026-11-16: a raid level is two scene activations (Level_GroundZero_Main then
                // its subscene Level_GroundZero_1, ~0.5 s apart). Reporting from the first one
                // started the server raid at the wrong anchor and caused a second report. Wait
                // until the active scene stops changing.
                _sceneStableAt = Time.unscaledTime + 1.5f;
                // 2026-11-16: unscaled time. The engine pauses/scales Time.time during the
                // loading fade, which stretched this gate into ~9 s of wall time and kept the
                // player invisible to server-side AI targeting for that whole window.
                _spawnReportAt = Time.unscaledTime + 0.5f;
                Log("active scene changed: " + activeScene);

                // scene switches (including death -> results -> new raid) invalidate remote visuals
                foreach (var pair in _remotes)
                {
                    if (pair.Value != null)
                        UnityEngine.Object.Destroy(pair.Value);
                }
                _remotes.Clear();
                _seenPlayers.Clear();

                foreach (var pair in _aiGhosts)
                {
                    if (pair.Value != null)
                        UnityEngine.Object.Destroy(pair.Value);
                }
                _aiGhosts.Clear();
                _processedAiSpawns.Clear();

                // only a scene the server told us to enter counts as a coop raid scene
                if (activeScene.StartsWith("Level_") && _raidSpawnAllowed)
                {
                    _inRaidScene = true;
                    _raidSceneName = activeScene;
                    // readiness window covers the server party gate and a slow second client
                    _loadGateDeadline = Time.unscaledTime + 45f;
                    _autoExtractSent = false;
                    _localDead = false;
                    _leaveSent = false;
                    _localHealth = null;
                    _localLastHp = -1f;

                    // give spawners time to initialise before we disable them,
                    // otherwise our own spawning API cannot find a valid spawner
                    _suppressAfter = Time.time + 3f;
                    if (_clearAi)
                    {
                        DestroyLocalAi();
                        _nativeClearAt = 0f;
                    }

                    if (_resumePendingTeleport)
                    {
                        _resumePendingTeleport = false;
                        _teleportTarget = _resumeTarget;
                        _teleportUntil = Time.unscaledTime + 3f;
                        _spawnReported = false;
                        Log("resume teleport applied");
                    }
                }
                else if (_inRaidScene)
                {
                    var wasRaidActive = _raidSpawnAllowed;
                    _inRaidScene = false;
                    _raidSpawnAllowed = false;
                    Log($"left raid scene '{_raidSceneName}' -> '{activeScene}'");

                    var wasDead = _localDead;
                    CleanupRaidState();
                    ClearAllPlayerV8WireState();
                    ClearRemotePlayers("left raid scene");

                    if (wasRaidActive && !wasDead && _client != null &&
                        _client.State == ClientState.InGame && !_autoExtractSent &&
                        !_suppressAutoExtract)
                    {
                        _autoExtractSent = true;
                        _client.SendExtract(++_correlation);
                        _interaction = "raid ended, reporting extract";
                        Log("auto extract reported");
                    }
                    else if (wasDead)
                    {
                        Log("left raid after death, no extract reported");
                    }
                }
            }

            if (!_spawnReported && _raidSpawnAllowed && !_localDead && _client.State == ClientState.InGame &&
                _inRaidScene && Time.unscaledTime >= _spawnReportAt && Time.unscaledTime >= _teleportUntil &&
                Time.unscaledTime >= _sceneStableAt && MapVariantSettled)
            {
                var main = LocalMainCharacter();
                if (main != null)
                {
                    // 2026-11-16: report even with a null CharacterItem. The server resends
                    // SInitialInventory until the client acks it, and the apply is deferred
                    // until the native item exists, so waiting here only kept the player
                    // invisible to AI targeting (no scene/position on the server).
                    if (main.CharacterItem == null && !_spawnItemWaitLogged)
                    {
                        _spawnItemWaitLogged = true;
                        Log("[spawn] reporting before CharacterItem; server will resend the inventory");
                    }

                    var position = main.transform.position;
                    _spawnReported = true;
                    _spawnItemWaitLogged = false;
                    _client.SendPlayerSpawn(activeScene,
                        (int)(position.x * 100f), (int)(position.y * 100f), (int)(position.z * 100f), _raidEpoch);
                    Log($"spawn reported: scene={activeScene} epoch={_raidEpoch} pos={position.x:F1},{position.y:F1},{position.z:F1}");
                }
                else if (Time.time >= _nextSpawnReportPendingLog)
                {
                    _nextSpawnReportPendingLog = Time.time + 1f;
                    Log("[spawn] report pending: main not available yet");
                }
            }
            else if (!_spawnReported && Time.time >= _nextSpawnReportPendingLog)
            {
                _nextSpawnReportPendingLog = Time.time + 1f;
                Log($"[spawn] report pending: state={_client.State} inRaid={_inRaidScene} " +
                    $"allowed={_raidSpawnAllowed} dead={_localDead} " +
                    $"t={Time.unscaledTime:F1} at={_spawnReportAt:F1} tp={_teleportUntil:F1}");
            }

            if (_inRaidScene && _client.SpawnAssignCount != _lastSpawnAssign)
            {
                _lastSpawnAssign = _client.SpawnAssignCount;
                var anchor = _client.SpawnAnchor;
                var main = LocalMainCharacter();
                if (main != null)
                {
                    var offset = 1.5f * Mathf.Max(0, anchor.Seat);
                    _teleportTarget = new Vector3(
                        anchor.X / 100f + offset,
                        anchor.Y / 100f,
                        anchor.Z / 100f);
                    _teleportUntil = Time.unscaledTime + 3f;
                    main.transform.position = _teleportTarget;
                    Log($"teleported to spawn anchor: {anchor.X / 100f:F1},{anchor.Y / 100f:F1},{anchor.Z / 100f:F1} offset={offset:F1}");
                _spawnAnchorApplied = true;
                }
            }
        }

        private static bool TryLoadSceneViaGame(string sceneName)
        {
            try
            {
                var type = FindTypeByName("SceneLoader");
                if (type == null)
                {
                    Log("SceneLoader type not found");
                    return false;
                }

                var instance = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (instance == null)
                {
                    Log("SceneLoader.Instance is null");
                    return false;
                }

                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "LoadScene")
                    .ToList();

                // prefer the overload that exposes `useLocation`, so we can skip location binding
                var method = methods.FirstOrDefault(m =>
                {
                    var parameters = m.GetParameters();
                    return parameters.Length > 1 &&
                           parameters[0].ParameterType == typeof(string) &&
                           parameters.Any(p => p.Name == "useLocation");
                });

                var useLocationOverload = method != null;

                method ??= methods.FirstOrDefault(m =>
                {
                    var parameters = m.GetParameters();
                    return parameters.Length > 1 &&
                           parameters[0].ParameterType == typeof(string) &&
                           parameters[1].ParameterType.IsValueType;
                });

                if (method == null)
                {
                    Log("SceneLoader.LoadScene(string, ...) not found");
                    return false;
                }

                var ps = method.GetParameters();
                var args = new object[ps.Length];
                for (var i = 1; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                }

                args[0] = sceneName;

                for (var i = 1; i < ps.Length; i++)
                {
                    switch (ps[i].Name)
                    {
                    case "doCircleFade": args[i] = true; break;
                    case "clickToConinue": args[i] = false; break;
                    case "notifyEvacuation": args[i] = false; break;
                    case "saveToFile": args[i] = false; break;
                    case "hideTips": args[i] = false; break;
                    case "useLocation": args[i] = false; break;
                    }
                }

                Log($"SceneLoader overload: {ps.Length} params, useLocation={useLocationOverload}");
                method.Invoke(instance, args);
                Log("SceneLoader.LoadScene issued: " + sceneName);
                return true;
            }
            catch (Exception ex)
            {
                Log("SceneLoader.LoadScene failed: " + ex);
                return false;
            }
        }

        private static void SuppressSpawners()
        {
            try
            {
                var disabled = 0;
                var destroyed = 0;
                foreach (var spawner in UnityEngine.Object.FindObjectsOfType<CharacterSpawnerComponentBase>(true))
                {
                    spawner.enabled = false;
                    disabled++;
                }

                foreach (var root in UnityEngine.Object.FindObjectsOfType<CharacterSpawnerRoot>(true))
                {
                    root.enabled = false;
                    disabled++;

                    // Disabling the root does not stop a WaveCharacterSpawner coroutine
                    // that already started. Night spiders / storm shadows are added to
                    // CharacterSpawnerRoot.CreatedCharacters, so remove those native
                    // characters explicitly. Coop-owned AI never appears in this list.
                    var created = root.CreatedCharacters;
                    if (created == null)
                        continue;
                    foreach (var character in created.ToArray())
                    {
                        if (character == null)
                            continue;
                        var go = character.gameObject;
                        if (go == null || go.name.StartsWith("CoopAi_", StringComparison.Ordinal) ||
                            go.name.StartsWith("DuckovCoop_AiReplica_", StringComparison.Ordinal) ||
                            go.GetComponentInParent<RemoteAIReplicaTag>() != null)
                            continue;
                        UnityEngine.Object.Destroy(go);
                        destroyed++;
                    }
                }

                if (destroyed > 0 && Time.unscaledTime - _lastSpawnerCleanupLog > 10f)
                {
                    _lastSpawnerCleanupLog = Time.unscaledTime;
                    Log($"[spawn] removed {destroyed} native spawner character(s)");
                }

                if (Time.unscaledTime - _lastSuppressLog > 10f)
                {
                    _lastSuppressLog = Time.unscaledTime;
                    Log($"spawners suppressed: {disabled} (alive ais={UnityEngine.Object.FindObjectsOfType<AICharacterController>().Length})");
                }
            }
            catch
            {
                // scene transitions can invalidate objects mid-iteration
            }
        }

        private static void DestroyLocalAi()
        {
            try
            {
                var removed = 0;
                foreach (var ai in UnityEngine.Object.FindObjectsOfType<AICharacterController>())
                {
                    if (ai == null)
                        continue;
                    UnityEngine.Object.Destroy(ai.gameObject);
                    removed++;
                }

                Log($"local ai cleared: {removed}");
            }
            catch (Exception ex)
            {
                Log("clear ai failed: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static void ClearUntaggedAi()
        {
            try
            {
                var removed = 0;
                foreach (var ai in UnityEngine.Object.FindObjectsOfType<AICharacterController>())
                {
                    if (ai == null || ai.gameObject.name.StartsWith("CoopAi_"))
                        continue;

                    // Native-preset replicas are AICharacterControllers too; never let
                    // the local cleanup eat them.
                    if (ai.gameObject.name.StartsWith("DuckovCoop_AiReplica_") ||
                        ai.GetComponentInParent<RemoteAIReplicaTag>() != null)
                        continue;

                    UnityEngine.Object.Destroy(ai.gameObject);
                    removed++;
                }

                if (removed > 0)
                    Log($"cleared {removed} native ai");
            }
            catch
            {
                // scene transitions can invalidate objects mid-iteration
            }
        }

        private static void BlockMapTeleporters()
        {
            try
            {
                var blocked = 0;
                foreach (var teleporter in UnityEngine.Object.FindObjectsOfType<Duckov.Scenes.MultiSceneTeleporter>())
                {
                    if (teleporter == null || !teleporter.enabled)
                        continue;
                    teleporter.enabled = false;
                    blocked++;
                }

                if (blocked > 0)
                    Log($"blocked {blocked} map teleporters");
            }
            catch
            {
                // scene transitions can invalidate objects mid-iteration
            }
        }

        private static string NormalizeRaidScene(string scene)
        {
            if (string.IsNullOrEmpty(scene))
                return scene;

            if (OpenMaps.ContainsKey(scene))
                return scene;

            // sub-scenes like Level_GroundZero_1 cannot be loaded on their own
            var trimmed = System.Text.RegularExpressions.Regex.Replace(scene, "_\\d+$", "_Main");
            if (OpenMaps.ContainsKey(trimmed))
                return trimmed;

            return scene;
        }

        private void UpdateWorldGating()
        {
            if (_inRaidScene)
                ForceAiActivation();

            if (_clearAi && _nativeClearAt > 0f && Time.time >= _nativeClearAt)
            {
                _nativeClearAt = 0f;
                ClearUntaggedAi();
            }

            if (_suppressSpawns && _inRaidScene && Time.time >= _suppressAfter && Time.unscaledTime >= _nextSpawnerSweep)
            {
                _nextSpawnerSweep = Time.unscaledTime + 3f;
                SuppressSpawners();
                BlockMapTeleporters();

                // Non-owners must never keep AI the host does not know about. Skip while
                // our own preset spawn is in flight: those characters are not renamed or
                // registered yet and would be swept as "native".
                if (_realAi.Count == 0 && _pendingRealAiSpawns == 0)
                    ClearUntaggedAi();
            }
        }
        private void UpdateSceneProbe()
        {
            if (!_scenesFromGame && Time.unscaledTime >= _nextSceneProbe)
            {
                _nextSceneProbe = Time.unscaledTime + 5f;
                if (TryLoadScenesFromGame())
                {
                    _scenesFromGame = true;
                    _sceneIndex = 0;
                    Log($"scenes from game (late): {_sceneChoices.Count}");
                }
            }
        }
        private void HandleSceneLoadRequests()
        {
            if (_client.SceneLoadRequests != _lastSceneLoadCount)
            {
                _lastSceneLoadCount = _client.SceneLoadRequests;
                Log($"raid start requested by server: scene={_client.RequestedScene} epoch={_client.RaidEpoch}");

                // a new raid: allow exactly one spawn report until the player leaves it
                _raidEpoch = _client.RaidEpoch;
                _raidSpawnAllowed = true;
                _inRaidScene = false; // the old Level_* scene (if any) must not auto-extract
                _localDead = false;
                _leaveSent = false;
                _autoExtractSent = false;
                ResetExtractionState();

                if (_autoLoadScene && !string.IsNullOrEmpty(_client.RequestedScene))
                {
                    // 2026-11-16: hide the ~8 s map-variant / raid-start work behind the game's
                    // own loading curtain instead of freezing a visible world.
                    ExtendNativeLoadingCurtain();
                    if (!TryLoadSceneViaGame(_client.RequestedScene))
                        Log("scene load skipped: SceneLoader not available");
                }
            }
        }
        private void UpdateTeleport()
        {
            if (Time.unscaledTime < _teleportUntil)
            {
                var main = LocalMainCharacter();
                if (main != null)
                {
                    main.transform.position = _teleportTarget;
                    // Do NOT clear _spawnReported here. This teleport is usually the
                    // SSpawnAssign response to the spawn report; clearing it makes the
                    // client report again after the 3s hold, which the server treats as
                    // a late spawn and answers with another assign -> permanent pin.
                }
            }
        }
    }
}
