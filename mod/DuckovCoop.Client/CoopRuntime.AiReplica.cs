using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DuckovCoop.Client;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// P-AI-REPLICA prototype: create a native CharacterMainControl from the real
    /// AI model prefab, then strip AI/movement/damage authority. This is gated by
    /// Gate A until SAiSpawn carries a real monsterId/model identity.
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private const string GateAAiModelMapFile = "gate_a_ai_models.txt";
        private const string GateAAiDefaultModel = "0_CharacterModel_Custom_Enemy _Mushroom";

        private sealed class PresetReplicaJob
        {
            public AiSpawn Spawn;
            public CharacterRandomPreset Preset;
        }

        private readonly Queue<PresetReplicaJob> _presetReplicaQueue = new Queue<PresetReplicaJob>();
        private bool _presetReplicaWorkerRunning;
        private readonly Dictionary<uint, Transform> _aiReplicaModelRoots = new Dictionary<uint, Transform>();
        private readonly Dictionary<uint, Transform> _aiReplicaModels = new Dictionary<uint, Transform>();

        private bool TryCreateFullAiReplica(AiSpawn spawn, out GameObject root)
        {
            root = null;
            if (spawn == null || _client == null)
                return false;

            try
            {
                var main = CharacterMainControl.Main;
                if (main == null)
                {
                    Log("[gatea] A11 replica skipped: no local CharacterMainControl");
                    return false;
                }

                // Prefer Assembly-CSharp: TeamSoda.Duckov.Core also exposes a
                // LevelManager name, but only the game LevelManager has CharacterCreator.
                var levelManagerType = ResolveGameType("LevelManager");
                if (levelManagerType == null)
                {
                    Log("[gatea] A11 replica failed: LevelManager type missing");
                    return false;
                }
                var levelManager = levelManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                if (levelManager == null)
                {
                    Log($"[gatea] A11 replica failed: LevelManager.Instance null type={levelManagerType.Assembly.GetName().Name}");
                    return false;
                }
                var creator = levelManagerType.GetProperty("CharacterCreator",
                                  BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  ?.GetValue(levelManager) ??
                              levelManagerType.GetField("characterCreator",
                                  BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  ?.GetValue(levelManager);
                if (creator == null)
                {
                    Log($"[gatea] A11 replica failed: CharacterCreator null type={levelManagerType.Assembly.GetName().Name}");
                    return false;
                }

                // Native preset path: the preset owns both the model prefab and the
                // monster's own CharacterItem type. Building the replica from the local
                // player's CharacterItem leaves the enemy renderers without their
                // appearance (client saw untextured/white monsters), so prefer the
                // preset identity and keep the old path only as a fallback.
                var catalogEntry = FindMonster(spawn.MonsterId);
                var preset = FindPresetForMonster(catalogEntry);
                CharacterModel model = null;
                object item = null;
                var presetItemTypeId = 0;
                if (preset != null)
                {
                    model = ReadMember<CharacterModel>(preset, "CharacterModel");
                    presetItemTypeId = ReadPresetItemTypeId(preset);
                    if (presetItemTypeId > 0)
                        item = LoadReplicaCharacterItem(creator, presetItemTypeId);
                    Log($"[catalog] replica preset ai={spawn.AiId} monster={spawn.MonsterId} " +
                        $"preset={preset.name} itemTypeId={presetItemTypeId} " +
                        $"model={(model != null ? model.name : "<null>")} item={(item != null)}");
                }

                if (model == null)
                    model = ResolveAiModel(spawn);
                if (model == null)
                {
                    Log($"[catalog] A11 replica model missing ai={spawn.AiId} monster={spawn.MonsterId}");
                    return false;
                }

                if (item == null)
                {
                    var typeId = ReadCharacterItemTypeId(main);
                    item = ResolveReplicaCharacterItem(main, levelManagerType, levelManager);
                    if (item == null)
                    {
                        Log($"[gatea] A11 replica failed: character item null (typeId={typeId})");
                        return false;
                    }
                }
                Log($"[gatea] A11 replica item ai={spawn.AiId} type={item.GetType().FullName}");

                var create = creator.GetType().GetMethod("CreateCharacter",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (create == null)
                {
                    Log($"[gatea] A11 replica failed: CreateCharacter missing on {creator.GetType().FullName}");
                    return false;
                }

                var position = new Vector3(spawn.X / 100f, spawn.Y / 100f, spawn.Z / 100f);
                var rotation = Quaternion.Euler(0f, spawn.Yaw / 65535f * 360f, 0f);
                var task = create.Invoke(creator, new object[] { item, model, position, rotation });
                var character = AwaitUniTaskResult(task) as CharacterMainControl;
                if (character == null)
                {
                    Log($"[gatea] A11 replica create failed ai={spawn.AiId} model={model.name}");
                    return false;
                }

                root = ConfigureFullAiReplica(spawn, character);
                if (root != null)
                {
                    ApplyPresetFace(spawn, preset, character);
                    LogReplicaVisuals(spawn, root);
                }
                return root != null;
            }
            catch (Exception ex)
            {
                Log($"[gatea] A11 replica failed ai={spawn.AiId}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static object ResolveReplicaCharacterItem(CharacterMainControl main, Type levelManagerType,
            object levelManager)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                var item = typeof(CharacterMainControl).GetProperty("CharacterItem", any)?.GetValue(main);
                if (item == null)
                    item = typeof(CharacterMainControl).GetField("characterItem", any)?.GetValue(main);
                if (item != null)
                    return item;
            }
            catch
            {
                // continue to LevelManager
            }

            try
            {
                var load = levelManagerType.GetMethod("LoadOrCreateCharacterItemInstance", any, null,
                    Type.EmptyTypes, null);
                if (load != null)
                {
                    var loaded = AwaitUniTaskResult(load.Invoke(levelManager, null));
                    if (loaded != null)
                    {
                        Log($"[gatea] A11 replica item loaded by LevelManager type={loaded.GetType().FullName}");
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[gatea] A11 replica item load failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                var typeId = ReadCharacterItemTypeId(main);
                var itemAssetsType = ResolveGameType("ItemAssetsCollection");
                var instantiate = itemAssetsType?.GetMethod("InstantiateSync",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
                if (typeId > 0 && instantiate != null)
                    return instantiate.Invoke(null, new object[] { typeId });
            }
            catch (Exception ex)
            {
                Log("[gatea] A11 replica item instantiate failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return null;
        }

        private static Type ResolveGameType(string name)
        {
            var fromGameAssembly = typeof(CharacterMainControl).Assembly.GetType(name, false);
            return fromGameAssembly ?? FindTypeByName(name);
        }

        private CharacterModel ResolveAiModel(AiSpawn spawn)
        {
            var entry = FindMonster(spawn.MonsterId);
            if (entry != null && !string.IsNullOrEmpty(entry.CharacterModelKey))
            {
                var model = FindCharacterModelByKey(entry.CharacterModelKey);
                if (model != null)
                {
                    Log($"[catalog] replica model ai={spawn.AiId} monster={spawn.MonsterId} " +
                        $"key={entry.CharacterModelKey} controller={entry.AnimatorControllerKey}");
                    return model;
                }

                Log($"[catalog] replica model not found ai={spawn.AiId} " +
                    $"monster={spawn.MonsterId} key={entry.CharacterModelKey}");
            }
            else
            {
                Log($"[catalog] replica entry missing ai={spawn.AiId} monster={spawn.MonsterId}");
            }

            // Development-only fallback for Gate A when the catalog is incomplete.
            if (GateAProbe.Enabled)
                return ResolveGateAAiModel(spawn.AiId);
            return null;
        }

        private static CharacterModel FindCharacterModelByKey(string key)
        {
            var normalized = NormalizeModelName(key);
            foreach (var model in Resources.FindObjectsOfTypeAll<CharacterModel>())
            {
                if (model != null &&
                    string.Equals(NormalizeModelName(model.name), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return model;
                }
            }

            return null;
        }

        private static CharacterRandomPreset FindPresetForMonster(MonsterCatalogEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.AssetName))
                return null;

            var wanted = NormalizeModelName(entry.AssetName);
            foreach (var preset in Resources.FindObjectsOfTypeAll<CharacterRandomPreset>())
            {
                if (preset != null &&
                    string.Equals(NormalizeModelName(preset.name), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return preset;
                }
            }

            // Presets that are not registered in Resources still hang off the scene
            // spawners; scan the same lists the host path uses.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var spawner in UnityEngine.Object.FindObjectsOfType<RandomCharacterSpawner>())
            {
                if (spawner == null)
                    continue;
                var infos = typeof(RandomCharacterSpawner).GetField("randomPresetInfos", flags)
                    ?.GetValue(spawner) as System.Collections.IList;
                if (infos == null)
                    continue;
                for (var i = 0; i < infos.Count; i++)
                {
                    var info = infos[i];
                    if (info == null)
                        continue;
                    var candidate = info.GetType().GetField("randomPreset", flags)?.GetValue(info)
                        as CharacterRandomPreset;
                    if (candidate != null &&
                        string.Equals(NormalizeModelName(candidate.name), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private bool StartPresetReplica(AiSpawn spawn, CharacterRandomPreset preset)
        {
            try
            {
                // Serialize native preset spawns: 13 parallel CreateCharacterAsync calls
                // raced on the same "new AI" scan and could steal each other's character.
                _presetReplicaQueue.Enqueue(new PresetReplicaJob { Spawn = spawn, Preset = preset });
                if (!_presetReplicaWorkerRunning)
                    StartCoroutine(PresetReplicaWorker());
                return true;
            }
            catch (Exception ex)
            {
                Log($"[catalog] replica preset start failed ai={spawn.AiId}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private System.Collections.IEnumerator PresetReplicaWorker()
        {
            _presetReplicaWorkerRunning = true;
            try
            {
                while (_presetReplicaQueue.Count > 0)
                {
                    var job = _presetReplicaQueue.Dequeue();
                    if (job == null || job.Spawn == null || job.Preset == null)
                        continue;
                    if (_client == null || !_client.AiSpawns.ContainsKey(job.Spawn.AiId))
                        continue;

                    object group;
                    int sceneIndex;
                    ResolvePresetContext(out group, out sceneIndex);
                    yield return CreatePresetReplicaCoroutine(job.Spawn, job.Preset, group, sceneIndex);
                }
            }
            finally
            {
                _presetReplicaWorkerRunning = false;
            }
        }

        private static void ResolvePresetContext(out object group, out int sceneIndex)
        {
            group = null;
            sceneIndex = 0;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var spawner in UnityEngine.Object.FindObjectsOfType<RandomCharacterSpawner>())
            {
                if (spawner == null)
                    continue;
                var root = typeof(RandomCharacterSpawner).GetField("spawnerRoot", flags)?.GetValue(spawner);
                var presets = typeof(RandomCharacterSpawner).GetField("randomPresetInfos", flags)?.GetValue(spawner)
                    as System.Collections.IList;
                if (root == null || presets == null || presets.Count == 0)
                    continue;
                group = typeof(RandomCharacterSpawner).GetField("masterGroup", flags)?.GetValue(spawner);
                if (root.GetType().GetField("relatedScene", flags)?.GetValue(root) is int index)
                    sceneIndex = index;
                return;
            }
        }

        private System.Collections.IEnumerator CreatePresetReplicaCoroutine(AiSpawn spawn,
            CharacterRandomPreset preset, object group, int sceneIndex)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var position = new Vector3(spawn.X / 100f, spawn.Y / 100f, spawn.Z / 100f);
            var before = new HashSet<int>();
            foreach (var ai in UnityEngine.Object.FindObjectsOfType<AICharacterController>())
            {
                if (ai != null)
                    before.Add(ai.GetInstanceID());
            }

            object task = null;
            try
            {
                var create = preset.GetType().GetMethod("CreateCharacterAsync", any);
                if (create == null)
                    Log($"[catalog] replica preset spawn: CreateCharacterAsync missing on {preset.GetType().Name}");
                else
                    task = create.Invoke(preset, new object[] { position, Vector3.forward, sceneIndex, group, false });
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Log($"[catalog] replica preset spawn failed ai={spawn.AiId}: {inner.GetType().Name}: {inner.Message}");
            }

            var deadline = Time.unscaledTime + 3f;
            CharacterMainControl character = null;
            AICharacterController fresh = null;
            while (Time.unscaledTime < deadline)
            {
                if (fresh == null)
                {
                    foreach (var ai in UnityEngine.Object.FindObjectsOfType<AICharacterController>())
                    {
                        if (ai == null || before.Contains(ai.GetInstanceID()))
                            continue;
                        fresh = ai;
                        break;
                    }
                }

                if (fresh != null)
                {
                    // The native spawn starts with its AI live: freeze it immediately.
                    // Do not touch its team here - the neutral team colour is grey and the
                    // configured team is applied in ConfigureFullAiReplica.
                    if (fresh.enabled)
                        fresh.enabled = false;
                }

                if (task != null)
                {
                    var result = AwaitUniTaskResult(task);
                    if (result != null)
                    {
                        character = result as CharacterMainControl;
                        break;
                    }
                }

                yield return null;
            }

            if (character == null && fresh != null)
                character = fresh.GetComponent<CharacterMainControl>();

            if (character == null)
            {
                Log($"[catalog] replica preset spawn no character ai={spawn.AiId}, falling back");
                if (TryCreateFullAiReplica(spawn, out var fallback) && fallback != null)
                    ReplaceGhost(spawn.AiId, fallback);
                else
                    CreateAiGhost(spawn);
                yield break;
            }

            if (_client == null || !_client.AiSpawns.ContainsKey(spawn.AiId) ||
                (spawn.RaidEpoch != 0 && _aiSpawnEpoch != 0 && spawn.RaidEpoch != _aiSpawnEpoch))
            {
                Log($"[catalog] replica preset spawn canceled ai={spawn.AiId} (despawned or epoch changed)");
                UnityEngine.Object.Destroy(character.gameObject);
                yield break;
            }

            var createdPreset = ReadMember<CharacterRandomPreset>(character, "characterPreset");
            if (createdPreset != null && preset != null && createdPreset != preset)
            {
                Log($"[catalog] replica preset mismatch ai={spawn.AiId} " +
                    $"got={createdPreset.name} want={preset.name}");
            }

            var root = ConfigureFullAiReplica(spawn, character);
            if (root == null)
                yield break;

            DetachFromSpawnerGroup(group, character);
            ReplaceGhost(spawn.AiId, root);
            ApplyPresetFace(spawn, preset, character);
            Log($"[visual] replica color ai={spawn.AiId} {DescribeBodyColor(root)}");
            LogReplicaVisuals(spawn, root);
            Log($"ai replica spawned: id={spawn.AiId} pos={root.transform.position} source=preset");
        }

        private static void DetachFromSpawnerGroup(object group, CharacterMainControl character)
        {
            if (group == null || character == null)
                return;
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var list = GetGroupCharacters(group) as System.Collections.IList;
                var controller = character.GetComponent<AICharacterController>();
                if (list != null && controller != null)
                    list.Remove(controller);

                var leader = group.GetType().GetField("leaderAI", any);
                if (leader != null && controller != null && ReferenceEquals(leader.GetValue(group), controller))
                    leader.SetValue(group, null);
            }
            catch (Exception ex)
            {
                Log("[catalog] replica group detach failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void ApplyPresetFace(AiSpawn spawn, CharacterRandomPreset preset, CharacterMainControl character)
        {
            try
            {
                var facePreset = ReadMember<CustomFacePreset>(preset, "FacePreset");
                var model = ReadMember<CharacterModel>(character, "characterModel");
                if (facePreset != null && model != null)
                {
                    model.SetFaceFromPreset(facePreset);
                    Log($"[visual] replica face ai={spawn.AiId} facePreset={facePreset.name}");
                    return;
                }

                Log($"[visual] replica face ai={spawn.AiId} " +
                    $"facePreset={(facePreset != null ? facePreset.name : "<null>")} model={(model != null)}");
            }
            catch (Exception ex)
            {
                Log("[visual] replica face failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static int ReadRelatedScene(CharacterMainControl character)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var field = typeof(CharacterMainControl).GetField("relatedScene", any);
                if (field != null)
                    return Convert.ToInt32(field.GetValue(character));
            }
            catch
            {
                // private field probe only
            }

            return 0;
        }

        private static string DescribeBodyColor(GameObject root)
        {
            try
            {
                var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                Renderer body = null;
                foreach (var renderer in renderers)
                {
                    if (renderer == null)
                        continue;
                    if (renderer.name.IndexOf("DuckBody", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        body = renderer;
                        break;
                    }

                    if (body == null)
                        body = renderer;
                }

                if (body == null)
                    return "body=<none>";

                var material = body.sharedMaterial;
                var block = new MaterialPropertyBlock();
                body.GetPropertyBlock(block);
                var builder = new StringBuilder();
                builder.Append("body=").Append(body.name)
                    .Append(" mat=").Append(material != null ? material.name : "<null>")
                    .Append(" blockEmpty=").Append(block.isEmpty);
                foreach (var key in new[] { "_BaseColor", "_Color", "_MainColor", "_SkinColor", "_TintColor", "_Tint" })
                {
                    if (material == null || !material.HasProperty(key))
                        continue;
                    builder.Append(' ').Append(key).Append("=m:").Append(material.GetColor(key));
                    try
                    {
                        builder.Append("/b:").Append(block.GetColor(key));
                    }
                    catch
                    {
                        builder.Append("/b:<none>");
                    }
                }

                return builder.ToString();
            }
            catch (Exception ex)
            {
                return "color probe failed: " + ex.GetType().Name;
            }
        }

        private static int ReadPresetItemTypeId(object preset)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (preset == null)
                return 0;
            try
            {
                var property = preset.GetType().GetProperty("characterItemTypeID", any);
                if (property != null)
                    return Convert.ToInt32(property.GetValue(preset, null));
                var method = preset.GetType().GetMethod("get_characterItemTypeID", any);
                if (method != null)
                    return Convert.ToInt32(method.Invoke(preset, null));
            }
            catch (Exception ex)
            {
                Log("[catalog] replica preset itemTypeId failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return 0;
        }

        private static object LoadReplicaCharacterItem(object creator, int typeId)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                var load = creator?.GetType().GetMethod("LoadOrCreateCharacterItemInstance", any, null,
                    new[] { typeof(int) }, null);
                if (load != null)
                {
                    var loaded = AwaitUniTaskResult(load.Invoke(creator, new object[] { typeId }));
                    if (loaded != null)
                        return loaded;
                }
            }
            catch (Exception ex)
            {
                Log("[catalog] replica item(creator) failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                var collection = ResolveGameType("ItemAssetsCollection");
                var instantiate = collection?.GetMethod("InstantiateSync",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                    new[] { typeof(int) }, null);
                if (instantiate != null)
                {
                    var created = instantiate.Invoke(null, new object[] { typeId });
                    if (created != null)
                        return created;
                }
            }
            catch (Exception ex)
            {
                Log("[catalog] replica item(collection) failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return null;
        }

        private static T ReadMember<T>(object target, string name) where T : class
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (target == null)
                return null;
            try
            {
                var property = target.GetType().GetProperty(name, any);
                if (property != null)
                    return property.GetValue(target, null) as T;
                var field = target.GetType().GetField(name, any);
                if (field != null)
                    return field.GetValue(target) as T;
            }
            catch
            {
                // reflection probe only
            }

            return null;
        }

        private void LogReplicaVisuals(AiSpawn spawn, GameObject root)
        {
            try
            {
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                var builder = new StringBuilder();
                var shown = 0;
                foreach (var renderer in renderers)
                {
                    if (renderer == null || !renderer.enabled)
                        continue;
                    var material = renderer.sharedMaterial;
                    builder.Append('[').Append(renderer.name);
                    builder.Append(" mat=").Append(material != null ? material.name : "<null>");
                    if (material != null && material.shader != null)
                        builder.Append(" shader=").Append(material.shader.name);
                    builder.Append(" block=").Append(renderer.HasPropertyBlock());
                    builder.Append(']');
                    if (++shown >= 3)
                        break;
                }

                Log($"[visual] replica ai={spawn.AiId} renderers={renderers.Length} {builder}");
            }
            catch (Exception ex)
            {
                Log("[visual] replica dump failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private CharacterModel ResolveGateAAiModel(uint aiId)
        {
            var name = ReadGateAAiModelName(aiId);
            var normalized = NormalizeModelName(name);
            CharacterModel fallback = null;
            foreach (var model in Resources.FindObjectsOfTypeAll<CharacterModel>())
            {
                if (model == null)
                    continue;
                var modelName = NormalizeModelName(model.name);
                if (fallback == null && modelName.IndexOf("Mushroom", StringComparison.OrdinalIgnoreCase) >= 0)
                    fallback = model;
                if (string.Equals(modelName, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[gatea] A11 replica model ai={aiId} model={model.name}");
                    return model;
                }
            }

            if (fallback != null)
                Log($"[gatea] A11 replica model fallback ai={aiId} requested={name} model={fallback.name}");
            return fallback;
        }

        private static string ReadGateAAiModelName(uint aiId)
        {
            try
            {
                var directory = Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location);
                var path = Path.Combine(directory ?? string.Empty, GateAAiModelMapFile);
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                            continue;

                        var split = trimmed.IndexOf('=');
                        if (split <= 0)
                            continue;

                        if (uint.TryParse(trimmed.Substring(0, split).Trim(), out var id) && id == aiId)
                            return trimmed.Substring(split + 1).Trim();
                    }
                }
            }
            catch
            {
                // fall through to the prototype model
            }

            return GateAAiDefaultModel;
        }

        private static string NormalizeModelName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            var result = name.Trim();
            const string clone = "(Clone)";
            if (result.EndsWith(clone, StringComparison.OrdinalIgnoreCase))
                result = result.Substring(0, result.Length - clone.Length).Trim();
            return result;
        }

        private GameObject ConfigureFullAiReplica(AiSpawn spawn, CharacterMainControl character)
        {
            var root = character.gameObject;
            root.name = "DuckovCoop_AiReplica_" + spawn.AiId;
            // Reference mod pattern: configure the replica while inactive, then activate it
            // once. This lets native Awake/Start/OnEnable (Animator, CharacterAnimationControl,
            // MagicBlending) initialize in their normal order. Disabling/延迟 Animator or
            // stripping MagicBlending leaves an uninitialised AnimationScriptPlayable and
            // produces "ArgumentNullException: The Playable is null".
            root.SetActive(false);
            root.transform.SetParent(transform, false);
            root.transform.position = new Vector3(spawn.X / 100f, spawn.Y / 100f, spawn.Z / 100f);
            root.transform.rotation = Quaternion.Euler(0f, spawn.Yaw / 65535f * 360f, 0f);

            foreach (var controller in root.GetComponentsInChildren<CharacterController>(true))
                controller.enabled = false;
            foreach (var agent in root.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true))
                agent.enabled = false;
            foreach (var ai in root.GetComponentsInChildren<AICharacterController>(true))
                ai.enabled = false;
            // Reference mod's MakeReplicaPassive only disables NavMeshAgent and
            // AICharacterController; leave CharacterController/Rigidbody/Movement scripts
            // untouched so native animation / MagicBlending initialization is not disturbed.

            // Keep Animator and CharacterAnimationControl enabled; only stop root motion.
            // The reference mod does the same (it drives parameters, not the controller).
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            LogAnimatorParameters(root, "aiReplica");

            // Keep all other Behaviours (including CharacterAnimationControl) enabled.
            // The reference mod only disables NavMeshAgent + AICharacterController above.

            // The native model root carries the AI's aim yaw; leaving it stacks on top
            // of the replicated root yaw and makes the body face backwards. The remote
            // player clone (BUG-006) resets it for the same reason.
            var modelRoot = ReadMember<Transform>(character, "modelRoot");
            if (modelRoot != null)
            {
                var modelYaw = modelRoot.localEulerAngles.y;
                modelRoot.localRotation = Quaternion.identity;
                _aiReplicaModelRoots[spawn.AiId] = modelRoot;
                Log($"[visual] replica modelRoot reset ai={spawn.AiId} yaw={modelYaw:F1}");
            }

            var characterModel = ReadMember<CharacterModel>(character, "characterModel");
            if (characterModel != null)
                _aiReplicaModels[spawn.AiId] = characterModel.transform;

            var catalogEntry = FindMonster(spawn.MonsterId);
            var team = character.Team;
            var hasCatalogTeam = false;
            Teams catalogTeam = team;
            if (catalogEntry != null && Enum.TryParse<Teams>(catalogEntry.Team, true, out var parsedTeam))
            {
                catalogTeam = parsedTeam;
                hasCatalogTeam = true;
            }
            if (hasCatalogTeam)
                character.SetTeam(catalogTeam);

            var health = character.Health;
            if (health != null)
            {
                health.autoInit = false;
                health.showHealthBar = true;
                if (hasCatalogTeam)
                    health.team = catalogTeam;
                health.SetHealth(Mathf.Max(1f, spawn.Hp));
            }
            Log($"[catalog] replica team monster={spawn.MonsterId} " +
                $"catalog={(catalogEntry != null ? catalogEntry.Team : "<missing>")} " +
                $"applied={(hasCatalogTeam ? catalogTeam.ToString() : character.Team.ToString())}");

            // Prefer the host-authored v6 loadout; fall back to the local preset's
            // first weapon only until SAiLoadout arrives.
            try
            {
                if (!ApplyHostAiLoadoutV7(spawn.AiId, character) &&
                    !ApplyHostAiLoadout(spawn.AiId, character))
                {
                    var switched = character.SwitchToFirstAvailableWeapon();
                    var hold = character.CurrentHoldItemAgent;
                    Log($"[visual] replica weapon ai={spawn.AiId} switched={switched} " +
                        $"hold={(hold != null ? hold.name : "<null>")}");
                }
            }
            catch (Exception ex)
            {
                Log("[visual] replica weapon failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            var receiver = character.mainDamageReceiver;
            if (receiver != null)
            {
                receiver.health = health;
                receiver.useSimpleHealth = false;
                var damageLayer = LayerMask.NameToLayer("DamageReceiver");
                if (damageLayer >= 0)
                    receiver.gameObject.layer = damageLayer;
            }

            if (root.GetComponent<RemoteAIReplicaTag>() == null)
                root.AddComponent<RemoteAIReplicaTag>();

            var localHp = health != null ? health.CurrentHealth : -1f;
            var localMax = health != null ? health.MaxHealth : -1f;
            Log($"[gatea] A11 replica ready ai={spawn.AiId} root={root.name} " +
                $"model={root.name} spawnHp={spawn.Hp} localHp={localHp:F0} localMax={localMax:F0} " +
                $"colliders={root.GetComponentsInChildren<Collider>(true).Length} " +
                $"character={character != null} health={(health != null)}");

            // Reference mod waits a frame while the replica is inactive; activation is what
            // runs native Awake/Start/OnEnable (Animator, CharacterAnimationControl,
            // MagicBlending) in the intended order.
            StartCoroutine(ActivateReplicaNextFrame(root, spawn.AiId));
            return root;
        }

        private System.Collections.IEnumerator ActivateReplicaNextFrame(GameObject root, uint aiId)
        {
            yield return null;
            yield return null;
            if (root == null)
                yield break;
            root.SetActive(true);
            Log($"[visual] replica activated ai={aiId}");
        }
    }

    /// <summary>Marks a passively created native AI replica; local damage is suppressed.</summary>
    internal sealed class RemoteAIReplicaTag : MonoBehaviour
    {
    }
}
