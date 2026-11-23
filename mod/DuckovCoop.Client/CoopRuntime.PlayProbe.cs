using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    /// <summary>
    /// PLAY-owned, read-only scene probe for B0 spawn data and the M2 map/spawn work.
    ///
    /// It never changes the scene: it only walks the loaded level and writes a JSON
    /// snapshot of spawners, presets, containers, exits, NPCs, breakables and AI
    /// activation components. Disabled by default; opt in with either
    ///   [debug] sceneProbe = 1   in coop.ini
    /// or a file named play_scene_probe.on next to the mod DLL.
    ///
    /// Output: Application.persistentDataPath/play_scene_probe/scene_<name>_p<pass>_<time>.json
    /// Pass 1 runs ~2s after a Level_* scene loads, pass 2 ~6s (before the native AI
    /// cleanup at ~8s in CoopRuntime.Scene.cs). This is diagnostics only: no wire,
    /// no server, no gameplay state changes.
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private const string PlayProbeConfigSection = "debug";
        private const string PlayProbeConfigKey = "sceneprobe";
        private const string PlayProbeGroundLootKey = "groundlootprobe";
        private const string PlayProbeSentinelName = "play_scene_probe.on";
        private const int PlayProbeMaxArrayItems = 32;
        private const int PlayProbeMaxFields = 160;
        private const int PlayProbeMaxInstancesPerType = 400;
        private const int PlayProbeMaxChildren = 128;
        private const int PlayProbeMaxString = 400;

        private bool _playProbeConfigLoaded;
        private bool _playProbeEnabled;
        private bool _playProbeTargetsOnly;
        private bool _playProbeForceGroundLoot;
        private bool _playProbeRunning;
        private string _playProbeScene = string.Empty;
        private int _playProbePass;
        private float _playProbeFirstAt;
        private float _playProbeSecondAt;
        private float _playProbeThirdAt;
        private Dictionary<string, Type> _playProbeTypeIndex;
        private HashSet<object> _playProbeSeen;
        private Scene _playProbeSceneCache;
        private List<MonoBehaviour> _playProbeSceneBehaviourCache;
        private List<Component> _playProbeSceneComponentCache;
        private Dictionary<Type, List<Component>> _playProbeComponentQueryCache;

        private static readonly string[] PlayProbeDiscoveryKeywords =
        {
            "spawn", "loot", "container", "box", "exit", "extract", "teleport", "transition",
            "passage", "door", "gate", "break", "destruct", "repair", "bridge", "barricade",
            "obstacle", "npc", "merchant", "trader", "vendor", "quest", "dialogue", "interact",
            "pickup", "itemagent", "dropped", "worlditem", "manhole", "sewer", "pipe",
            "shortcut", "tunnel", "vent", "ladder", "elevator", "checkpoint", "exfil",
        };

        private static readonly string[] PlayProbeUiSuffixes =
        {
            "UI", "View", "Panel", "Window", "Slot", "Button", "Text", "Image", "Icon", "Canvas",
        };

        // ---------- lifecycle ----------

        private void UpdatePlaySceneProbe()
        {
            if (!_playProbeConfigLoaded)
                LoadPlayProbeConfig();

            if (!_playProbeEnabled || _playProbeRunning)
                return;

            var scenes = PlayProbeLoadedLevelScenes();
            if (scenes.Count == 0)
                return;

            var signature = PlayProbeSceneSignature(scenes);
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.name.StartsWith("Level_", StringComparison.Ordinal))
                scene = scenes[0];

            if (!string.Equals(_playProbeScene, signature, StringComparison.Ordinal))
            {
                _playProbeScene = signature;
                _playProbePass = 0;
                _playProbeFirstAt = Time.unscaledTime + 2f;
                _playProbeSecondAt = Time.unscaledTime + 6f;
                _playProbeThirdAt = Time.unscaledTime + 20f;
                Log($"[playprobe] armed scenes={signature} pass1=+2s pass2=+6s pass3=+20s");
            }

            if (_playProbePass == 0 && Time.unscaledTime >= _playProbeFirstAt)
            {
                _playProbePass = 1;
                DumpPlaySceneProbe(scene, 1);
            }
            else if (_playProbePass == 1 && Time.unscaledTime >= _playProbeSecondAt)
            {
                _playProbePass = 2;
                DumpPlaySceneProbe(scene, 2);
                Log("[playprobe] middle pass done");
            }
            else if (_playProbePass == 2 && Time.unscaledTime >= _playProbeThirdAt)
            {
                _playProbePass = 3;
                DumpPlaySceneProbe(scene, 3);
                Log("[playprobe] done");
            }
        }

        private static List<Scene> PlayProbeLoadedLevelScenes()
        {
            var result = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                if (scene.name.StartsWith("Level_", StringComparison.Ordinal) ||
                    (scene.path ?? string.Empty).IndexOf("/Level_", StringComparison.Ordinal) >= 0)
                    result.Add(scene);
            }
            return result;
        }

        private static string PlayProbeSceneSignature(List<Scene> scenes)
        {
            return string.Join("|", scenes
                .Select(s => s.name)
                .OrderBy(s => s, StringComparer.Ordinal));
        }

        private void LoadPlayProbeConfig()
        {
            _playProbeConfigLoaded = true;
            try
            {
                var directory = Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location) ?? string.Empty;
                var iniPath = Path.Combine(directory, "coop.ini");
                if (File.Exists(iniPath))
                {
                    var section = string.Empty;
                    foreach (var line in File.ReadAllLines(iniPath))
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
                        if (split <= 0)
                            continue;

                        var key = trimmed.Substring(0, split).Trim().ToLowerInvariant();
                        var value = trimmed.Substring(split + 1).Trim();
                        if (section == PlayProbeConfigSection && key == PlayProbeConfigKey)
                        {
                            _playProbeEnabled = value == "1" ||
                                                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                                value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (section == PlayProbeConfigSection && key == PlayProbeGroundLootKey)
                        {
                            _playProbeForceGroundLoot = value == "1" ||
                                                        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                                        value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }

                if (File.Exists(Path.Combine(directory, PlayProbeSentinelName)))
                    _playProbeEnabled = true;

                if (File.Exists(Path.Combine(directory, "groundzero_targets.on")))
                {
                    _playProbeEnabled = true;
                    _playProbeTargetsOnly = true;
                }
            }
            catch (Exception ex)
            {
                Log("[playprobe] config error: " + ex.GetType().Name + ": " + ex.Message);
            }

            Log($"[playprobe] enabled={_playProbeEnabled} output={PlayProbeOutputDirectory()}");
        }

        private static string PlayProbeOutputDirectory()
        {
            return Path.Combine(Application.persistentDataPath, "play_scene_probe");
        }

        // ---------- dump ----------

        private void DumpPlaySceneProbe(Scene scene, int pass)
        {
            _playProbeRunning = true;
            var started = Time.realtimeSinceStartup;
            try
            {
                _playProbeSeen = new HashSet<object>(PlayProbeReferenceComparer.Instance);
                _playProbeSceneCache = scene;
                _playProbeSceneBehaviourCache = null;
                _playProbeSceneComponentCache = null;
                _playProbeComponentQueryCache = new Dictionary<Type, List<Component>>();

                if (_playProbeForceGroundLoot && pass <= 2)
                    ForcePlayProbeLootSpawners(scene);

                var directory = PlayProbeOutputDirectory();
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory,
                    $"scene_{PlayProbeSafeName(scene.name)}_p{pass}_{DateTime.Now:yyyyMMdd_HHmmss}.json");

                var writer = new PlayProbeJsonWriter();
                writer.BeginObject();
                writer.Name("meta");
                WritePlayProbeMeta(writer, scene, pass);

                if (_playProbeTargetsOnly)
                {
                    writer.Name("mapVariantTargets");
                    WritePlayProbeMapVariantTargets(writer, scene);
                    writer.Name("navMeshAnchors");
                    WritePlayProbeNavMeshAnchors(writer);
                    writer.EndObject();

                    File.WriteAllText(file, writer.ToString(), new UTF8Encoding(false));
                    var targetsElapsed = (Time.realtimeSinceStartup - started) * 1000f;
                    Log($"[playprobe] dumped targets pass={pass} scene={scene.name} file={file} " +
                        $"bytes={writer.Length} elapsed={targetsElapsed:F0}ms");
                    return;
                }

                writer.Name("sceneInfo");
                WritePlayProbeSceneInfo(writer);
                writer.Name("typeHistogram");
                WritePlayProbeTypeHistogram(writer, scene);
                writer.Name("spawners");
                WritePlayProbeSpawners(writer, scene);
                writer.Name("points");
                WritePlayProbePoints(writer, scene);
                writer.Name("containers");
                WritePlayProbeKeywordObjects(writer, scene, "containers",
                    "lootbox", "loot_box", "lootboxloader", "lootcontainer", "itembox", "supplybox");
                writer.Name("lootSpawners");
                WritePlayProbeKeywordObjects(writer, scene, "lootSpawners", "lootspawner");
                writer.Name("groundItems");
                WritePlayProbeGroundItems(writer, scene);
                writer.Name("exits");
                WritePlayProbeKeywordObjects(writer, scene, "exits",
                    "exit", "extract", "evac", "teleport", "transition", "passage", "manhole",
                    "sewer", "shortcut", "tunnel", "elevator", "ladder");
                writer.Name("doors");
                WritePlayProbeKeywordObjects(writer, scene, "doors", "door", "gate");
                writer.Name("breakables");
                WritePlayProbeKeywordObjects(writer, scene, "breakables",
                    "breakable", "destructible", "repair", "bridge", "barricade", "obstacle");
                writer.Name("walls");
                WritePlayProbeKeywordObjects(writer, scene, "walls", "wall");
                writer.Name("npcs");
                WritePlayProbeNpcs(writer, scene);
                writer.Name("activation");
                WritePlayProbeActivation(writer, scene);
                writer.Name("mapVariantTargets");
                WritePlayProbeMapVariantTargets(writer, scene);
                writer.Name("navMeshAnchors");
                WritePlayProbeNavMeshAnchors(writer);
                writer.Name("keywordTypes");
                WritePlayProbeKeywordTypes(writer, scene);
                writer.EndObject();

                File.WriteAllText(file, writer.ToString(), new UTF8Encoding(false));
                var elapsed = (Time.realtimeSinceStartup - started) * 1000f;
                Log($"[playprobe] dumped pass={pass} scene={scene.name} file={file} " +
                    $"bytes={writer.Length} elapsed={elapsed:F0}ms");
            }
            catch (Exception ex)
            {
                Log($"[playprobe] dump failed: {ex.GetType().Name}: {ex.Message}");
                Log(ex.StackTrace ?? "<no stack>");
            }
            finally
            {
                _playProbeSeen = null;
                _playProbeSceneBehaviourCache = null;
                _playProbeSceneComponentCache = null;
                _playProbeComponentQueryCache = null;
                _playProbeRunning = false;
            }
        }

        private void WritePlayProbeMeta(PlayProbeJsonWriter w, Scene scene, int pass)
        {
            w.BeginObject();
            w.Prop("sceneName", scene.name);
            w.Prop("scenePath", scene.path);
            w.Prop("sceneBuildIndex", scene.buildIndex);
            w.Name("loadedScenes");
            w.BeginArray();
            foreach (var loaded in PlayProbeLoadedLevelScenes())
            {
                w.BeginObject();
                w.Prop("name", loaded.name);
                w.Prop("path", loaded.path);
                w.Prop("buildIndex", loaded.buildIndex);
                w.Prop("active", loaded == SceneManager.GetActiveScene());
                w.EndObject();
            }
            w.EndArray();
            w.Prop("pass", pass);
            w.Prop("timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            w.Prop("unityVersion", Application.unityVersion);
            w.Prop("applicationVersion", Application.version);
            w.Prop("persistentDataPath", Application.persistentDataPath);

            w.Name("client");
            w.BeginObject();
            if (_client == null)
            {
                w.Prop("connected", false);
            }
            else
            {
                w.Prop("connected", true);
                w.Prop("state", _client.State.ToString());
                w.Prop("playerId", _client.PlayerId);
                w.Prop("raidEpoch", _client.RaidEpoch);
                w.Prop("raidPhase", _client.RaidPhase);
                w.Prop("snapshots", _client.SnapshotsReceived);
                var snapshot = _client.LatestSnapshot;
                w.Prop("players", snapshot != null ? snapshot.Players.Length : 0);
            }
            w.Prop("inRaidScene", _inRaidScene);
            w.Prop("raidSceneName", _raidSceneName ?? string.Empty);
            w.Prop("raidEpochLocal", _raidEpoch);
            w.EndObject();

            w.Name("localPlayer");
            w.BeginObject();
            var main = CharacterMainControl.Main;
            if (main == null)
            {
                w.Prop("present", false);
            }
            else
            {
                w.Prop("present", true);
                w.Prop("name", main.name);
                w.Prop("path", PlayProbePath(main.transform));
                w.Prop("pos", PlayProbeVector(main.transform.position));
                w.Prop("yaw", PlayProbeRound(main.transform.eulerAngles.y));
                w.Prop("team", PlayProbeDescribeValue(PlayProbeGetMember(main, "Team")));
            }
            w.EndObject();

            w.Name("roots");
            w.BeginArray();
            var rootCount = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null)
                    continue;
                rootCount++;
                if (rootCount > 200)
                    break;
                w.BeginObject();
                w.Prop("name", root.name);
                w.Prop("active", root.activeSelf);
                w.Prop("pos", PlayProbeVector(root.transform.position));
                w.EndObject();
            }
            w.EndArray();
            w.Prop("rootCount", scene.GetRootGameObjects().Length);
            w.EndObject();
        }

        private void WritePlayProbeSceneInfo(PlayProbeJsonWriter w)
        {
            w.BeginObject();
            var type = PlayProbeFindType("SceneInfoCollection");
            if (type == null)
            {
                w.Prop("found", false);
                w.Prop("reason", "SceneInfoCollection type not found");
                w.EndObject();
                return;
            }

            object instance = null;
            try
            {
                instance = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            }
            catch
            {
                // ignore
            }

            if (instance == null)
            {
                var found = Resources.FindObjectsOfTypeAll(type);
                if (found != null && found.Length > 0)
                    instance = found[0];
            }

            if (instance == null)
            {
                w.Prop("found", false);
                w.Prop("reason", "SceneInfoCollection instance not found");
                w.EndObject();
                return;
            }

            w.Prop("found", true);
            w.Prop("type", type.FullName);
            w.Name("fields");
            WritePlayProbeFields(w, instance, 1);

            object entries = null;
            try
            {
                entries = type.GetProperty("Entries", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);
            }
            catch
            {
                // ignore
            }

            if (entries == null)
            {
                try
                {
                    entries = type.GetField("entries", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(instance);
                }
                catch
                {
                    // ignore
                }
            }

            w.Name("entries");
            if (entries is IEnumerable enumerable)
                WritePlayProbeEnumerable(w, enumerable, 1);
            else
                w.Null();
            w.EndObject();
        }

        private void WritePlayProbeTypeHistogram(PlayProbeJsonWriter w, Scene scene)
        {
            var behaviours = PlayProbeSceneBehaviours(scene);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null)
                    continue;
                var typeName = behaviour.GetType().FullName ?? behaviour.GetType().Name;
                counts.TryGetValue(typeName, out var count);
                counts[typeName] = count + 1;
            }

            w.BeginObject();
            w.Prop("totalMonoBehaviours", behaviours.Count);
            w.Name("types");
            w.BeginArray();
            foreach (var pair in counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                w.BeginObject();
                w.Prop("type", pair.Key);
                w.Prop("count", pair.Value);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        private void WritePlayProbeSpawners(PlayProbeJsonWriter w, Scene scene)
        {
            w.BeginArray();
            var seen = new HashSet<int>();
            var typeNames = new[]
            {
                "RandomCharacterSpawner",
                "CharacterSpawnerRoot",
                "CharacterSpawnerComponentBase",
                "CharacterSpawnerBase",
                "CharacterSpawnerGroup",
                "CharacterSpawnerGroupSelector",
            };

            foreach (var typeName in typeNames)
            {
                var type = PlayProbeFindType(typeName);
                if (type == null)
                {
                    w.BeginObject();
                    w.Prop("missingType", typeName);
                    w.EndObject();
                    continue;
                }

                foreach (var component in PlayProbeSceneComponents(type, scene))
                {
                    if (component == null || !seen.Add(component.GetInstanceID()))
                        continue;

                    w.BeginObject();
                    WritePlayProbeComponentHeader(w, component);
                    w.Name("fields");
                    WritePlayProbeFields(w, component, 1);

                    if (component is RandomCharacterSpawner spawner)
                    {
                        WritePlayProbeSpawnerPresets(w, spawner);
                        WritePlayProbeSpawnPointData(w, spawner);
                    }

                    WritePlayProbeSpawnerRoot(w, component);
                    WritePlayProbeTransformChildren(w, component.transform, PlayProbeMaxChildren);
                    w.EndObject();
                }
            }
            w.EndArray();
        }

        private void WritePlayProbeSpawnPointData(PlayProbeJsonWriter w, RandomCharacterSpawner spawner)
        {
            var value = PlayProbeGetMember(spawner, "spawnPoints");
            w.Name("spawnPointData");
            if (value is Component component)
            {
                w.BeginObject();
                WritePlayProbeComponentHeader(w, component);
                w.Name("fields");
                WritePlayProbeFields(w, component, 2);
                WritePlayProbeTransformChildren(w, component.transform, PlayProbeMaxChildren);
                w.EndObject();
                return;
            }

            WritePlayProbeValue(w, value, 1);
        }

        private void WritePlayProbePoints(PlayProbeJsonWriter w, Scene scene)
        {
            w.BeginArray();
            var type = PlayProbeFindType("Points");
            if (type == null)
            {
                w.BeginObject();
                w.Prop("missingType", "Points");
                w.EndObject();
                w.EndArray();
                return;
            }

            foreach (var component in PlayProbeSceneComponents(type, scene))
            {
                if (component == null)
                    continue;
                w.BeginObject();
                WritePlayProbeComponentHeader(w, component);
                w.Name("fields");
                WritePlayProbeFields(w, component, 2);
                WritePlayProbeTransformChildren(w, component.transform, PlayProbeMaxChildren);
                w.EndObject();
            }
            w.EndArray();
        }

        /// <summary>
        /// P2 probe helper: only the local streamed area activates its LootSpawners, so a
        /// plain pass sees just one or two of the eight GroundZero spawners. When
        /// groundlootprobe=1 is set, activate all scene LootSpawners and force spawnChance=1;
        /// their Start/Setup then runs before the pass-3 groundItems scan. This is probe-only
        /// and must never be enabled during normal play.
        /// </summary>
        private void ForcePlayProbeLootSpawners(Scene scene)
        {
            var type = PlayProbeFindType("LootSpawner");
            if (type == null)
                return;

            var activated = 0;
            foreach (var component in PlayProbeSceneComponents(type, scene))
            {
                if (component == null)
                    continue;
                try
                {
                    if (!component.gameObject.activeSelf)
                        component.gameObject.SetActive(true);
                    var chance = type.GetField("spawnChance",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    chance?.SetValue(component, 1f);
                    var behaviour = component as MonoBehaviour;
                    if (behaviour != null)
                        behaviour.enabled = true;
                    activated++;
                }
                catch (Exception ex)
                {
                    Log("[playprobe] force loot spawner failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            Log($"[playprobe] force loot spawners scene={scene.name} activated={activated}");
        }

        /// <summary>
        /// P2: actual ground ItemAgents after native LootSpawner.Setup has run. This is the
        /// authoritative source for the exported world-item seed table; it deliberately
        /// excludes held/inventory items and reports TypeID/StackCount/Quality/pose.
        /// </summary>
        private void WritePlayProbeGroundItems(PlayProbeJsonWriter w, Scene scene)
        {
            w.BeginObject();
            var type = PlayProbeFindType("ItemAgent");
            if (type == null)
            {
                w.Prop("found", false);
                w.Prop("reason", "ItemAgent type not found");
                w.EndObject();
                return;
            }

            w.Prop("found", true);
            w.Prop("scene", scene.name);
            var components = PlayProbeSceneComponents(type, scene);
            w.Prop("componentCount", components.Count);

            w.Name("items");
            w.BeginArray();
            var seenItems = new HashSet<int>();
            var written = 0;
            foreach (var component in components)
            {
                if (component == null || written >= PlayProbeMaxInstancesPerType)
                    continue;

                var item = PlayProbeGetMember(component, "Item");
                if (item == null)
                    continue;
                var itemInstance = item as UnityEngine.Object;
                if (itemInstance != null && !seenItems.Add(itemInstance.GetInstanceID()))
                    continue;

                var fromInfo = PlayProbeGetMember(item, "FromInfoKey") as string;
                if (!string.Equals(fromInfo, "Ground", StringComparison.OrdinalIgnoreCase))
                    continue;

                // A held/container item can retain FromInfoKey=Ground after pickup; only
                // report items that are still free in the scene.
                if (PlayProbeGetMember(item, "InInventory") != null ||
                    PlayProbeGetMember(item, "ParentItem") != null)
                    continue;
                if (component.GetComponentInParent<CharacterMainControl>() != null)
                    continue;

                int typeId = 0;
                int stackCount = 0;
                int quality = 0;
                try { typeId = Convert.ToInt32(PlayProbeGetMember(item, "TypeID")); } catch { }
                try { stackCount = Convert.ToInt32(PlayProbeGetMember(item, "StackCount")); } catch { }
                try { quality = Convert.ToInt32(PlayProbeGetMember(item, "Quality")); } catch { }

                w.BeginObject();
                w.Prop("name", component.name);
                w.Prop("path", PlayProbePath(component.transform));
                w.Prop("scene", component.gameObject.scene.name);
                w.Prop("pos", PlayProbeVector(component.transform.position));
                w.Prop("yaw", PlayProbeRound(component.transform.eulerAngles.y));
                w.Prop("itemTypeId", typeId);
                w.Prop("stackCount", stackCount);
                w.Prop("quality", quality);
                w.Prop("fromInfoKey", fromInfo ?? string.Empty);
                w.Prop("agentType", PlayProbeDescribeValue(PlayProbeGetMember(component, "AgentType")));
                w.Prop("active", component.gameObject.activeInHierarchy);
                w.EndObject();
                written++;
            }
            w.EndArray();
            w.Prop("count", written);
            w.EndObject();
        }

        private void WritePlayProbeSpawnerPresets(PlayProbeJsonWriter w, RandomCharacterSpawner spawner)
        {
            w.Name("presetInfos");
            w.BeginArray();
            var infos = PlayProbeGetMember(spawner, "randomPresetInfos") as IEnumerable;
            if (infos == null)
            {
                w.EndArray();
                return;
            }

            var index = 0;
            foreach (var info in infos)
            {
                if (info == null)
                    continue;
                if (index >= PlayProbeMaxArrayItems)
                {
                    w.BeginObject();
                    w.Prop("truncated", true);
                    w.EndObject();
                    break;
                }

                w.BeginObject();
                w.Prop("index", index);
                w.Prop("infoType", info.GetType().FullName);
                w.Name("infoFields");
                WritePlayProbeFields(w, info, 1);

                var preset = PlayProbeGetMember(info, "randomPreset");
                w.Name("preset");
                WritePlayProbePreset(w, preset);
                w.EndObject();
                index++;
            }
            w.EndArray();
        }

        private void WritePlayProbePreset(PlayProbeJsonWriter w, object preset)
        {
            if (preset == null)
            {
                w.Null();
                return;
            }

            w.BeginObject();
            w.Prop("type", preset.GetType().FullName);
            if (preset is UnityEngine.Object unityObject)
                w.Prop("assetName", unityObject.name);
            foreach (var member in new[]
                     {
                         "nameKey", "health", "team", "isBoss", "exp",
                         "setActiveByPlayerDistance", "forceTracePlayerDistance",
                     })
            {
                var value = PlayProbeGetMember(preset, member);
                w.Name(member);
                WritePlayProbeValue(w, value, 0);
            }

            w.Name("fields");
            WritePlayProbeFields(w, preset, 1);
            w.EndObject();
        }

        private void WritePlayProbeSpawnerRoot(PlayProbeJsonWriter w, Component spawner)
        {
            var root = PlayProbeGetMember(spawner, "spawnerRoot");
            w.Name("spawnerRoot");
            if (root == null)
            {
                w.Null();
                return;
            }

            w.BeginObject();
            if (root is UnityEngine.Object unityObject)
            {
                w.Prop("name", unityObject.name);
                w.Prop("type", root.GetType().FullName);
                if (root is Component component)
                {
                    w.Prop("path", PlayProbePath(component.transform));
                    w.Prop("pos", PlayProbeVector(component.transform.position));
                }
            }
            w.Name("fields");
            WritePlayProbeFields(w, root, 1);
            if (root is Component rootComponent)
                WritePlayProbeTransformChildren(w, rootComponent.transform, PlayProbeMaxChildren);
            w.EndObject();
        }

        private void WritePlayProbeKeywordObjects(PlayProbeJsonWriter w, Scene scene, string section, params string[] keywords)
        {
            w.BeginArray();
            var types = PlayProbeFindTypesByKeywords(keywords);
            foreach (var type in types)
            {
                var components = PlayProbeSceneComponents(type, scene);
                if (components.Count == 0)
                    continue;

                w.BeginObject();
                w.Prop("type", type.FullName);
                w.Prop("count", components.Count);
                w.Name("instances");
                w.BeginArray();
                var written = 0;
                foreach (var component in components)
                {
                    if (written++ >= PlayProbeMaxInstancesPerType)
                        break;
                    w.BeginObject();
                    WritePlayProbeComponentHeader(w, component);
                    w.Name("fields");
                    WritePlayProbeFields(w, component, PlayProbeFieldDepth(component));
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
        }

        private static int PlayProbeFieldDepth(Component component)
        {
            var name = component.GetType().Name;
            if (name.IndexOf("LootBoxLoader", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("LootSpawner", StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;
            return 1;
        }

        private void WritePlayProbeNpcs(PlayProbeJsonWriter w, Scene scene)
        {
            w.BeginArray();
            var seen = new HashSet<int>();

            var characters = PlayProbeSceneComponents(typeof(CharacterMainControl), scene);
            w.BeginObject();
            w.Prop("type", "CharacterMainControl");
            w.Prop("count", characters.Count);
            w.Name("instances");
            w.BeginArray();
            var count = 0;
            foreach (var character in characters)
            {
                if (count++ >= PlayProbeMaxInstancesPerType)
                    break;
                if (!seen.Add(character.GetInstanceID()))
                    continue;

                w.BeginObject();
                w.Prop("name", character.name);
                w.Prop("path", PlayProbePath(character.transform));
                w.Prop("pos", PlayProbeVector(character.transform.position));
                w.Prop("active", character.gameObject.activeInHierarchy);
                w.Prop("team", PlayProbeDescribeValue(PlayProbeGetMember(character, "Team")));
                w.Prop("hasAi", character.GetComponentInChildren<AICharacterController>(true) != null);
                var preset = PlayProbeGetMember(character, "characterPreset");
                w.Name("preset");
                WritePlayProbePreset(w, preset);

                w.Name("components");
                w.BeginArray();
                foreach (var behaviour in character.GetComponents<MonoBehaviour>())
                {
                    if (behaviour == null)
                        continue;
                    var typeName = behaviour.GetType().Name;
                    if (typeName.IndexOf("Merchant", StringComparison.OrdinalIgnoreCase) < 0 &&
                        typeName.IndexOf("Trader", StringComparison.OrdinalIgnoreCase) < 0 &&
                        typeName.IndexOf("Vendor", StringComparison.OrdinalIgnoreCase) < 0 &&
                        typeName.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) < 0 &&
                        typeName.IndexOf("Dialogue", StringComparison.OrdinalIgnoreCase) < 0 &&
                        typeName.IndexOf("NPC", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    w.BeginObject();
                    w.Prop("type", behaviour.GetType().FullName);
                    w.Prop("enabled", behaviour.enabled);
                    w.Name("fields");
                    WritePlayProbeFields(w, behaviour, 1);
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();

            var types = PlayProbeFindTypesByKeywords("merchant", "trader", "vendor", "quest", "dialogue", "npc");
            foreach (var type in types)
            {
                var components = PlayProbeSceneComponents(type, scene);
                if (components.Count == 0)
                    continue;
                w.BeginObject();
                w.Prop("type", type.FullName);
                w.Prop("count", components.Count);
                w.Name("instances");
                w.BeginArray();
                var written = 0;
                foreach (var component in components)
                {
                    if (written++ >= PlayProbeMaxInstancesPerType)
                        break;
                    if (!seen.Add(component.GetInstanceID()))
                        continue;
                    w.BeginObject();
                    WritePlayProbeComponentHeader(w, component);
                    w.Name("fields");
                    WritePlayProbeFields(w, component, PlayProbeFieldDepth(component));
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
        }

        private void WritePlayProbeActivation(PlayProbeJsonWriter w, Scene scene)
        {
            w.BeginObject();

            var ais = PlayProbeSceneComponents(typeof(AICharacterController), scene);
            w.Prop("aiCount", ais.Count);
            w.Name("ai");
            w.BeginArray();
            var index = 0;
            foreach (var ai in ais)
            {
                if (index++ >= PlayProbeMaxInstancesPerType)
                    break;
                w.BeginObject();
                w.Prop("name", ai.name);
                w.Prop("path", PlayProbePath(ai.transform));
                w.Prop("pos", PlayProbeVector(ai.transform.position));
                w.Prop("active", ai.gameObject.activeInHierarchy);
                w.Prop("enabled", ai is Behaviour aiBehaviour && aiBehaviour.enabled);
                foreach (var member in new[]
                         {
                             "alert", "noticed", "sightDistance", "forceTracePlayerDistance",
                             "focusDistance", "searchedEnemy", "aimTarget",
                         })
                {
                    w.Name(member);
                    WritePlayProbeValue(w, PlayProbeGetMember(ai, member), 0);
                }
                var character = ai.GetComponentInParent<CharacterMainControl>() ??
                                ai.GetComponent<CharacterMainControl>();
                w.Name("preset");
                WritePlayProbePreset(w, character != null
                    ? PlayProbeGetMember(character, "characterPreset")
                    : null);

                w.Name("activationComponents");
                w.BeginArray();
                foreach (var behaviour in ai.GetComponents<MonoBehaviour>())
                {
                    if (behaviour == null)
                        continue;
                    var typeName = behaviour.GetType().Name;
                    if (typeName.IndexOf("SetActiveByPlayerDistance", StringComparison.OrdinalIgnoreCase) < 0 &&
                        typeName.IndexOf("Activation", StringComparison.OrdinalIgnoreCase) < 0 &&
                        typeName.IndexOf("PlayerDistance", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    w.BeginObject();
                    w.Prop("type", behaviour.GetType().FullName);
                    w.Prop("enabled", behaviour.enabled);
                    w.Name("fields");
                    WritePlayProbeFields(w, behaviour, 1);
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();

            var activationTypes = PlayProbeFindTypesByKeywords("SetActiveByPlayerDistance", "Activation", "PlayerDistance");
            w.Name("activationTypes");
            w.BeginArray();
            foreach (var type in activationTypes)
            {
                var components = PlayProbeSceneComponents(type, scene);
                if (components.Count == 0)
                    continue;
                w.BeginObject();
                w.Prop("type", type.FullName);
                w.Prop("count", components.Count);
                w.Name("instances");
                w.BeginArray();
                var written = 0;
                foreach (var component in components)
                {
                    if (written++ >= PlayProbeMaxInstancesPerType)
                        break;
                    w.BeginObject();
                    WritePlayProbeComponentHeader(w, component);
                    w.Name("fields");
                    WritePlayProbeFields(w, component, PlayProbeFieldDepth(component));
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        private static Type _playProbeNavMeshType;
        private static MethodInfo _playProbeNavMeshSampleMethod;

        private void WritePlayProbeMapVariantTargets(PlayProbeJsonWriter w, Scene scene)
        {
            var keywords = new[]
            {
                "smallbridge", "breakablewall", "bunkerdoor", "manhole", "sewer", "pipe",
                "shortcut", "exit", "pet", "merchant", "mud", "jeff", "basket", "teleport",
            };

            w.BeginArray();
            var seen = new HashSet<int>();
            var written = 0;
            foreach (var behaviour in PlayProbeSceneBehaviours(scene))
            {
                if (behaviour == null)
                    continue;
                var path = PlayProbePath(behaviour.transform);
                var text = (path + " " + behaviour.name).ToLowerInvariant();
                var matched = false;
                foreach (var keyword in keywords)
                {
                    if (text.IndexOf(keyword, StringComparison.Ordinal) < 0)
                        continue;
                    matched = true;
                    break;
                }
                if (!matched)
                    continue;
                if (!seen.Add(behaviour.GetInstanceID()))
                    continue;
                if (written++ >= 300)
                    break;

                w.BeginObject();
                WritePlayProbeComponentHeader(w, behaviour);
                w.Name("fields");
                WritePlayProbeFields(w, behaviour, 2);
                WritePlayProbeTransformChildren(w, behaviour.transform, 64);
                w.Name("methods");
                WritePlayProbeMethods(w, behaviour.GetType());
                w.Name("navMesh");
                WritePlayProbeNavMeshSamples(w, behaviour.transform.position);
                w.EndObject();
            }
            w.EndArray();
        }

        private void WritePlayProbeMethods(PlayProbeJsonWriter w, Type type)
        {
            w.BeginArray();
            var written = 0;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                    continue;
                if (written++ >= 80)
                    break;
                w.BeginObject();
                w.Prop("name", method.Name);
                w.Prop("returns", method.ReturnType.Name);
                w.Name("parameters");
                w.BeginArray();
                foreach (var parameter in method.GetParameters())
                {
                    w.BeginObject();
                    w.Prop("name", parameter.Name);
                    w.Prop("type", parameter.ParameterType.Name);
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
        }

        private void WritePlayProbeNavMeshSamples(PlayProbeJsonWriter w, Vector3 position)
        {
            PlayProbeEnsureNavMesh();
            w.BeginObject();
            w.Prop("typeFound", _playProbeNavMeshType != null);
            w.Prop("sampleMethodFound", _playProbeNavMeshSampleMethod != null);
            w.Name("samples");
            w.BeginArray();
            var offsets = new[]
            {
                Vector3.zero,
                new Vector3(2f, 0f, 0f),
                new Vector3(-2f, 0f, 0f),
                new Vector3(0f, 0f, 2f),
                new Vector3(0f, 0f, -2f),
                new Vector3(0f, 1f, 0f),
            };
            foreach (var offset in offsets)
            {
                var source = position + offset;
                var hit = source;
                var distance = -1f;
                var ok = PlayProbeTrySampleNavMesh(source, out hit, out distance);
                w.BeginObject();
                w.Prop("offset", PlayProbeVector(offset));
                w.Prop("hit", ok);
                w.Prop("position", PlayProbeVector(hit));
                w.Prop("distance", distance);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        private void WritePlayProbeNavMeshAnchors(PlayProbeJsonWriter w)
        {
            w.BeginArray();
            var main = CharacterMainControl.Main;
            if (main != null)
            {
                w.BeginObject();
                w.Prop("label", "localPlayer");
                w.Prop("path", PlayProbePath(main.transform));
                w.Prop("pos", PlayProbeVector(main.transform.position));
                w.Name("navMesh");
                WritePlayProbeNavMeshSamples(w, main.transform.position);
                w.EndObject();
            }
            w.EndArray();
        }

        private static void PlayProbeEnsureNavMesh()
        {
            if (_playProbeNavMeshType == null)
                _playProbeNavMeshType = Type.GetType("UnityEngine.AI.NavMesh, UnityEngine.AIModule");
            if (_playProbeNavMeshType == null || _playProbeNavMeshSampleMethod != null)
                return;

            foreach (var method in _playProbeNavMeshType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "SamplePosition")
                    continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 4 &&
                    parameters[0].ParameterType == typeof(Vector3) &&
                    parameters[1].ParameterType.IsByRef &&
                    parameters[2].ParameterType == typeof(float) &&
                    parameters[3].ParameterType == typeof(int))
                {
                    _playProbeNavMeshSampleMethod = method;
                    break;
                }
            }
        }

        private static bool PlayProbeTrySampleNavMesh(Vector3 source, out Vector3 hit, out float distance)
        {
            hit = source;
            distance = -1f;
            try
            {
                PlayProbeEnsureNavMesh();
                if (_playProbeNavMeshSampleMethod == null)
                    return false;

                var hitType = _playProbeNavMeshSampleMethod.GetParameters()[1].ParameterType.GetElementType();
                if (hitType == null)
                    return false;

                object hitBox = Activator.CreateInstance(hitType);
                object[] args = { source, hitBox, 2f, -1 };
                var ok = (bool)_playProbeNavMeshSampleMethod.Invoke(null, args);
                if (!ok)
                    return false;

                hitBox = args[1];
                var positionMember = (MemberInfo)hitType.GetProperty("position") ??
                                     hitType.GetField("m_Position");
                var distanceMember = (MemberInfo)hitType.GetProperty("distance") ??
                                     hitType.GetField("m_Distance");
                if (positionMember is PropertyInfo positionProperty)
                    hit = (Vector3)positionProperty.GetValue(hitBox);
                else if (positionMember is FieldInfo positionField)
                    hit = (Vector3)positionField.GetValue(hitBox);

                if (distanceMember is PropertyInfo distanceProperty)
                    distance = Convert.ToSingle(distanceProperty.GetValue(hitBox));
                else if (distanceMember is FieldInfo distanceField)
                    distance = Convert.ToSingle(distanceField.GetValue(hitBox));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void WritePlayProbeKeywordTypes(PlayProbeJsonWriter w, Scene scene)
        {
            var examples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var behaviour in PlayProbeSceneBehaviours(scene))
            {
                if (behaviour == null)
                    continue;
                var type = behaviour.GetType();
                if (!PlayProbeMatchesKeyword(type, PlayProbeDiscoveryKeywords))
                    continue;
                var typeName = type.FullName ?? type.Name;
                if (!examples.TryGetValue(typeName, out var paths))
                {
                    paths = new List<string>();
                    examples[typeName] = paths;
                }
                if (paths.Count < 3)
                    paths.Add(PlayProbePath(behaviour.transform));
            }

            w.BeginArray();
            foreach (var pair in examples.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                w.BeginObject();
                w.Prop("type", pair.Key);
                w.Prop("examples", pair.Value.Count);
                w.Name("paths");
                w.BeginArray();
                foreach (var path in pair.Value)
                    w.String(path);
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
        }

        // ---------- component helpers ----------

        private void WritePlayProbeComponentHeader(PlayProbeJsonWriter w, Component component)
        {
            w.Prop("type", component.GetType().FullName);
            w.Prop("name", component.name);
            w.Prop("path", PlayProbePath(component.transform));
            var scene = component.gameObject.scene;
            w.Prop("scene", scene.IsValid() ? scene.name : string.Empty);
            w.Prop("scenePath", scene.IsValid() ? scene.path : string.Empty);
            w.Prop("active", component.gameObject.activeInHierarchy);
            if (component is Behaviour behaviour)
                w.Prop("enabled", behaviour.enabled);
            w.Prop("layer", component.gameObject.layer);
            w.Prop("tag", component.gameObject.tag);
            w.Prop("pos", PlayProbeVector(component.transform.position));
            w.Prop("yaw", PlayProbeRound(component.transform.eulerAngles.y));
            w.Prop("parentPath", component.transform.parent != null
                ? PlayProbePath(component.transform.parent)
                : string.Empty);
        }

        private void WritePlayProbeTransformChildren(PlayProbeJsonWriter w, Transform parent, int max)
        {
            w.Name("children");
            w.BeginArray();
            if (parent == null)
            {
                w.EndArray();
                return;
            }

            var count = 0;
            foreach (Transform child in parent)
            {
                if (child == null)
                    continue;
                if (count++ >= max)
                {
                    w.BeginObject();
                    w.Prop("truncated", true);
                    w.EndObject();
                    break;
                }
                w.BeginObject();
                w.Prop("name", child.name);
                w.Prop("path", PlayProbePath(child));
                w.Prop("pos", PlayProbeVector(child.position));
                w.Prop("yaw", PlayProbeRound(child.eulerAngles.y));
                w.Prop("active", child.gameObject.activeInHierarchy);
                w.EndObject();
            }
            w.EndArray();
        }

        private void WritePlayProbeFields(PlayProbeJsonWriter w, object target, int depth)
        {
            w.BeginObject();
            if (target == null)
            {
                w.EndObject();
                return;
            }

            if (target is UnityEngine.Object destroyedTarget && destroyedTarget == null)
            {
                w.Null();
                w.EndObject();
                return;
            }

            if (_playProbeSeen != null && !_playProbeSeen.Add(target))
            {
                w.Prop("cycle", true);
                w.EndObject();
                return;
            }

            var written = 0;
            var type = target.GetType();
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                var ns = current.Namespace ?? string.Empty;
                if (ns.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                    ns.StartsWith("Unity.", StringComparison.Ordinal) ||
                    ns.StartsWith("System", StringComparison.Ordinal))
                    break;

                FieldInfo[] fields;
                try
                {
                    fields = current.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                               BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }

                foreach (var field in fields)
                {
                    if (field.IsStatic)
                        continue;

                    object value;
                    try
                    {
                        value = field.GetValue(target);
                    }
                    catch
                    {
                        continue;
                    }

                    if (value == null)
                        continue;

                    if (written++ >= PlayProbeMaxFields)
                    {
                        w.Prop("__truncated", true);
                        break;
                    }

                    w.Name(field.Name);
                    WritePlayProbeValue(w, value, depth);
                }
            }

            if (_playProbeSeen != null)
                _playProbeSeen.Remove(target);
            w.EndObject();
        }

        private void WritePlayProbeEnumerable(PlayProbeJsonWriter w, IEnumerable enumerable, int depth)
        {
            if (enumerable != null && PlayProbeIsNativeCollection(enumerable.GetType()))
            {
                w.String("<native-collection:" + enumerable.GetType().Name + ">");
                return;
            }

            w.BeginArray();
            var count = 0;
            try
            {
                foreach (var item in enumerable)
                {
                    if (count >= PlayProbeMaxArrayItems)
                    {
                        w.BeginObject();
                        w.Prop("truncated", true);
                        w.EndObject();
                        break;
                    }
                    WritePlayProbeValue(w, item, depth);
                    count++;
                }
            }
            catch (Exception ex)
            {
                w.BeginObject();
                w.Prop("error", ex.GetType().Name + ": " + ex.Message);
                w.EndObject();
            }
            w.EndArray();
        }

        private static bool PlayProbeIsNativeCollection(Type type)
        {
            var ns = type.Namespace ?? string.Empty;
            return ns.StartsWith("Unity.Collections", StringComparison.Ordinal);
        }

        private void WritePlayProbeValue(PlayProbeJsonWriter w, object value, int depth)
        {
            if (value == null)
            {
                w.Null();
                return;
            }

            // Unity objects override == null when destroyed. A plain `value == null` on
            // object does NOT use that overload, so accessing .name/.transform below can
            // still throw MissingReferenceException/NullReferenceException during a probe.
            if (value is UnityEngine.Object destroyed && destroyed == null)
            {
                w.Null();
                return;
            }

            var type = value.GetType();
            if (PlayProbeIsNativeCollection(type))
            {
                w.String("<native-collection:" + type.Name + ">");
                return;
            }
            if (value is string text)
            {
                w.String(PlayProbeTruncate(text));
                return;
            }

            if (value is bool boolean)
            {
                w.Bool(boolean);
                return;
            }

            if (type.IsEnum)
            {
                w.String(value.ToString());
                return;
            }

            if (value is Vector2 vector2)
            {
                w.BeginArray();
                w.Number(vector2.x);
                w.Number(vector2.y);
                w.EndArray();
                return;
            }

            if (value is Vector3 vector3)
            {
                w.BeginArray();
                w.Number(vector3.x);
                w.Number(vector3.y);
                w.Number(vector3.z);
                w.EndArray();
                return;
            }

            if (value is Vector4 vector4)
            {
                w.BeginArray();
                w.Number(vector4.x);
                w.Number(vector4.y);
                w.Number(vector4.z);
                w.Number(vector4.w);
                w.EndArray();
                return;
            }

            if (value is Quaternion quaternion)
            {
                w.BeginArray();
                w.Number(quaternion.x);
                w.Number(quaternion.y);
                w.Number(quaternion.z);
                w.Number(quaternion.w);
                w.EndArray();
                return;
            }

            if (value is Color color)
            {
                w.BeginArray();
                w.Number(color.r);
                w.Number(color.g);
                w.Number(color.b);
                w.Number(color.a);
                w.EndArray();
                return;
            }

            if (value is Transform transform)
            {
                w.BeginObject();
                w.Prop("name", transform.name);
                w.Prop("path", PlayProbePath(transform));
                w.Prop("pos", PlayProbeVector(transform.position));
                w.Prop("yaw", PlayProbeRound(transform.eulerAngles.y));
                w.EndObject();
                return;
            }

            if (value is GameObject gameObject)
            {
                w.BeginObject();
                w.Prop("name", gameObject.name);
                w.Prop("path", PlayProbePath(gameObject.transform));
                w.Prop("active", gameObject.activeInHierarchy);
                w.Prop("pos", PlayProbeVector(gameObject.transform.position));
                w.EndObject();
                return;
            }

            if (value is Component component)
            {
                w.BeginObject();
                w.Prop("type", component.GetType().FullName);
                w.Prop("name", component.name);
                w.Prop("path", PlayProbePath(component.transform));
                w.Prop("pos", PlayProbeVector(component.transform.position));
                w.EndObject();
                return;
            }

            if (value is UnityEngine.Object unityObject)
            {
                w.BeginObject();
                w.Prop("type", unityObject.GetType().FullName);
                w.Prop("name", unityObject.name);
                w.EndObject();
                return;
            }

            if (value is IDictionary dictionary)
            {
                w.BeginObject();
                var count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (count++ >= PlayProbeMaxArrayItems)
                    {
                        w.Prop("__truncated", true);
                        break;
                    }
                    var key = PlayProbeDescribeValue(entry.Key);
                    w.Name(key);
                    WritePlayProbeValue(w, entry.Value, depth);
                }
                w.EndObject();
                return;
            }

            if (value is IEnumerable enumerable)
            {
                WritePlayProbeEnumerable(w, enumerable, depth);
                return;
            }

            if (PlayProbeIsNumber(value))
            {
                w.Number(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                return;
            }

            if (depth > 0 && PlayProbeIsComplex(type))
            {
                w.BeginObject();
                w.Prop("type", type.FullName);
                w.Name("fields");
                WritePlayProbeFields(w, value, depth - 1);
                w.EndObject();
                return;
            }

            w.String(PlayProbeTruncate(PlayProbeDescribeValue(value)));
        }

        // ---------- scene scanning ----------

        private List<MonoBehaviour> PlayProbeSceneBehaviours(Scene scene)
        {
            if (_playProbeSceneBehaviourCache != null)
                return _playProbeSceneBehaviourCache;

            var handles = new HashSet<int>();
            foreach (var loaded in PlayProbeLoadedLevelScenes())
                handles.Add(loaded.handle);

            var result = new List<MonoBehaviour>();
            UnityEngine.Object[] objects;
            try
            {
                objects = UnityEngine.Object.FindObjectsOfType(typeof(MonoBehaviour), true);
            }
            catch
            {
                objects = Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour));
            }

            foreach (var obj in objects)
            {
                if (obj is not MonoBehaviour behaviour || behaviour == null)
                    continue;
                var gameObject = behaviour.gameObject;
                if (gameObject == null)
                    continue;
                var objectScene = gameObject.scene;
                if (!objectScene.IsValid() || !handles.Contains(objectScene.handle))
                    continue;
                result.Add(behaviour);
            }

            result.Sort((a, b) => string.CompareOrdinal(PlayProbePath(a.transform), PlayProbePath(b.transform)));
            _playProbeSceneCache = scene;
            _playProbeSceneBehaviourCache = result;
            _playProbeSceneComponentCache = result.Cast<Component>().ToList();
            return result;
        }

        private List<Component> PlayProbeSceneComponents(Type type, Scene scene)
        {
            if (type == null)
                return new List<Component>();

            if (_playProbeComponentQueryCache != null &&
                _playProbeComponentQueryCache.TryGetValue(type, out var cached))
                return cached;

            var behaviours = PlayProbeSceneBehaviours(scene);
            var all = _playProbeSceneComponentCache;
            if (all == null)
            {
                all = behaviours.Cast<Component>().ToList();
                _playProbeSceneComponentCache = all;
            }

            var result = new List<Component>();
            foreach (var component in all)
            {
                if (component == null || !type.IsInstanceOfType(component))
                    continue;
                result.Add(component);
            }

            result.Sort((a, b) => string.CompareOrdinal(PlayProbePath(a.transform), PlayProbePath(b.transform)));
            if (_playProbeComponentQueryCache != null)
                _playProbeComponentQueryCache[type] = result;
            return result;
        }

        private List<Type> PlayProbeFindTypesByKeywords(params string[] keywords)
        {
            PlayProbeEnsureTypeIndex();
            var result = new List<Type>();
            var seen = new HashSet<Type>();
            foreach (var type in _playProbeTypeIndex.Values)
            {
                if (type == null || type.IsAbstract || !typeof(Component).IsAssignableFrom(type))
                    continue;
                if (!PlayProbeMatchesKeyword(type, keywords))
                    continue;
                if (seen.Add(type))
                    result.Add(type);
            }

            result.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            return result;
        }

        private static bool PlayProbeMatchesKeyword(Type type, string[] keywords)
        {
            if (type == null || type.IsAbstract)
                return false;

            var ns = type.Namespace ?? string.Empty;
            if (ns.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                ns.StartsWith("Unity.", StringComparison.Ordinal) ||
                ns.StartsWith("TMPro", StringComparison.Ordinal) ||
                ns.StartsWith("System", StringComparison.Ordinal))
                return false;

            var name = type.Name;
            foreach (var suffix in PlayProbeUiSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            var lower = name.ToLowerInvariant();
            foreach (var keyword in keywords)
            {
                if (lower.IndexOf(keyword.ToLowerInvariant(), StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        private Type PlayProbeFindType(string name)
        {
            PlayProbeEnsureTypeIndex();
            return _playProbeTypeIndex.TryGetValue(name, out var type) ? type : null;
        }

        private void PlayProbeEnsureTypeIndex()
        {
            if (_playProbeTypeIndex != null)
                return;

            _playProbeTypeIndex = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null)
                        continue;
                    if (!_playProbeTypeIndex.ContainsKey(type.Name))
                        _playProbeTypeIndex[type.Name] = type;
                    if (!string.IsNullOrEmpty(type.FullName) && !_playProbeTypeIndex.ContainsKey(type.FullName))
                        _playProbeTypeIndex[type.FullName] = type;
                }
            }
        }

        // ---------- reflection helpers ----------

        private static object PlayProbeGetMember(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name))
                return null;

            var type = target.GetType();
            for (var current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = null;
                PropertyInfo property = null;
                try
                {
                    field = current.GetField(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);
                    if (field == null)
                    {
                        property = current.GetProperty(name,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly);
                    }
                }
                catch
                {
                    continue;
                }

                try
                {
                    if (field != null)
                        return field.GetValue(target);
                    if (property != null && property.GetIndexParameters().Length == 0)
                        return property.GetValue(target);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static bool PlayProbeIsNumber(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort ||
                   value is int || value is uint || value is long || value is ulong ||
                   value is float || value is double || value is decimal;
        }

        private static bool PlayProbeIsComplex(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
                type == typeof(DateTime) || type == typeof(Guid) || type == typeof(TimeSpan))
                return false;
            var ns = type.Namespace ?? string.Empty;
            return !ns.StartsWith("UnityEngine", StringComparison.Ordinal) &&
                   !ns.StartsWith("Unity.", StringComparison.Ordinal) &&
                   !ns.StartsWith("System", StringComparison.Ordinal);
        }

        private static string PlayProbeDescribeValue(object value)
        {
            if (value == null)
                return "<null>";
            if (value is UnityEngine.Object unityObject)
                return $"{unityObject.GetType().Name} '{unityObject.name}'";
            return PlayProbeTruncate(value.ToString());
        }

        private static string PlayProbeTruncate(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return text.Length <= PlayProbeMaxString ? text : text.Substring(0, PlayProbeMaxString) + "...";
        }

        private static string PlayProbePath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            var builder = new StringBuilder(transform.name);
            var current = transform.parent;
            while (current != null)
            {
                builder.Insert(0, current.name + "/");
                current = current.parent;
            }
            return builder.ToString();
        }

        private static string PlayProbeSafeName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "unknown";

            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                builder.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }
            return builder.ToString();
        }

        private static string PlayProbeVector(Vector3 value)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}",
                value.x, value.y, value.z);
        }

        private static float PlayProbeRound(float value)
        {
            return (float)Math.Round(value, 3);
        }

        private sealed class PlayProbeReferenceComparer : IEqualityComparer<object>
        {
            public static readonly PlayProbeReferenceComparer Instance = new PlayProbeReferenceComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        // ---------- tiny JSON writer ----------

        private sealed class PlayProbeJsonWriter
        {
            private readonly StringBuilder _builder = new StringBuilder(1 << 20);
            private readonly Stack<ContainerState> _stack = new Stack<ContainerState>();
            private int _indent;
            private bool _afterName;

            private sealed class ContainerState
            {
                public bool First = true;
            }

            public int Length => _builder.Length;

            public override string ToString()
            {
                return _builder.ToString();
            }

            public void BeginObject()
            {
                BeginValue();
                _builder.Append('{');
                _stack.Push(new ContainerState());
                _indent++;
            }

            public void EndObject()
            {
                var state = _stack.Pop();
                _indent--;
                if (!state.First)
                {
                    _builder.Append('\n');
                    AppendIndent();
                }
                _builder.Append('}');
            }

            public void BeginArray()
            {
                BeginValue();
                _builder.Append('[');
                _stack.Push(new ContainerState());
                _indent++;
            }

            public void EndArray()
            {
                var state = _stack.Pop();
                _indent--;
                if (!state.First)
                {
                    _builder.Append('\n');
                    AppendIndent();
                }
                _builder.Append(']');
            }

            public void Name(string name)
            {
                Separate();
                AppendQuoted(name);
                _builder.Append(": ");
                _afterName = true;
            }

            public void Null()
            {
                BeginValue();
                _builder.Append("null");
            }

            public void Bool(bool value)
            {
                BeginValue();
                _builder.Append(value ? "true" : "false");
            }

            public void Number(double value)
            {
                BeginValue();
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    _builder.Append("null");
                    return;
                }
                _builder.Append(value.ToString("0.######", CultureInfo.InvariantCulture));
            }

            public void String(string value)
            {
                BeginValue();
                if (value == null)
                    _builder.Append("null");
                else
                    AppendQuoted(value);
            }

            public void Prop(string name, string value)
            {
                Name(name);
                String(value);
            }

            public void Prop(string name, bool value)
            {
                Name(name);
                Bool(value);
            }

            public void Prop(string name, int value)
            {
                Name(name);
                Number(value);
            }

            public void Prop(string name, long value)
            {
                Name(name);
                Number(value);
            }

            public void Prop(string name, float value)
            {
                Name(name);
                Number(value);
            }

            private void BeginValue()
            {
                if (!_afterName)
                    Separate();
                _afterName = false;
            }

            private void Separate()
            {
                if (_stack.Count == 0)
                    return;

                var state = _stack.Peek();
                if (!state.First)
                    _builder.Append(',');
                state.First = false;
                _builder.Append('\n');
                AppendIndent();
            }

            private void AppendIndent()
            {
                _builder.Append(' ', _indent * 2);
            }

            private void AppendQuoted(string value)
            {
                _builder.Append('"');
                foreach (var c in value)
                {
                    switch (c)
                    {
                        case '"': _builder.Append("\\\""); break;
                        case '\\': _builder.Append("\\\\"); break;
                        case '\b': _builder.Append("\\b"); break;
                        case '\f': _builder.Append("\\f"); break;
                        case '\n': _builder.Append("\\n"); break;
                        case '\r': _builder.Append("\\r"); break;
                        case '\t': _builder.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                                _builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                _builder.Append(c);
                            break;
                    }
                }
                _builder.Append('"');
            }
        }
    }
}
