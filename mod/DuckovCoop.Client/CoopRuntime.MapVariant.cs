using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    /// <summary>
    /// PLAY-owned GroundZero runtime scene variant spike.
    ///
    /// M2 scope: GroundZero only, no new Unity Scene asset. This component disables the
    /// scene objects that must not exist in the coop variant:
    ///   - functional NPCs / quest giver / dialogue actors / merchant / pet
    ///   - map transitions and fast-travel / shortcut objects
    ///   - structural breakable walls and repair bridges
    /// It keeps exactly the existing Exit(Clone) extraction point.
    ///
    /// It also writes NavMesh.SamplePosition results around each removed obstacle so we
    /// can tell whether the baked NavMesh actually opens; destroying a wall does not
    /// automatically open a baked NavMesh.
    ///
    /// Disabled by default. Enable with:
    ///   [debug] mapVariant = 1   in coop.ini
    /// or a file named groundzero_variant.on next to the mod DLL.
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private const string MapVariantLongBridgeRootPath =
            "LevelElements/GroundZero_01_SpecialElement/Bridge";

        private const string MapVariantConfigSection = "debug";
        private const string MapVariantConfigKey = "mapvariant";
        private const string MapVariantSentinelName = "groundzero_variant.on";

        private bool _mapVariantConfigLoaded;
        private bool _mapVariantEnabled = true; // coop base config: GroundZero variant is on by default
        private bool _mapVariantRunning;
        private string _mapVariantSignature = string.Empty;
        private int _mapVariantPass;
        private float _mapVariantFirstAt;
        private float _mapVariantSecondAt;
        private readonly HashSet<int> _mapVariantRemoved = new HashSet<int>();
        private readonly HashSet<string> _mapVariantLoggedBridges = new HashSet<string>(StringComparer.Ordinal);
        private static readonly string[] MapVariantBridgeRootPaths =
        {
            "LevelElements/GroundZero_01_SpecialElement/SmallBridgeNeedFix",
            "LevelElements/GroundZero_01_SpecialElement/SmallBridgeNeedFix_1",
            // The actual long bridge that showed "交付 (0/3)(0/2)"; it is a separate
            // ConstructionSite, 100m+ away from the two SmallBridgeNeedFix roots.
            "LevelElements/GroundZero_01_SpecialElement/Bridge",
        };

        private readonly HashSet<int> _mapVariantBridgeCostItemIds = new HashSet<int>();
        private readonly HashSet<string> _mapVariantBridgeCostSignatures = new HashSet<string>(StringComparer.Ordinal);
        private bool _mapVariantBridgeCostsCaptured;
        private bool _mapVariantBridgeAuditDone;
        private readonly Dictionary<string, Type> _mapVariantTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static Type _mapVariantNavMeshType;
        private static MethodInfo _mapVariantNavMeshSample;

        // 2026-11-15: the variant pass used to issue ~23 independent
        // Object.FindObjectsOfType calls against the fully loaded GroundZero hierarchy.
        // Each one walks every scene object, so a pass blocked the main thread for
        // 4-6s (measured frameMs 4199/4483 on host, 5643/6218 on client). One
        // Component snapshot per pass replaces all of them.
        private static UnityEngine.Component[] _mapVariantComponentCache;

        private static void MapVariantBeginPass()
        {
            _mapVariantComponentCache = UnityEngine.Object.FindObjectsOfType<UnityEngine.Component>(true);
        }

        private static void MapVariantEndPass()
        {
            _mapVariantComponentCache = null;
        }

        private static UnityEngine.Component[] MapVariantComponentCache()
        {
            if (_mapVariantComponentCache == null)
                _mapVariantComponentCache = UnityEngine.Object.FindObjectsOfType<UnityEngine.Component>(true);
            return _mapVariantComponentCache;
        }

        private static IEnumerable<UnityEngine.Object> MapVariantFind(Type type)
        {
            if (type == null)
                yield break;
            var cache = MapVariantComponentCache();
            for (var i = 0; i < cache.Length; i++)
            {
                var component = cache[i];
                if (component != null && type.IsInstanceOfType(component))
                    yield return component;
            }
        }

        private static IEnumerable<Transform> MapVariantTransforms()
        {
            var cache = MapVariantComponentCache();
            for (var i = 0; i < cache.Length; i++)
            {
                if (cache[i] is Transform transform && transform != null)
                    yield return transform;
            }
        }

        private void UpdateMapVariant()
        {
            if (!_mapVariantConfigLoaded)
                LoadMapVariantConfig();

            if (!_mapVariantEnabled || _mapVariantRunning)
                return;

            var scenes = MapVariantLoadedGroundZeroScenes();
            if (scenes.Count == 0)
                return;

            var signature = string.Join("|", scenes
                .Select(s => s.name)
                .OrderBy(s => s, StringComparer.Ordinal));

            if (!string.Equals(_mapVariantSignature, signature, StringComparison.Ordinal))
            {
                _mapVariantSignature = signature;
                _mapVariantPass = 0;
                _mapVariantFirstAt = Time.unscaledTime + 1.5f;
                _mapVariantSecondAt = Time.unscaledTime + 5f;
                _mapVariantRemoved.Clear();
                _mapVariantLoggedBridges.Clear();
                // Map costs are static for GroundZero; keep them if a later subscene
                // adds another signature (the bridge components are already destroyed
                // by then and cannot be read again). Only the audit itself re-arms.
                _mapVariantBridgeAuditDone = false;
                Log($"[mapvariant] armed scenes={signature} pass1=+1.5s pass2=+5s pass3=+20s");
            }

            if (_mapVariantPass == 0 && Time.unscaledTime >= _mapVariantFirstAt)
            {
                _mapVariantPass = 1;
                ApplyGroundZeroVariant();
            }
            else if (_mapVariantPass == 1 && Time.unscaledTime >= _mapVariantSecondAt)
            {
                _mapVariantPass = 2;
                ApplyGroundZeroVariant();
                Log("[mapvariant] middle/done pass complete (no +20s rescan)");
            }
        }

        private void LoadMapVariantConfig()
        {
            _mapVariantConfigLoaded = true;
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
                        if (section == MapVariantConfigSection && key == MapVariantConfigKey)
                        {
                            _mapVariantEnabled = value == "1" ||
                                                 value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                                 value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }

                if (File.Exists(Path.Combine(directory, MapVariantSentinelName)))
                    _mapVariantEnabled = true;
            }
            catch (Exception ex)
            {
                Log("[mapvariant] config error: " + ex.GetType().Name + ": " + ex.Message);
            }

            Log($"[mapvariant] enabled={_mapVariantEnabled}");
        }

        private static List<Scene> MapVariantLoadedGroundZeroScenes()
        {
            var result = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                if (scene.name.StartsWith("Level_GroundZero", StringComparison.Ordinal) ||
                    (scene.path ?? string.Empty).IndexOf("Level_GroundZero", StringComparison.Ordinal) >= 0)
                    result.Add(scene);
            }
            return result;
        }

        private void ApplyGroundZeroVariant()
        {
            _mapVariantRunning = true;
            MapVariantBeginPass();
            try
            {
                Log("[mapvariant] apply GroundZero variant");

                // 1. Functional NPCs / quest / dialogue / merchant / pet.
                MapVariantDisablePathPrefix("Duckov.Quests.QuestGiver",
                    "LevelElements/GroundZero_01_SpecialElement/Coach/Character_Mud", "quest giver Mud");
                MapVariantDisablePathPrefix("DuckovDialogueActor",
                    "LevelElements/GroundZero_01_SpecialElement/Coach/Character_Mud", "dialogue Mud");
                MapVariantDisablePathPrefix("DuckovDialogueActor",
                    "CutScenes/GroundZero_Intro/Jeff", "dialogue Jeff");
                MapVariantDisablePathPrefix("KunEvents",
                    "Scene/BasketBall/Basket_Pfb", "Kun basketball NPC");
                MapVariantDisableCharacterPreset("Merchant", "merchant Myst0");
                MapVariantDisableCharacterPreset("Pet", "pet");
                MapVariantDisablePetByMainScenePath();

                // 2. Map transitions / fast travel / shortcuts: disable only the
                // interaction component so the model stays in the scene.
                MapVariantDisableAllComponentsOfType("Duckov.Scenes.MultiSceneTeleporter", "map transition");
                MapVariantDisableAllComponentsOfType("SimpleTeleporter", "shortcut");
                MapVariantDisableAllComponentsOfType("TeleportBeacon", "quick travel");
                MapVariantDisableComponentOnPath("Duckov.ItemShortcut",
                    "LevelConfig/LevelManager(Clone)/ItemShortcutManager", "ItemShortcutManager");

                // Cave / storm / zipline: the teleporter component is already off above;
                // also remove the prompt and the card-reader/paid interaction so the
                // player never sees a dead-end interaction.
                MapVariantDisableInteractionsUnder("Teleporters/DoorToCave", "cave entrance");
                MapVariantHideInteractionObjectsUnder("Teleporters/DoorToCave", "cave entrance");
                MapVariantDisableInteractionsUnder("Teleporters/ToHiddenWarehouse", "warehouse entrance");
                MapVariantHideInteractionObjectsUnder("Teleporters/ToHiddenWarehouse", "warehouse entrance");
                MapVariantDisableInteractionsUnder("ToStormZone", "storm entrance");
                MapVariantHideInteractionObjectsUnder("ToStormZone", "storm entrance");
                MapVariantDisableSceneObject("ToStormZone", "storm entrance");
                MapVariantDisableInteractionsByKeywords("zipline", "warehouse", "accesscard", "cardreader", "card reader");
                MapVariantHideInteractionObjectsByKeywords("zipline", "warehouse", "accesscard", "cardreader", "card reader");
                MapVariantDisableRequireItemId(756, "J-Lab Lv3 card reader"); // Key_Card_JLabLv3
                MapVariantDisableInteractionsUnder("Scene/Group/DoorWire2_1", "J-Lab Lv3 door");
                MapVariantHideInteractionObjectsUnder("Scene/Group/DoorWire2_1", "J-Lab Lv3 door");
                MapVariantHideInteractionObjectsByKeywords("storm", "cave");

                // Permanently destroy the re-enabling scripts once. Doing this every
                // frame/second caused a full-scene MonoBehaviour scan every 2s and
                // severe hitching; these scripts are map furniture and never need to
                // be restored during this raid.
                MapVariantDestroyInteractionsUnder("Teleporters/DoorToCave", "cave entrance destroy");
                MapVariantDestroyInteractionsUnder("Teleporters/ToHiddenWarehouse", "warehouse entrance destroy");
                MapVariantDestroyInteractionsUnder("ToStormZone", "storm entrance destroy");
                MapVariantDestroyInteractionsUnder("Scene/Group/DoorWire2_1", "J-Lab Lv3 destroy");
                MapVariantCaptureBridgeCosts();
                MapVariantDestroyInteractionsUnder(
                    "LevelElements/GroundZero_01_SpecialElement/SmallBridgeNeedFix_1", "cave bridge destroy");
                MapVariantDestroyInteractionsUnder(
                    "LevelElements/GroundZero_01_SpecialElement/SmallBridgeNeedFix", "right bridge destroy");
                MapVariantDestroyRequireItemId(756, "J-Lab Lv3 card reader destroy");

                // Bunker: keep the model, remove only the extraction interaction.
                MapVariantDisableInteractionsUnder("Scene/BunkerDoorExit", "bunker");
                MapVariantHideInteractionObjectsUnder("Scene/BunkerDoorExit", "bunker");
                // Manhole / pipe / shortcut interactions: remove interaction only, keep visuals.
                MapVariantDisableInteractionsByKeywords("bunker", "manhole", "sewer", "pipe", "shortcut");
                MapVariantHideInteractionObjectsByKeywords("bunker", "manhole", "sewer", "pipe", "shortcut");
                // Last resort for the bunker/manhole prompt: strip every script on those
                // objects but keep Renderers/Animator so the model remains visible.
                MapVariantStripScriptsByKeywords("bunker", "manhole", "backtobunker");
                // The actual "进入地堡 / 下去" prompt is a POI / scene-loader marker
                // outside the model; hide the marker objects, keep the bunker/hatch model.
                MapVariantDisableSceneObject("MapElements/BunkerEnter", "bunker POI");
                MapVariantDisableSceneObject("BackToBunker", "manhole trigger");
                MapVariantHideMarkerChildrenUnder(
                    "LevelElements/GroundZero_01_SpecialElement/Contruction_\\Manhole/Built/BackToBunker_Manhole",
                    "manhole");
                // Keep the manhole in its original blocked/debris state and keep its
                // colliders. Only the "clear debris" interaction is removed.
                MapVariantBlockManhole(
                    "LevelElements/GroundZero_01_SpecialElement/Contruction_\\Manhole");

                // 3. Structural walls: open both explosive walls (and any other
                // BreakableWall present) as the base coop variant.
                MapVariantBreakWalls();
                // 4. Both repaired bridges are pre-built as base config. The cave
                // long bridge keeps its entrance interaction hidden, but its deck is
                // repaired so the upper crossing is not visually broken.
                MapVariantRepairBridges("LevelElements/GroundZero_01_SpecialElement/SmallBridgeNeedFix");
                MapVariantRepairBridges("LevelElements/GroundZero_01_SpecialElement/SmallBridgeNeedFix_1");

                // The real long bridge prompt is a third ConstructionSite at
                // .../SpecialElement/Bridge (cost 361:2|362:3 = the screenshot's
                // (0/2)(0/3)). Force it built instead of playing whack-a-mole with
                // its CostTaker, then seal the cave sub-map transition.
                MapVariantForceBuildConstructionSite(MapVariantLongBridgeRootPath, "long bridge");
                MapVariantRepairBridges(MapVariantLongBridgeRootPath);
                MapVariantSealCaveEntrance();
                MapVariantOpenSingleDirectionDoor();

                // Cave long bridge: no repair prompt even though the deck is repaired.
                MapVariantDisableInteractionsUnder(
                    "LevelElements/GroundZero_01_SpecialElement/SmallBridgeNeedFix_1", "cave bridge");
                MapVariantHideInteractionObjectsUnder(
                    "LevelElements/GroundZero_01_SpecialElement/SmallBridgeNeedFix_1", "cave bridge");

                // 4. Native spawners are intentionally KEPT. The coop native-spawner
                // guard suppresses their autonomous spawning, but TrySpawnRealAi()
                // still needs a RandomCharacterSpawner template to realize server AI.
                // Disabling the spawners here forced every server AI to fall back to a
                // ghost, whose local damage is suppressed -> "AI 打我不掉血".
                Log("[mapvariant] native spawners kept for server AI realization");

                // 5. Keep exactly the existing extraction point; only log it.
                MapVariantLogExit();
                if (_mapVariantPass >= 2) {
                    try {
                        MapVariantAuditBridgePrompts();
                    }
                    catch (Exception ex) {
                        Log("[mapvariant] bridge prompt audit failed: " +
                            ex.GetType().Name + ": " + ex.Message);
                    }
                }
                Log("[mapvariant] applied");
            }
            catch (Exception ex)
            {
                Log($"[mapvariant] apply failed: {ex.GetType().Name}: {ex.Message}");
                Log(ex.StackTrace ?? "<no stack>");
            }
            finally
            {
                MapVariantEndPass();
                _mapVariantRunning = false;
            }
        }

        // ---------- object removal ----------

        private void MapVariantDisablePathPrefix(string typeName, string pathPrefix, string label)
        {
            var type = MapVariantFindType(typeName);
            if (type == null)
            {
                Log($"[mapvariant] type not found: {typeName}");
                return;
            }

            var count = 0;
            foreach (var obj in MapVariantFind(type))
            {
                if (obj is not Component component || component == null)
                    continue;
                var path = MapVariantPath(component.transform);
                if (!path.StartsWith(pathPrefix, StringComparison.Ordinal))
                    continue;
                if (MapVariantDisableObject(component.gameObject, label, path))
                    count++;
            }

            if (count == 0)
                Log($"[mapvariant] no match: {label} prefix={pathPrefix}");
        }

        private void MapVariantDisableComponentOnPath(string typeName, string pathPrefix, string label)
        {
            var type = MapVariantFindType(typeName);
            if (type == null)
            {
                Log($"[mapvariant] type not found: {typeName}");
                return;
            }

            foreach (var obj in MapVariantFind(type))
            {
                if (obj is not Component component || component == null)
                    continue;
                var path = MapVariantPath(component.transform);
                if (!path.StartsWith(pathPrefix, StringComparison.Ordinal))
                    continue;
                if (component is Behaviour behaviour)
                {
                    if (!behaviour.enabled)
                        continue;
                    behaviour.enabled = false;
                    Log($"[mapvariant] disable {label} path={path}");
                }
            }
        }

        private void MapVariantDisableAllOfType(string typeName, string label)
        {
            var type = MapVariantFindType(typeName);
            if (type == null)
            {
                Log($"[mapvariant] type not found: {typeName}");
                return;
            }

            var count = 0;
            foreach (var obj in MapVariantFind(type))
            {
                if (obj is not Component component || component == null)
                    continue;
                if (!component.gameObject.activeInHierarchy)
                    continue;
                if (MapVariantDisableObject(component.gameObject, label, MapVariantPath(component.transform)))
                    count++;
            }
            Log($"[mapvariant] {label}: disabled {count}");
        }

        private void MapVariantDisableAllComponentsOfType(string typeName, string label)
        {
            var type = MapVariantFindType(typeName);
            if (type == null)
            {
                Log($"[mapvariant] type not found: {typeName}");
                return;
            }

            var count = 0;
            foreach (var obj in MapVariantFind(type))
            {
                if (obj is not Component component || component == null)
                    continue;
                if (component is Behaviour behaviour && behaviour.enabled)
                {
                    behaviour.enabled = false;
                    count++;
                    Log($"[mapvariant] disable {label} type={component.GetType().Name} path={MapVariantPath(component.transform)}");
                }
            }
            Log($"[mapvariant] {label}: components disabled={count}");
        }

        private void MapVariantDisablePetByMainScenePath()
        {
            const string petPath = "MultiSceneCore/Level_GroundZero_Main/Character(Clone)";
            foreach (var obj in MapVariantFind(typeof(CharacterMainControl)))
            {
                if (obj is not CharacterMainControl character || character == null)
                    continue;
                if (!string.Equals(MapVariantPath(character.transform), petPath, StringComparison.Ordinal))
                    continue;
                MapVariantDisableObject(character.gameObject, "pet-by-path", petPath);
            }
        }

        private void MapVariantDisableCharacterPreset(string presetSubstring, string label)
        {
            var matched = 0;
            var seen = 0;
            var samples = new List<string>();
            foreach (var obj in MapVariantFind(typeof(CharacterMainControl)))
            {
                if (obj is not CharacterMainControl character || character == null)
                    continue;
                seen++;
                var preset = MapVariantGetMember(character, "characterPreset");
                var asset = MapVariantGetMember(preset, "assetName") as string ?? string.Empty;
                if (samples.Count < 20)
                    samples.Add(asset + "@" + MapVariantPath(character.transform));
                if (asset.IndexOf(presetSubstring, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                MapVariantDisableObject(character.gameObject, label, MapVariantPath(character.transform));
                matched++;
            }
            if (matched == 0)
                Log($"[mapvariant] no character preset match: {label} substring={presetSubstring} characters={seen} " +
                    $"samples={string.Join(";", samples)}");
            else
                Log($"[mapvariant] character preset removed: {label} matched={matched}");
        }

        private void MapVariantBreakWalls()
        {
            var type = MapVariantFindType("Breakable");
            if (type == null)
            {
                Log("[mapvariant] type not found: Breakable");
                return;
            }

            var broken = 0;
            foreach (var obj in MapVariantFind(type))
            {
                if (obj is not Component component || component == null)
                    continue;
                if (!component.gameObject.name.StartsWith("BreakableWall", StringComparison.Ordinal))
                    continue;

                var stateSet = MapVariantSetEnumMember(component, "breakableState", "Breaked");
                MapVariantSetGameObjectMember(component, "normalVisual", false);
                MapVariantSetGameObjectMember(component, "dangerVisual", false);
                MapVariantSetGameObjectMember(component, "breakedVisual", true);
                MapVariantDisableColliderMember(component, "mainCollider");
                MapVariantDisableComponentMember(component, "simpleHealth");
                MapVariantDisableAllCollidersUnder(component.transform, "wall");
                var receiver = component.GetComponentInChildren<DamageReceiver>(true);
                if (receiver != null)
                    receiver.enabled = false;
                MapVariantDisableNavmeshCutsUnder(component.transform, "wall");
                broken++;
                Log($"[mapvariant] break wall path={MapVariantPath(component.transform)} state={stateSet} " +
                    $"breakedVisual={MapVariantGetMember(component, "breakedVisual") != null}");
            }
            Log($"[mapvariant] structural wall: broken={broken}");
        }

        private void MapVariantDisableAllCollidersUnder(Transform root, string label)
        {
            if (root == null)
                return;
            var count = 0;
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || !collider.enabled)
                    continue;
                collider.enabled = false;
                count++;
            }
            Log($"[mapvariant] {label}: colliders disabled={count} root={MapVariantPath(root)}");
        }

        private void MapVariantRepairBridges(string rootPath)
        {
            var root = GameObject.Find(rootPath);
            if (root == null)
            {
                Log($"[mapvariant] bridge root not found: {rootPath}");
                return;
            }

            if (_mapVariantLoggedBridges.Add(rootPath))
                MapVariantLogHierarchy(root.transform, 3);
            var repaired = false;
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child == null || child == root.transform)
                    continue;
                var name = child.name.ToLowerInvariant();
                if (name.StartsWith("notbuilt", StringComparison.Ordinal))
                {
                    if (child.gameObject.activeSelf)
                        child.gameObject.SetActive(false);
                    Log($"[mapvariant] bridge hide not-built child path={MapVariantPath(child)}");
                    continue;
                }
                if (name.Contains("costtaker") || name.Contains("interactmarker") ||
                    name.Contains("showifused") || name.Contains("hideifused"))
                {
                    if (child.gameObject.activeSelf)
                        child.gameObject.SetActive(false);
                    Log($"[mapvariant] bridge hide prompt child path={MapVariantPath(child)}");
                    continue;
                }
                if (name.Contains("fixed") || name.Contains("repair") || name.Contains("built") ||
                    name.Contains("bridge"))
                {
                    if (!child.gameObject.activeSelf)
                        child.gameObject.SetActive(true);
                    repaired = true;
                    Log($"[mapvariant] bridge activate repaired child path={MapVariantPath(child)}");
                }
                else if (name.Contains("needfix") || name.Contains("broken") || name.Contains("destroyed") ||
                         name.Contains("hole") || name.Contains("airwall"))
                {
                    if (child.gameObject.activeSelf)
                        child.gameObject.SetActive(false);
                    Log($"[mapvariant] bridge hide broken child path={MapVariantPath(child)}");
                }
            }

            MapVariantDisableInteractionsUnder(rootPath, "bridge");
            MapVariantDisableNavmeshCutsUnder(root.transform, "bridge");

            if (!repaired)
                MapVariantCreateBridgeDeck(root);
            Log($"[mapvariant] bridge repaired path={rootPath} nativeChild={repaired}");
        }

        private void MapVariantCreateBridgeDeck(GameObject root)
        {
            try
            {
                var deck = GameObject.CreatePrimitive(PrimitiveType.Cube);
                deck.name = "DuckovCoop_RepairedBridge";
                deck.transform.position = root.transform.position + Vector3.up * 0.1f;
                deck.transform.rotation = Quaternion.Euler(0f, root.transform.eulerAngles.y, 0f);
                deck.transform.localScale = new Vector3(4f, 0.3f, 10f);
                var renderer = deck.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Lit") ??
                                 Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
                    if (shader != null)
                    {
                        renderer.material = new Material(shader);
                        renderer.material.color = new Color(0.35f, 0.22f, 0.12f, 1f);
                    }
                }
                Log($"[mapvariant] bridge fallback deck created at {MapVariantVector(deck.transform.position)}");
            }
            catch (Exception ex)
            {
                Log($"[mapvariant] bridge deck failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void MapVariantDestroyInteractionsUnder(string pathPrefix, string label)
        {
            var count = 0;
            foreach (var behaviour in MapVariantFind(typeof(MonoBehaviour)))
            {
                if (behaviour is not MonoBehaviour component || component == null)
                    continue;
                var path = MapVariantPath(component.transform);
                if (!path.StartsWith(pathPrefix, StringComparison.Ordinal))
                    continue;
                if (!MapVariantIsInteractionType(component.GetType().Name))
                    continue;
                UnityEngine.Object.Destroy(component);
                count++;
                Log($"[mapvariant] destroy {label} type={component.GetType().Name} path={path}");
            }
            Log($"[mapvariant] {label}: interactions destroyed={count}");
        }

        private void MapVariantDestroyRequireItemId(int itemId, string label)
        {
            var type = MapVariantFindType("InteractableBase");
            if (type == null)
                return;
            var count = 0;
            foreach (var obj in MapVariantFind(type))
            {
                if (obj is not MonoBehaviour component || component == null)
                    continue;
                FieldInfo field = null;
                for (var current = component.GetType(); current != null && field == null; current = current.BaseType)
                    field = current.GetField("requireItemId",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                    continue;
                var actual = 0;
                try { actual = Convert.ToInt32(field.GetValue(component)); }
                catch { continue; }
                if (actual != itemId)
                    continue;
                UnityEngine.Object.Destroy(component);
                count++;
                Log($"[mapvariant] destroy {label} type={component.GetType().Name} path={MapVariantPath(component.transform)}");
            }
            Log($"[mapvariant] {label}: requireItemId={itemId} destroyed={count}");
        }

        /// <summary>
        /// Long bridge: set ConstructionSite.wasBuilt and refresh its built/not-built
        /// object sets instead of trying to remove the repair prompt one component at
        /// a time. The cave sub-map transition is sealed separately.
        /// </summary>
        private void MapVariantForceBuildConstructionSite(string rootPath, string label)
        {
            var root = GameObject.Find(rootPath);
            if (root == null) {
                Log($"[mapvariant] force build root not found: {rootPath}");
                return;
            }

            var siteType = MapVariantFindType("ConstructionSite");
            if (siteType == null) {
                Log($"[mapvariant] force build: ConstructionSite type not found ({label})");
                return;
            }

            var site = root.GetComponent(siteType);
            if (site == null)
                site = root.GetComponentInChildren(siteType, true);
            if (site == null) {
                Log($"[mapvariant] force build: ConstructionSite not found under {rootPath}");
                return;
            }

            var wasBuiltField = MapVariantFindField(site.GetType(), "wasBuilt");
            var previous = wasBuiltField != null ? wasBuiltField.GetValue(site) : null;
            if (wasBuiltField != null)
                wasBuiltField.SetValue(site, true);

            try {
                site.GetType()
                    .GetMethod("RefreshGameObjects", BindingFlags.Instance | BindingFlags.Public |
                                                     BindingFlags.NonPublic)
                    ?.Invoke(site, null);
            }
            catch (Exception ex) {
                Log($"[mapvariant] force build refresh failed: {ex.GetType().Name}: {ex.Message}");
            }

            MapVariantSetGameObjectsActive(MapVariantReadMember(site, "builtGameObjects") as Array, true);
            MapVariantSetGameObjectsActive(MapVariantReadMember(site, "notBuiltGameObjects") as Array, false);
            MapVariantSetGameObjectsActive(MapVariantReadMember(site, "setActiveOnBuilt") as Array, true);

            var costTaker = MapVariantReadMember(site, "costTaker") as Component;
            if (costTaker != null)
                costTaker.gameObject.SetActive(false);

            MapVariantDestroyInteractionsUnder(rootPath, label + " destroy");
            MapVariantDisableInteractionsUnder(rootPath, label);
            Log($"[mapvariant] force built {label} path={rootPath} wasBuilt={previous} " +
                $"costTaker={(costTaker != null)}");
        }

        /// <summary>
        /// The long bridge is forced built so the player can cross it; the cave sub-map
        /// transition at the far end must stay sealed. This is a one-shot seal per map
        /// pass (not a recurring scan): cave interactions are destroyed, teleport-like
        /// components near the bridge are disabled, and cave marker/prompt objects are
        /// hidden.
        /// </summary>
        private void MapVariantSealCaveEntrance()
        {
            var longBridge = GameObject.Find(MapVariantLongBridgeRootPath);
            var longBridgePosition = longBridge != null ? longBridge.transform.position : Vector3.zero;
            var sealedInteractions = 0;
            var sealedTeleports = 0;
            var hiddenMarkers = 0;

            foreach (var obj in MapVariantFind(typeof(MonoBehaviour))) {
                if (obj is not MonoBehaviour component || component == null)
                    continue;
                var path = MapVariantPath(component.transform);
                var pathHasCave = path.IndexOf("cave", StringComparison.OrdinalIgnoreCase) >= 0;
                var distance = longBridge != null
                    ? Vector3.Distance(component.transform.position, longBridgePosition)
                    : float.MaxValue;
                var typeName = component.GetType().Name;
                var isTeleport = MapVariantTypeContains(typeName, "teleport") ||
                                 MapVariantTypeContains(typeName, "sceneload") ||
                                 MapVariantTypeContains(typeName, "transition") ||
                                 MapVariantTypeContains(typeName, "portal");
                var isCaveInteraction = MapVariantTypeContains(typeName, "interact") ||
                                        string.Equals(typeName, "Door", StringComparison.OrdinalIgnoreCase) ||
                                        MapVariantTypeContains(typeName, "doortrigger") ||
                                        MapVariantTypeContains(typeName, "costtaker") ||
                                        MapVariantTypeContains(typeName, "constructionsite") ||
                                        isTeleport;

                if (!pathHasCave && !(distance <= 60f && isTeleport))
                    continue;
                if (!isCaveInteraction)
                    continue;

                if (isTeleport) {
                    if (component is Behaviour behaviour && behaviour.enabled) {
                        behaviour.enabled = false;
                        sealedTeleports++;
                        Log($"[mapvariant] cave seal teleport type={typeName} path={path}");
                    }
                    continue;
                }

                UnityEngine.Object.Destroy(component);
                sealedInteractions++;
                Log($"[mapvariant] cave seal interaction type={typeName} path={path}");
            }

            foreach (var transform in MapVariantTransforms()) {
                if (transform == null)
                    continue;
                var path = MapVariantPath(transform);
                if (path.IndexOf("cave", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var name = transform.name.ToLowerInvariant();
                if (!name.Contains("marker") && !name.Contains("prompt") && !name.Contains("interact"))
                    continue;
                if (!transform.gameObject.activeSelf)
                    continue;
                transform.gameObject.SetActive(false);
                hiddenMarkers++;
                Log($"[mapvariant] cave seal hide marker path={path}");
            }

            Log($"[mapvariant] cave entrance sealed interactions={sealedInteractions} " +
                $"teleports={sealedTeleports} markersHidden={hiddenMarkers} " +
                $"longBridge={(longBridge != null)}");
        }

        /// <summary>
        /// SingleDirectionDoor is a one-way factory gate whose Built/DoorInteract
        /// requires Key_Card_Factory (312). In the coop variant it should not be a
        /// locked/unlock event: clear the item requirement and open the door once so
        /// the player can pass from either side.
        /// </summary>
        private void MapVariantOpenSingleDirectionDoor()
        {
            const string rootPath = "LevelElements/GroundZero_01_SpecialElement/SingleDirectionDoor";
            var root = GameObject.Find(rootPath);
            if (root == null) {
                Log($"[mapvariant] single direction door root not found: {rootPath}");
                return;
            }

            var interactions = 0;
            foreach (var interact in root.GetComponentsInChildren<InteractableBase>(true)) {
                if (interact == null)
                    continue;
                var path = MapVariantPath(interact.transform);
                var itemBefore = interact.requireItemId;
                interact.requireItem = false;
                interact.requireItemId = 0;
                interactions++;
                Log($"[mapvariant] single direction door unlock interaction path={path} " +
                    $"requireItemId={itemBefore}->0");
            }

            var doors = 0;
            foreach (var door in root.GetComponentsInChildren<Door>(true)) {
                if (door == null)
                    continue;
                var path = MapVariantPath(door.transform);
                var closedBefore = MapVariantReadMember(door, "closed");
                try {
                    door.Open();
                }
                catch (Exception ex) {
                    Log($"[mapvariant] single direction door open failed: {ex.GetType().Name}: {ex.Message}");
                }
                var closedAfter = MapVariantReadMember(door, "closed");
                if (closedAfter is bool stillClosed && stillClosed) {
                    // Open() refuses before LevelManager.LevelInited; keep the map
                    // variant independent of that timing.
                    MapVariantFindField(door.GetType(), "closed")?.SetValue(door, false);
                    closedAfter = MapVariantReadMember(door, "closed");
                    Log($"[mapvariant] single direction door force-opened path={path} " +
                        $"closed={closedBefore}->{closedAfter}");
                }
                doors++;
                Log($"[mapvariant] single direction door opened path={path} " +
                    $"closed={closedBefore}->{closedAfter} noRequireItem={door.NoRequireItem}");
            }

            Log($"[mapvariant] single direction door: interactions={interactions} doors={doors}");
        }

        private static bool MapVariantTypeContains(string typeName, string keyword)
        {
            return typeName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static FieldInfo MapVariantFindField(Type type, string name)
        {
            for (var current = type; current != null; current = current.BaseType) {
                var field = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                    return field;
            }
            return null;
        }

        private static void MapVariantSetGameObjectsActive(Array objects, bool active)
        {
            if (objects == null)
                return;
            foreach (var item in objects) {
                if (item is GameObject go && go != null && go.activeSelf != active)
                    go.SetActive(active);
            }
        }

        /// <summary>
        /// One-shot bridge prompt audit at the +5s pass. Logs every construction/cost
        /// taker near either bridge with the fields the live bug needs (path, world
        /// position, enabled/active, requireItemId, cost). Instances outside the two
        /// bridge roots but carrying the exact bridge repair cost are destroyed by
        /// path; the active CostTaker list (which drives CostTakerHUD) is checked too.
        /// </summary>
        private void MapVariantAuditBridgePrompts()
        {
            if (_mapVariantBridgeAuditDone)
                return;
            _mapVariantBridgeAuditDone = true;

            var roots = MapVariantBridgeRootTransforms();
            Log($"[mapvariant] bridge prompt audit begin roots={roots.Count} " +
                $"costSignatures={string.Join(";", _mapVariantBridgeCostSignatures)}");

            // Another scene pass may have re-created a bridge interaction after pass 1.
            foreach (var rootPath in MapVariantBridgeRootPaths)
                MapVariantDestroyInteractionsUnder(rootPath, "bridge residual");

            var constructionType = MapVariantFindType("ConstructionSite");
            var seen = new HashSet<int>();
            var scanned = 0;
            var under = 0;
            var destroyed = 0;

            foreach (var typeName in new[] { "ConstructionSite", "CostTaker", "InteractableBase" })
            {
                var type = typeName == "ConstructionSite"
                    ? constructionType
                    : MapVariantFindType(typeName);
                if (type == null) {
                    Log($"[mapvariant] bridge prompt audit type not found: {typeName}");
                    continue;
                }

                foreach (var obj in MapVariantFind(type))
                {
                    if (obj is not MonoBehaviour component || component == null)
                        continue;
                    if (!seen.Add(component.GetInstanceID()))
                        continue;

                    scanned++;
                    var path = MapVariantPath(component.transform);
                    var distance = MapVariantNearestBridgeDistance(component.transform.position, roots);
                    if (MapVariantIsUnderBridgePath(path)) {
                        under++;
                        MapVariantLogBridgePrompt("UNDER", component, path, distance);
                        // Anything still alive under a root is a residual prompt: the
                        // whole subtree is safe because the bridge is pre-repaired.
                        if (MapVariantIsBridgePromptType(component.GetType().Name))
                            destroyed += MapVariantDestroyBridgePromptTree(component.gameObject, "bridge residual");
                        continue;
                    }

                    // Only construction/cost components (or anything under a cave
                    // path) are prompt candidates; lootboxes/doors near the bridge
                    // would otherwise flood the log.
                    var pathHasCave = path.IndexOf("cave", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!MapVariantIsBridgePromptType(component.GetType().Name) && !pathHasCave)
                        continue;
                    if (distance > 60f)
                        continue;

                    var cost = MapVariantReadMember(component, "cost") ??
                               MapVariantReadMember(component, "Cost");
                    var signature = MapVariantCostSignature(cost);
                    var matches = signature.Length > 0 &&
                                  _mapVariantBridgeCostSignatures.Contains(signature);
                    MapVariantLogBridgePrompt(matches ? "OUTLIER-MATCH" : "outlier", component, path, distance);
                    if (!matches || !MapVariantIsBridgePromptType(component.GetType().Name))
                        continue;

                    var destroyRoot = component.gameObject;
                    if (component.GetType().Name.IndexOf("CostTaker", StringComparison.OrdinalIgnoreCase) >= 0) {
                        var site = constructionType != null
                            ? component.GetComponentInParent(constructionType)
                            : null;
                        if (site != null && site.gameObject != destroyRoot)
                            destroyed += MapVariantDestroyBridgePromptTree(site.gameObject, "bridge outlier");
                    }
                    destroyed += MapVariantDestroyBridgePromptTree(destroyRoot, "bridge outlier");
                }
            }

            // CostTakerHUD reads the static active list, not the scene hierarchy, so
            // check the registered set explicitly. A matching bridge cost is required
            // for nearby non-bridge construction sites to avoid collateral removal.
            var costTakerType = MapVariantFindType("CostTaker");
            var activeProperty = costTakerType?.GetProperty("ActiveCostTakers",
                BindingFlags.Public | BindingFlags.Static);
            var activeEnumerable = activeProperty?.GetValue(null) as System.Collections.IEnumerable;
            var activeTakers = new List<MonoBehaviour>();
            if (activeEnumerable != null) {
                foreach (var item in activeEnumerable)
                    if (item is MonoBehaviour taker && taker != null)
                        activeTakers.Add(taker);
            }
            Log($"[mapvariant] bridge prompt audit active cost takers={activeTakers.Count}");
            foreach (var taker in activeTakers) {
                var path = MapVariantPath(taker.transform);
                var distance = MapVariantNearestBridgeDistance(taker.transform.position, roots);
                var cost = MapVariantReadMember(taker, "cost") ??
                           MapVariantReadMember(taker, "Cost");
                var signature = MapVariantCostSignature(cost);
                var matches = signature.Length > 0 &&
                              _mapVariantBridgeCostSignatures.Contains(signature);
                MapVariantLogBridgePrompt(matches ? "ACTIVE-MATCH" : "ACTIVE", taker, path, distance);
                if (!MapVariantIsUnderBridgePath(path) && !matches)
                    continue;

                if (constructionType != null) {
                    var site = taker.GetComponentInParent(constructionType);
                    if (site != null && site.gameObject != taker.gameObject)
                        destroyed += MapVariantDestroyBridgePromptTree(site.gameObject, "bridge active");
                }
                destroyed += MapVariantDestroyBridgePromptTree(taker.gameObject, "bridge active");
            }

            // A destroyed target can leave a pooled CostTakerHUD entry visible when
            // the unregister happened while the entry was inactive. Hide orphaned
            // active entries once; this is not a recurring scene scan.
            var entryType = MapVariantFindType("CostTakerHUD_Entry");
            var orphaned = 0;
            if (entryType != null) {
                foreach (var obj in MapVariantFind(entryType)) {
                    if (obj is not MonoBehaviour entry || entry == null)
                        continue;
                    var target = MapVariantReadMember(entry, "Target");
                    if (target is UnityEngine.Object unityTarget) {
                        // A destroyed target still compares non-null as a C# object,
                        // but Unity's overloaded == reports it as null.
                        if (unityTarget != null)
                            continue;
                    }
                    else if (target != null) {
                        continue;
                    }
                    if (!entry.gameObject.activeSelf)
                        continue;
                    Log($"[mapvariant] bridge audit hide orphan cost hud path={MapVariantPath(entry.transform)}");
                    entry.gameObject.SetActive(false);
                    orphaned++;
                }
            }

            Log($"[mapvariant] bridge prompt audit done scanned={scanned} under={under} " +
                $"destroyed={destroyed} orphanHud={orphaned}");
        }

        private void MapVariantCaptureBridgeCosts()
        {
            // Only the first pass can read the ConstructionSite/CostTaker cost: the
            // destroy below removes those components before the +5s audit pass.
            if (_mapVariantBridgeCostsCaptured)
                return;
            _mapVariantBridgeCostItemIds.Clear();
            _mapVariantBridgeCostSignatures.Clear();
            var constructionType = MapVariantFindType("ConstructionSite");
            var costTakerType = MapVariantFindType("CostTaker");

            foreach (var rootPath in MapVariantBridgeRootPaths) {
                var root = GameObject.Find(rootPath);
                if (root == null) {
                    Log($"[mapvariant] bridge cost root not found: {rootPath}");
                    continue;
                }

                Component site = constructionType != null ? root.GetComponent(constructionType) : null;
                if (site == null && constructionType != null)
                    site = root.GetComponentInChildren(constructionType, true);
                var cost = site != null ? MapVariantReadMember(site, "cost") : null;
                if (cost == null && costTakerType != null) {
                    var taker = root.GetComponentInChildren(costTakerType, true);
                    if (taker != null)
                        cost = MapVariantReadMember(taker, "cost") ?? MapVariantReadMember(taker, "Cost");
                }

                var signature = MapVariantCostSignature(cost);
                var key = site != null ? MapVariantReadMember(site, "_key") : null;
                if (signature.Length > 0)
                    _mapVariantBridgeCostSignatures.Add(signature);
                foreach (var id in MapVariantCostItemIds(cost))
                    _mapVariantBridgeCostItemIds.Add(id);

                Log($"[mapvariant] bridge cost root={rootPath} key={key ?? "-"} " +
                    $"cost={MapVariantCostText(cost)} signature={signature}");
            }
            _mapVariantBridgeCostsCaptured = _mapVariantBridgeCostSignatures.Count > 0;
        }

        private static List<Transform> MapVariantBridgeRootTransforms()
        {
            var roots = new List<Transform>();
            foreach (var transform in MapVariantTransforms()) {
                if (transform == null)
                    continue;
                var path = MapVariantPath(transform);
                foreach (var rootPath in MapVariantBridgeRootPaths) {
                    if (!string.Equals(path, rootPath, StringComparison.Ordinal))
                        continue;
                    roots.Add(transform);
                    break;
                }
            }
            return roots;
        }

        private static bool MapVariantIsUnderBridgePath(string path)
        {
            foreach (var rootPath in MapVariantBridgeRootPaths) {
                if (string.Equals(path, rootPath, StringComparison.Ordinal) ||
                    path.StartsWith(rootPath + "/", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool MapVariantIsBridgePromptType(string typeName)
        {
            var lower = typeName.ToLowerInvariant();
            return lower.Contains("constructionsite") || lower.Contains("costtaker") ||
                   lower.Contains("interactmarker");
        }

        private static float MapVariantNearestBridgeDistance(Vector3 position, List<Transform> roots)
        {
            var best = float.MaxValue;
            foreach (var root in roots) {
                if (root == null)
                    continue;
                var distance = Vector3.Distance(position, root.position);
                if (distance < best)
                    best = distance;
            }
            return best;
        }

        private static int MapVariantDestroyBridgePromptTree(GameObject root, string label)
        {
            if (root == null)
                return 0;
            var count = 0;
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true)) {
                if (behaviour == null)
                    continue;
                var typeName = behaviour.GetType().Name;
                if (!MapVariantIsBridgePromptType(typeName))
                    continue;
                UnityEngine.Object.Destroy(behaviour);
                count++;
                Log($"[mapvariant] destroy {label} type={typeName} path={MapVariantPath(behaviour.transform)}");
            }
            return count;
        }

        private static void MapVariantLogBridgePrompt(string tag, MonoBehaviour component, string path,
                                                      float distance)
        {
            var enabled = component is Behaviour behaviour
                ? behaviour.enabled.ToString()
                : "n/a";
            var requireItemId = MapVariantReadMember(component, "requireItemId");
            var cost = MapVariantReadMember(component, "cost") ??
                       MapVariantReadMember(component, "Cost");
            Log($"[mapvariant] bridge prompt {tag} type={component.GetType().Name} path={path} " +
                $"pos={MapVariantVector(component.transform.position)} enabled={enabled} " +
                $"active={component.gameObject.activeInHierarchy} " +
                $"requireItemId={requireItemId ?? "-"} cost={MapVariantCostText(cost)} " +
                $"bridgeDist={distance.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        private static object MapVariantReadMember(object instance, string name)
        {
            if (instance == null)
                return null;
            for (var current = instance.GetType(); current != null; current = current.BaseType) {
                var field = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                    return field.GetValue(instance);
                var property = current.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(instance);
            }
            return null;
        }

        private static List<int> MapVariantCostItemIds(object cost)
        {
            var ids = new List<int>();
            if (cost == null)
                return ids;
            if (MapVariantReadMember(cost, "items") is not Array items)
                return ids;
            foreach (var item in items) {
                var id = MapVariantReadMember(item, "id");
                if (id == null)
                    continue;
                try { ids.Add(Convert.ToInt32(id)); }
                catch { /* unexpected cost entry */ }
            }
            return ids;
        }

        private static string MapVariantCostSignature(object cost)
        {
            if (cost == null)
                return string.Empty;
            var parts = new List<string>();
            var money = MapVariantReadMember(cost, "money");
            if (money != null) {
                try {
                    var value = Convert.ToInt64(money);
                    if (value != 0)
                        parts.Add("money:" + value);
                }
                catch { /* unexpected money type */ }
            }
            if (MapVariantReadMember(cost, "items") is Array items) {
                foreach (var item in items) {
                    var id = MapVariantReadMember(item, "id");
                    var amount = MapVariantReadMember(item, "amount");
                    if (id == null || amount == null)
                        continue;
                    try {
                        parts.Add(Convert.ToInt32(id) + ":" + Convert.ToInt64(amount));
                    }
                    catch { /* unexpected item entry */ }
                }
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join("|", parts);
        }

        private static string MapVariantCostText(object cost)
        {
            var signature = MapVariantCostSignature(cost);
            return signature.Length == 0 ? "free" : signature;
        }

        private void MapVariantDisableRequireItemId(int itemId, string label, bool quiet = false)
        {
            var type = MapVariantFindType("InteractableBase");
            if (type == null)
            {
                Log($"[mapvariant] type not found: InteractableBase ({label})");
                return;
            }

            var count = 0;
            foreach (var obj in MapVariantFind(type))
            {
                if (obj is not MonoBehaviour component || component == null)
                    continue;

                FieldInfo field = null;
                for (var current = component.GetType(); current != null && field == null; current = current.BaseType)
                    field = current.GetField("requireItemId",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                    continue;

                var actual = 0;
                try { actual = Convert.ToInt32(field.GetValue(component)); }
                catch { continue; }
                if (actual != itemId)
                    continue;

                var path = MapVariantPath(component.transform);
                if (component is Behaviour behaviour && behaviour.enabled)
                {
                    behaviour.enabled = false;
                    count++;
                    if (!quiet)
                        Log($"[mapvariant] disable {label} interaction type={component.GetType().Name} path={path}");
                }
                MapVariantHideInteractionObjectsUnder(path, label, quiet);
            }
            if (!quiet)
                Log($"[mapvariant] {label}: requireItemId={itemId} disabled={count}");
        }

        private void MapVariantDisableInteractionsUnder(string pathPrefix, string label, bool quiet = false)
        {
            var count = 0;
            foreach (var behaviour in MapVariantFind(typeof(MonoBehaviour)))
            {
                if (behaviour is not MonoBehaviour component || component == null)
                    continue;
                var path = MapVariantPath(component.transform);
                if (!path.StartsWith(pathPrefix, StringComparison.Ordinal))
                    continue;
                if (!MapVariantIsInteractionType(component.GetType().Name))
                    continue;
                if (component is Behaviour behaviourComponent && behaviourComponent.enabled)
                {
                    behaviourComponent.enabled = false;
                    count++;
                    if (!quiet)
                        Log($"[mapvariant] disable {label} interaction type={component.GetType().Name} path={path}");
                }
            }
            if (!quiet)
                Log($"[mapvariant] {label}: interactions disabled={count}");
        }

        private void MapVariantDisableInteractionsByKeywords(params string[] keywords)
        {
            var count = 0;
            foreach (var behaviour in MapVariantFind(typeof(MonoBehaviour)))
            {
                if (behaviour is not MonoBehaviour component || component == null)
                    continue;
                var path = MapVariantPath(component.transform);
                var text = (path + " " + component.name).ToLowerInvariant();
                var matched = false;
                foreach (var keyword in keywords)
                {
                    if (text.IndexOf(keyword, StringComparison.Ordinal) < 0)
                        continue;
                    matched = true;
                    break;
                }
                if (!matched || !MapVariantIsInteractionType(component.GetType().Name))
                    continue;
                if (component is Behaviour behaviourComponent && behaviourComponent.enabled)
                {
                    behaviourComponent.enabled = false;
                    count++;
                    Log($"[mapvariant] disable keyword interaction type={component.GetType().Name} path={path}");
                }
            }
            Log($"[mapvariant] keyword interactions disabled={count}");
        }

        private void MapVariantHideInteractionObjectsUnder(string rootPath, string label, bool quiet = false)
        {
            var root = GameObject.Find(rootPath);
            if (root == null)
            {
                if (!quiet)
                    Log($"[mapvariant] hide interactions root not found: {rootPath}");
                return;
            }

            var count = 0;
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child == null)
                    continue;
                var name = child.name.ToLowerInvariant();
                if (!(name.Contains("interactmarker") || name.Contains("costtaker") ||
                      name == "trigger" || name.Contains("showifused") || name.Contains("hideifused")))
                    continue;
                if (!child.gameObject.activeSelf)
                    continue;
                child.gameObject.SetActive(false);
                count++;
                if (!quiet)
                    Log($"[mapvariant] hide {label} interaction object path={MapVariantPath(child)}");
            }
            if (!quiet)
                Log($"[mapvariant] {label}: interaction objects hidden={count}");
        }

        private void MapVariantHideInteractionObjectsByKeywords(params string[] keywords)
        {
            var count = 0;
            foreach (var obj in MapVariantFind(typeof(Transform)))
            {
                if (obj is not Transform transform || transform == null)
                    continue;
                var path = MapVariantPath(transform);
                var text = path.ToLowerInvariant();
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
                var name = transform.name.ToLowerInvariant();
                if (!(name.Contains("interactmarker") || name.Contains("costtaker") ||
                      name.Contains("showifused") || name.Contains("hideifused")))
                    continue;
                if (!transform.gameObject.activeSelf)
                    continue;
                transform.gameObject.SetActive(false);
                count++;
                Log($"[mapvariant] hide keyword interaction object path={path}");
            }
            Log($"[mapvariant] keyword interaction objects hidden={count}");
        }

        private void MapVariantStripScriptsByKeywords(params string[] keywords)
        {
            var count = 0;
            var types = new HashSet<string>(StringComparer.Ordinal);
            foreach (var behaviour in MapVariantFind(typeof(MonoBehaviour)))
            {
                if (behaviour is not MonoBehaviour component || component == null)
                    continue;
                var path = MapVariantPath(component.transform);
                var text = (path + " " + component.name).ToLowerInvariant();
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
                var typeName = component.GetType().Name;
                if (typeName.IndexOf("Animator", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (component is Behaviour behaviourComponent && behaviourComponent.enabled)
                {
                    behaviourComponent.enabled = false;
                    count++;
                    types.Add(typeName);
                    if (count <= 30)
                        Log($"[mapvariant] strip script type={typeName} path={path}");
                }
            }
            Log($"[mapvariant] stripped scripts={count} types={string.Join(",", types)}");
        }

        private void MapVariantDisableSceneObject(string path, string label)
        {
            var gameObject = MapVariantFindGameObject(path);
            if (gameObject == null)
            {
                Log($"[mapvariant] scene object not found: {label} path={path}");
                return;
            }
            MapVariantDisableObject(gameObject, label, path);
        }

        private void MapVariantBlockManhole(string rootPath)
        {
            var root = MapVariantFindGameObject(rootPath);
            if (root == null)
            {
                Log($"[mapvariant] manhole root not found: {rootPath}");
                return;
            }

            var blocked = false;
            var builtHidden = false;
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child == null || child.gameObject == root)
                    continue;
                var name = child.name.ToLowerInvariant();
                if (name.StartsWith("notbuilt", StringComparison.Ordinal) ||
                    name.Contains("debris") || name.Contains("rubble") || name.Contains("blocked") ||
                    name.Contains("cover"))
                {
                    if (!child.gameObject.activeSelf)
                        child.gameObject.SetActive(true);
                    blocked = true;
                    Log($"[mapvariant] manhole restore blocked child path={MapVariantPath(child)}");
                }
                else if (name.StartsWith("built", StringComparison.Ordinal))
                {
                    if (child.gameObject.activeSelf)
                        child.gameObject.SetActive(false);
                    builtHidden = true;
                    Log($"[mapvariant] manhole hide open child path={MapVariantPath(child)}");
                }
            }

            MapVariantDisableInteractionsUnder(rootPath, "manhole");
            MapVariantHideInteractionObjectsUnder(rootPath, "manhole");
            Log($"[mapvariant] manhole blocked={blocked} openHidden={builtHidden} collidersKept=true");
        }

        private void MapVariantDisableCollidersByPath(string path, string label)
        {
            var gameObject = MapVariantFindGameObject(path);
            if (gameObject == null)
            {
                Log($"[mapvariant] collider root not found: {label} path={path}");
                return;
            }
            MapVariantDisableAllCollidersUnder(gameObject.transform, label);
        }

        private void MapVariantHideMarkerChildrenUnder(string rootPath, string label)
        {
            var root = MapVariantFindGameObject(rootPath);
            if (root == null)
            {
                Log($"[mapvariant] marker root not found: {label} path={rootPath}");
                return;
            }
            var count = 0;
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child == null || child.gameObject == root)
                    continue;
                var name = child.name.ToLowerInvariant();
                if (!(name.Contains("marker") || name.Contains("interact") || name.Contains("icon") ||
                      name.Contains("prompt") || name.Contains("canvas") || name.Contains("ui")))
                    continue;
                if (!child.gameObject.activeSelf)
                    continue;
                child.gameObject.SetActive(false);
                count++;
                Log($"[mapvariant] hide {label} marker path={MapVariantPath(child)}");
            }
            Log($"[mapvariant] {label}: marker children hidden={count}");
        }

        private static GameObject MapVariantFindGameObject(string path)
        {
            var direct = GameObject.Find(path);
            if (direct != null)
                return direct;
            foreach (var obj in MapVariantFind(typeof(Transform)))
            {
                if (obj is not Transform transform || transform == null)
                    continue;
                if (string.Equals(MapVariantPath(transform), path, StringComparison.Ordinal))
                    return transform.gameObject;
            }
            return null;
        }

        private static bool MapVariantIsInteractionType(string typeName)
        {
            var lower = typeName.ToLowerInvariant();
            if (lower.Contains("bunkerdoorvisual") || lower.Contains("interactmarker") ||
                lower.Contains("costtaker") || lower == "door" || lower.Contains("doortrigger"))
                return true;
            if (lower.Contains("construction") || lower.Contains("constructionsite"))
                return true;
            if (lower.Contains("visual") || lower.Contains("renderer") || lower.Contains("animator"))
                return false;
            return lower.Contains("interact") || lower.Contains("trigger") || lower.Contains("require") ||
                   lower.Contains("dialogue") || lower.Contains("quest") || lower.Contains("audioeventproxy") ||
                   lower.Contains("door");
        }

        private void MapVariantDisableNavmeshCutsUnder(Transform root, string label)
        {
            if (root == null)
                return;
            var cutType = MapVariantFindType("Pathfinding.NavmeshCut") ??
                          MapVariantFindType("NavmeshCut");
            if (cutType == null)
            {
                Log($"[mapvariant] NavmeshCut type not found ({label})");
                return;
            }

            var rootPath = MapVariantPath(root);
            var count = 0;
            foreach (var obj in MapVariantFind(cutType))
            {
                if (obj is not Component component || component == null)
                    continue;
                var path = MapVariantPath(component.transform);
                if (!path.StartsWith(rootPath, StringComparison.Ordinal))
                    continue;
                if (component is Behaviour behaviour && behaviour.enabled)
                    behaviour.enabled = false;
                var forceUpdate = cutType.GetMethod("ForceUpdate",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                try
                {
                    forceUpdate?.Invoke(component, null);
                }
                catch
                {
                    // graph update is best-effort
                }
                count++;
                Log($"[mapvariant] disable NavmeshCut path={path}");
            }
            Log($"[mapvariant] NavmeshCut disabled={count} ({label})");
        }

        private void MapVariantLogHierarchy(Transform root, int maxDepth)
        {
            MapVariantLogHierarchy(root, maxDepth, 0);
        }

        private void MapVariantLogHierarchy(Transform root, int maxDepth, int depth)
        {
            if (root == null || depth > maxDepth)
                return;
            Log($"[mapvariant] bridge hierarchy {new string(' ', depth * 2)}{root.name} active={root.gameObject.activeSelf}");
            foreach (Transform child in root)
                MapVariantLogHierarchy(child, maxDepth, depth + 1);
        }

        private static bool MapVariantSetEnumMember(object target, string name, string value)
        {
            if (target == null)
                return false;
            var type = target.GetType();
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
                if (field == null || !field.FieldType.IsEnum)
                    continue;
                try
                {
                    field.SetValue(target, Enum.Parse(field.FieldType, value, true));
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        private static bool MapVariantSetGameObjectMember(object target, string name, bool active)
        {
            if (MapVariantGetMember(target, name) is not GameObject gameObject || gameObject == null)
                return false;
            gameObject.SetActive(active);
            return true;
        }

        private static bool MapVariantDisableColliderMember(object target, string name)
        {
            if (MapVariantGetMember(target, name) is not Collider collider || collider == null)
                return false;
            collider.enabled = false;
            return true;
        }

        private static bool MapVariantDisableComponentMember(object target, string name)
        {
            var value = MapVariantGetMember(target, name);
            if (value is Behaviour behaviour)
            {
                behaviour.enabled = false;
                return true;
            }
            if (value is Collider collider)
            {
                collider.enabled = false;
                return true;
            }
            return false;
        }

        private bool MapVariantDisableObject(GameObject gameObject, string label, string path)
        {
            if (gameObject == null)
                return false;
            var id = gameObject.GetInstanceID();
            if (!_mapVariantRemoved.Add(id))
                return false;
            if (gameObject.activeSelf)
                gameObject.SetActive(false);
            Log($"[mapvariant] disable {label} path={path}");
            return true;
        }

        private void MapVariantLogExit()
        {
            var creator = MapVariantFindType("ExitCreator");
            if (creator != null)
            {
                foreach (var obj in MapVariantFind(creator))
                {
                    if (obj is not Component component || component == null)
                        continue;
                    Log($"[mapvariant] keep exit creator path={MapVariantPath(component.transform)}");
                }
            }

            var proxy = MapVariantFindType("EvacuationCountdownUIProxy");
            if (proxy != null)
            {
                foreach (var obj in MapVariantFind(proxy))
                {
                    if (obj is not Component component || component == null)
                        continue;
                    var path = MapVariantPath(component.transform);
                    Log($"[mapvariant] keep exit path={path} pos={MapVariantVector(component.transform.position)}");
                }
            }
        }

        // ---------- NavMesh sampling ----------

        private void MapVariantLogNavMeshSample(string label, string path, Vector3 position)
        {
            if (!MapVariantTrySampleNavMesh(position, out var hit, out var distance))
            {
                Log($"[mapvariant] navmesh {label} path={path} pos={MapVariantVector(position)} hit=false");
                return;
            }

            Log($"[mapvariant] navmesh {label} path={path} pos={MapVariantVector(position)} " +
                $"hit=true nav={MapVariantVector(hit)} distance={distance:F2}");
        }

        private static bool MapVariantTrySampleNavMesh(Vector3 source, out Vector3 hit, out float distance)
        {
            hit = source;
            distance = -1f;
            try
            {
                if (_mapVariantNavMeshType == null)
                    _mapVariantNavMeshType = Type.GetType("UnityEngine.AI.NavMesh, UnityEngine.AIModule");

                if (_mapVariantNavMeshType == null)
                    return false;

                if (_mapVariantNavMeshSample == null)
                {
                    foreach (var method in _mapVariantNavMeshType.GetMethods(BindingFlags.Public | BindingFlags.Static))
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
                            _mapVariantNavMeshSample = method;
                            break;
                        }
                    }
                }

                if (_mapVariantNavMeshSample == null)
                    return false;

                var hitType = _mapVariantNavMeshSample.GetParameters()[1].ParameterType.GetElementType();
                if (hitType == null)
                    return false;

                object hitBox = Activator.CreateInstance(hitType);
                object[] args = { source, hitBox, 2f, -1 };
                var ok = (bool)_mapVariantNavMeshSample.Invoke(null, args);
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

        // ---------- helpers ----------

        private Type MapVariantFindType(string name)
        {
            if (_mapVariantTypeCache.TryGetValue(name, out var cached))
                return cached;

            Type result = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var direct = assembly.GetType(name, false);
                    if (direct != null)
                    {
                        result = direct;
                        break;
                    }
                }
                catch
                {
                    // ignore broken assemblies
                }

                try
                {
                    foreach (var candidate in assembly.GetTypes())
                    {
                        if (candidate.Name != name)
                            continue;
                        result = candidate;
                        break;
                    }
                }
                catch
                {
                    // dynamic assemblies / type load errors are fine
                }

                if (result != null)
                    break;
            }

            _mapVariantTypeCache[name] = result;
            return result;
        }

        private static object MapVariantGetMember(object target, string name)
        {
            if (target == null)
                return null;

            var type = target.GetType();
            for (var current = type; current != null; current = current.BaseType)
            {
                try
                {
                    var field = current.GetField(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);
                    if (field != null)
                        return field.GetValue(target);

                    var property = current.GetProperty(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);
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

        private static string MapVariantPath(Transform transform)
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

        private static string MapVariantVector(Vector3 value)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:0.###},{1:0.###},{2:0.###}", value.x, value.y, value.z);
        }
    }
}
