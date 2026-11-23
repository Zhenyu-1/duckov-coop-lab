using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Cysharp.Threading.Tasks;
using Duckov.Utilities;
using DuckovCoop.Client;
using Saves;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private float _yawOffset;
        private ushort _lastRemoteYaw;
        private static readonly Color DefaultDuckBodyColor = new Color(0.96f, 0.80f, 0.25f, 1f);
        private readonly Dictionary<ushort, GameObject> _remotes = new Dictionary<ushort, GameObject>();
        private readonly List<ushort> _staleRemotes = new List<ushort>();
        private readonly HashSet<ushort> _seenPlayers = new HashSet<ushort>();
        private readonly Dictionary<ushort, float> _remoteSeenAt = new Dictionary<ushort, float>();
        // D-3: TryAttachCharacterModel runs once when the mirror is created. If the local
        // player does not exist yet (loading window) the mirror stays a capsule forever, so
        // retry at 0.5s while Main is available.
        private readonly Dictionary<ushort, float> _remoteModelRetryAt =
            new Dictionary<ushort, float>();
        // T6: play a short fall before removing a dead remote player. The snapshot's alive
        // bit is the single source of the transition; corpse containers stay server-authoritative.
        private readonly HashSet<ushort> _remoteDeathAnimating = new HashSet<ushort>();
        private readonly Dictionary<ushort, bool> _remoteWasAlive = new Dictionary<ushort, bool>();

        /// <summary>
        /// Contract 2.16.0: the server's max HP per player, taken from
        /// <c>PlayerSnapshot.maxHp</c>. The mirror body is a clone of OUR character, so its
        /// <c>Health.MaxHealth</c> is our own value; friendly bars must use this instead.
        /// </summary>
        internal static readonly Dictionary<ushort, ushort> RemoteMaxHp =
            new Dictionary<ushort, ushort>();

        // Snapshots arrive on an unreliable channel; a single dropped packet must not
        // destroy and rebuild the mirror (that re-creates the clone and drops its gear).
        private const float RemoteGraceSeconds = 2f;

        // Stable marker for "this object belongs to a remote player mirror"; the friendly
        // health-bar tint and other presentation code match on it.
        internal const string RemoteRootPrefix = "DuckovCoop_Remote_";
        private void UpdateRemotePlayers(Snapshot snapshot)
        {
            if (snapshot == null)
                return;

            _seenPlayers.Clear();
            for (var i = 0; i < snapshot.Players.Length; i++)
            {
                var player = snapshot.Players[i];
                if (player.PlayerId == _client.PlayerId)
                    continue;

                // T6: detect the alive 1 -> 0 transition before visibility filtering, but only
                // animate when a mirror actually exists (never seen / already gone: skip).
                var alive = (player.Flags & 0x01) != 0;
                _remoteWasAlive.TryGetValue(player.PlayerId, out var wasAlive);
                if (wasAlive && !alive && _remotes.ContainsKey(player.PlayerId))
                {
                    _remoteWasAlive[player.PlayerId] = alive;
                    StartCoroutine(PlayRemoteDeathAndRemove(player.PlayerId));
                    continue;
                }
                _remoteWasAlive[player.PlayerId] = alive;

                // dead / left players (alive bit clear) disappear from view
                if (!alive)
                    continue;

                // extracted players stay out of the raid even though they are still alive
                if ((player.Flags & 0x04) != 0)
                    continue;

                // nobody has a position before their first input / spawn report
                if ((player.Flags & 0x02) == 0)
                    continue;

                // a player is rendered when both sides agree on the scene, and only while
                // that player is actually an active raid participant
                var localScene = _inRaidScene ? SceneManager.GetActiveScene().name : string.Empty;
                var remoteInRaid = (player.Flags & 0x08) != 0;
                if (_inRaidScene)
                {
                    if (!remoteInRaid)
                        continue;
                    if (!string.Equals(player.Scene ?? string.Empty, localScene, StringComparison.Ordinal))
                        continue;
                }
                else
                {
                    if (remoteInRaid)
                        continue;
                    if (!string.IsNullOrEmpty(player.Scene))
                        continue;
                    // do not draw lobby avatars while we are standing in a level scene
                    // (e.g. during the transition into a new raid)
                    if (SceneManager.GetActiveScene().name.StartsWith("Level_"))
                        continue;
                }

                _seenPlayers.Add(player.PlayerId);
                _remoteSeenAt[player.PlayerId] = Time.unscaledTime;

                _lastRemoteYaw = player.Yaw;
                var yawDegrees = player.Yaw / 65535f * 360f + _yawOffset;
                var created = false;

                if (!_remotes.TryGetValue(player.PlayerId, out var remote) || remote == null)
                {
                    remote = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    remote.name = RemoteRootPrefix + player.PlayerId;
                    remote.transform.SetParent(transform, false);
                    remote.transform.localScale = new Vector3(0.8f, 1.1f, 0.8f);

                    var main = CharacterMainControl.Main;
                    if (main != null)
                        remote.layer = main.gameObject.layer;

                    var collider = remote.GetComponent<Collider>();
                    if (collider != null)
                        UnityEngine.Object.Destroy(collider);

                    var renderer = remote.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                        if (shader != null)
                        {
                            renderer.material = new Material(shader);
                            renderer.material.color = new Color(0.1f, 0.9f, 1f, 1f);
                        }
                        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        renderer.receiveShadows = false;
                    }

                    // small nose marker so the remote yaw is visible
                    var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    nose.name = "nose";
                    nose.transform.SetParent(remote.transform, false);
                    nose.transform.localPosition = new Vector3(0f, 0.35f, 0.45f);
                    nose.transform.localScale = new Vector3(0.18f, 0.18f, 0.55f);
                    UnityEngine.Object.Destroy(nose.GetComponent<Collider>());
                    var noseRenderer = nose.GetComponent<Renderer>();
                    if (noseRenderer != null)
                    {
                        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                        if (shader != null)
                        {
                            noseRenderer.material = new Material(shader);
                            noseRenderer.material.color = new Color(1f, 0.6f, 0.1f, 1f);
                        }
                        noseRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    }

                    _remotes[player.PlayerId] = remote;
                    created = true;
                    // A rebuilt mirror is a fresh, bare clone: invalidate the per-player
                    // presentation caches so V8 loadout/face/state are re-applied below.
                    // Without this the signature checks skip the re-apply and the mirror
                    // stays undressed (BUG-2026-11-12: "remote lost all equipment").
                    ResetRemotePresentationCaches(player.PlayerId);
                    var modelOk = TryAttachCharacterModel(remote);
                    if (modelOk)
                    {
                        MarkMirrorModel(remote);
                        TagMirrorPlayer(remote, player.PlayerId);
                    }
                    EnsureMirrorAimPose(player.PlayerId, remote);
                    var spawnPos = new Vector3(player.X / 100f, player.Y / 100f, player.Z / 100f);
                    var localPos = CharacterMainControl.Main != null ? CharacterMainControl.Main.transform.position : Vector3.zero;
                    Log($"remote player appeared: id={player.PlayerId} layer={remote.layer} model={modelOk} " +
                        $"pos={spawnPos} yawRaw={player.Yaw} yaw={yawDegrees:F1} local={localPos} " +
                        $"dist={Vector3.Distance(spawnPos, localPos):F2}");
                }

                if (!created &&
                    remote.GetComponentInChildren<RemotePlayerMirrorTag>(true) == null &&
                    LocalMainCharacter() != null)
                {
                    _remoteModelRetryAt.TryGetValue(player.PlayerId, out var retryAt);
                    if (Time.unscaledTime >= retryAt)
                    {
                        _remoteModelRetryAt[player.PlayerId] = Time.unscaledTime + 0.5f;
                        if (TryAttachCharacterModel(remote))
                        {
                            MarkMirrorModel(remote);
                            TagMirrorPlayer(remote, player.PlayerId);
                            ResetRemotePresentationCaches(player.PlayerId);
                            EnsureMirrorAimPose(player.PlayerId, remote);
                            Log($"[v8] model attached on retry id={player.PlayerId}");
                        }
                    }
                }

                // Contract 2.16.0: cache the server's max HP (and expose the player id on the
                // mirror marker) so the friendly health bar renders hp / serverMax.
                if (player.MaxHp > 0)
                {
                    RemoteMaxHp[player.PlayerId] = player.MaxHp;
                    TagMirrorPlayer(remote, player.PlayerId);
                }

                // D5: remote players' HP is only carried by the snapshot; mirror it onto
                // the cloned model so the health bar reflects the server value.
                if (player.Hp > 0 || !_inRaidScene)
                {
                    // Resolve through the explicit marker: the remote root can also contain
                    // the AI target proxy, which is a full CharacterMainControl with its own
                    // (uninitialised, 1000000 HP) Health. GetComponentInChildren picked
                    // whichever came first, which produced a second red health bar on hosts.
                    var remoteHealth = MirrorHealth(remote);
                    if (remoteHealth != null)
                    {
                        if (!remoteHealth.showHealthBar)
                            remoteHealth.showHealthBar = true;
                        // The base is not a combat scene and the server deliberately keeps the
                        // extracted player's last raid HP until the next raid. Do not mirror
                        // that stale low value into the lobby: show the friendly bar full.
                        var hp = _inRaidScene ? player.Hp : remoteHealth.MaxHealth;
                        if (hp > 0f)
                        {
                            if (!_inRaidScene && remoteHealth.CurrentHealth < hp - 0.5f)
                                Log($"[mirror] base health reset id={player.PlayerId} " +
                                    $"{remoteHealth.CurrentHealth:F0}->{hp:F0}");
                            remoteHealth.SetHealth(hp);
                        }
                    }
                }

                var target = new Vector3(player.X / 100f, player.Y / 100f, player.Z / 100f);
                ApplyRemoteLoadout(player.PlayerId, remote);
                if (created)
                {
                    // BUG-006: a rebuilt clone must start at the snapshot pose, not at
                    // identity/old rotation, otherwise reconnect shows a ~90° offset.
                    remote.transform.position = target;
                    remote.transform.rotation = Quaternion.Euler(0f, yawDegrees, 0f);
                }
                else
                {
                    remote.transform.position = Vector3.Lerp(
                        remote.transform.position, target, 1f - Mathf.Exp(-12f * Time.deltaTime));
                    remote.transform.rotation = Quaternion.Slerp(
                        remote.transform.rotation,
                        Quaternion.Euler(0f, yawDegrees, 0f),
                        1f - Mathf.Exp(-10f * Time.deltaTime));
                }
                SetMirrorYaw(remote, yawDegrees);
                SetMirrorTarget(remote, target);

            }

            _staleRemotes.Clear();
            foreach (var pair in _remotes)
            {
                if (_seenPlayers.Contains(pair.Key))
                    continue;
                if (_remoteDeathAnimating.Contains(pair.Key))
                    continue; // the death coroutine owns the removal
                if (_remoteSeenAt.TryGetValue(pair.Key, out var seenAt) &&
                    Time.unscaledTime - seenAt < RemoteGraceSeconds)
                    continue;
                _staleRemotes.Add(pair.Key);
            }

            foreach (var id in _staleRemotes)
            {
                if (_remotes.TryGetValue(id, out var remote) && remote != null)
                    UnityEngine.Object.Destroy(remote);
                _remotes.Remove(id);
                _playerMirrorLastPositions.Remove(id);
                _remoteSeenAt.Remove(id);
                _remoteModelRetryAt.Remove(id);
                Log($"remote player left: id={id}");
            }
        }

        /// <summary>
        /// T6: 0.55s fall (tilt 90° forward, sink 0.45m) before removing the mirror. Exits
        /// early if the mirror is rebuilt or the player respawns mid-fall.
        /// </summary>
        private System.Collections.IEnumerator PlayRemoteDeathAndRemove(ushort playerId)
        {
            if (!_remotes.TryGetValue(playerId, out var remote) || remote == null)
                yield break;

            _remoteDeathAnimating.Add(playerId);
            var startPosition = remote.transform.position;
            var startRotation = remote.transform.rotation;
            var elapsed = 0f;
            const float duration = 0.55f;
            Log($"[v8] remote death id={playerId} fall={duration:F2}s");

            while (elapsed < duration)
            {
                if (remote == null ||
                    !_remotes.TryGetValue(playerId, out var current) ||
                    !ReferenceEquals(current, remote))
                {
                    _remoteDeathAnimating.Remove(playerId);
                    yield break;
                }
                if (_remoteWasAlive.TryGetValue(playerId, out var aliveNow) && aliveNow)
                {
                    // Respawned mid-fall: restore the pose and let the normal update win.
                    remote.transform.position = startPosition;
                    remote.transform.rotation = startRotation;
                    _remoteDeathAnimating.Remove(playerId);
                    yield break;
                }

                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / duration);
                remote.transform.position = startPosition + Vector3.down * (0.45f * t);
                remote.transform.rotation = startRotation * Quaternion.Euler(90f * t, 0f, 0f);
                yield return null;
            }

            if (remote != null)
                UnityEngine.Object.Destroy(remote);
            _remotes.Remove(playerId);
            _playerMirrorLastPositions.Remove(playerId);
            _remoteSeenAt.Remove(playerId);
            _remoteModelRetryAt.Remove(playerId);
            _remoteDeathAnimating.Remove(playerId);
            _remoteWasAlive[playerId] = false;
            ClearPlayerV8(playerId);
            ClearRemoteLoadout(playerId);
            Log($"[v8] remote death removed id={playerId}");
        }

        /// <summary>
        /// CharacterMainControl.Main can be null for a client after a scene load while the
        /// actual local character is LevelManager.MainCharacter (the V8 sampler already uses
        /// this fallback). Mirrors must use the same resolution or their CharacterItem is
        /// created without a type and gets cleaned up by the game.
        /// </summary>
        private static CharacterMainControl LocalMainCharacter()
        {
            var main = CharacterMainControl.Main;
            if (main == null && LevelManager.Instance != null)
                main = LevelManager.Instance.MainCharacter;
            return main;
        }

        /// <summary>
        /// Health of the visible mirror body. The remote root can also contain the AI target
        /// proxy — a full CharacterMainControl with its own (uninitialised) Health — so
        /// GetComponentInChildren&lt;Health&gt; picked whichever came first and could return the
        /// proxy's 1000000-HP bar instead of the player's.
        /// </summary>
        internal static Health MirrorHealth(GameObject remote)
        {
            if (remote == null)
                return null;
            var tag = remote.GetComponentInChildren<RemotePlayerMirrorTag>(true);
            return tag != null ? tag.GetComponentInChildren<Health>(true) : null;
        }

        internal static void MarkMirrorModel(GameObject remote)
        {
            if (remote == null)
                return;
            CharacterMainControl best = null;
            foreach (var control in remote.GetComponentsInChildren<CharacterMainControl>(true))
            {
                if (control == null)
                    continue;
                if (control.gameObject.name == "model")
                {
                    best = control;
                    break;
                }
                if (best == null)
                    best = control;
            }
            if (best == null)
                return;
            if (best.GetComponent<RemotePlayerMirrorTag>() == null)
                best.gameObject.AddComponent<RemotePlayerMirrorTag>();
        }

        /// <summary>
        /// Contract 2.16.0: the health bar resolves "which player is this mirror" through the
        /// marker, so the marker must carry the id (the root name is not a contract).
        /// </summary>
        internal static void TagMirrorPlayer(GameObject remote, ushort playerId)
        {
            if (remote == null)
                return;
            var tag = remote.GetComponentInChildren<RemotePlayerMirrorTag>(true);
            if (tag != null)
                tag.PlayerId = playerId;
        }

        private static void SetMirrorYaw(GameObject remote, float yaw)
        {
            var control = remote.GetComponentInChildren<CharacterMainControl>(true);
            if (control == null)
                return;
            var follow = control.GetComponent<MirrorYawFollow>();
            if (follow == null)
                follow = control.gameObject.AddComponent<MirrorYawFollow>();
            follow.SetYaw(yaw);
        }

        private static void SetMirrorTarget(GameObject remote, Vector3 target)
        {
            var control = remote.GetComponentInChildren<CharacterMainControl>(true);
            if (control == null)
                return;
            var follow = control.GetComponent<MirrorYawFollow>();
            if (follow == null)
                follow = control.gameObject.AddComponent<MirrorYawFollow>();
            follow.SetTarget(target);
        }

        private static bool TryCreateNativeRemoteModel(GameObject remote, CharacterMainControl main,
            bool defaultAppearance)
        {
            try
            {
                var level = LevelManager.Instance;
                if (level == null || level.CharacterCreator == null)
                    return false;

                var character = AwaitUniTaskResult(
                    CreateNativeRemoteTask(level, remote, main, defaultAppearance)) as
                    CharacterMainControl;
                if (character == null)
                    return false;

                var go = character.gameObject;
                go.name = "model";
                go.transform.SetParent(remote.transform, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                remote.transform.localScale = Vector3.one;
                go.layer = main != null ? main.gameObject.layer : remote.layer;

                MakeNativePhysicsPassive(go);
                if (defaultAppearance)
                {
                    // No local avatar is ready yet: keep the game's default yellow duck body
                    // and its default face preset instead of a factory-white placeholder.
                    ApplyDefaultFace(character);
                }
                else
                {
                    if (!ReplaceNativeModel(character, main))
                    {
                        UnityEngine.Object.Destroy(go);
                        return false;
                    }
                    CopyNativeMaterials(main, character);
                }
                MakeNativePassive(go);
                ForceNativeAnimation(go);
                character.enabled = true;
                if (character.Health != null)
                    character.Health.autoInit = false;
                ResetNativeModelRotation(character);
                ClearAllItemAgents(go);

                // The mirror root is still the scaled capsule placeholder; the clone branch
                // normally hides it after attaching. Hide it here too or it stays visible
                // around the native character (T7c screenshot).
                var capsuleRenderer = remote.GetComponent<Renderer>();
                if (capsuleRenderer != null)
                    capsuleRenderer.enabled = false;
                var nose = remote.transform.Find("nose");
                if (nose != null)
                    nose.gameObject.SetActive(false);

                Log($"model: native character created item={(character.CharacterItem != null)} " +
                    $"type={(character.CharacterItem != null ? character.CharacterItem.TypeID : -1)} " +
                    $"appearance={(defaultAppearance ? "default-duck" : "local-clone")}");
                return true;
            }
            catch (Exception ex)
            {
                Log("model: native create failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static void ForceNativeAnimation(GameObject go)
        {
            var animators = 0;
            foreach (var animator in go.GetComponentsInChildren<Animator>(true))
            {
                if (animator == null)
                    continue;
                animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.updateMode = AnimatorUpdateMode.Normal;
                animator.applyRootMotion = false;
                animators++;
            }
            var controls = 0;
            foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null)
                    continue;
                if (!behaviour.GetType().Name.StartsWith("CharacterAnimation",
                        StringComparison.Ordinal))
                    continue;
                behaviour.enabled = true;
                controls++;
            }
            Log($"model: native animation forced animators={animators} controls={controls}");
        }

        private static void MakeNativePassive(GameObject go)
        {
            // Reference mod behaviour: keep CharacterMainControl and all animation scripts
            // so the native Animator can pose the body; only stop movement/AI drivers.
            foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null)
                    continue;
                var name = behaviour.GetType().Name;
                if (name.Contains("Locomotion") || name.Contains("Motor") ||
                    name.Contains("CharacterInput") || name.Contains("PlayerInput") ||
                    name.Contains("AICharacterController"))
                    behaviour.enabled = false;
            }
            Log("model: native animation scripts kept, movement disabled");
        }

        private static void StripNativeBehaviors(GameObject go)
        {
            var keep = new HashSet<string>
            {
                "CharacterMainControl",
                "CharacterEquipmentController",
                "ItemAgentHolder",
                "CharacterModel",
                "Health",
                "DamageReceiver",
                // M2-v12: keep the victim FX listener; it only reacts to Health.OnHurtEvent
                // and never runs the native damage path on a mirror.
                "HurtVisual",
            };
            foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || keep.Contains(behaviour.GetType().Name))
                    continue;
                // Animator layer/aim/attack weights are driven by CharacterAnimationControl;
                // stripping it leaves HandState/GunReady with no consumer (T7c hold pose).
                if (behaviour.GetType().Name.StartsWith("CharacterAnimation",
                        StringComparison.Ordinal))
                    continue;
                UnityEngine.Object.Destroy(behaviour);
            }
            foreach (var collider in go.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.Destroy(collider);
            foreach (var camera in go.GetComponentsInChildren<Camera>(true))
                UnityEngine.Object.Destroy(camera);
            foreach (var listener in go.GetComponentsInChildren<AudioListener>(true))
                UnityEngine.Object.Destroy(listener);
            foreach (var controller in go.GetComponentsInChildren<CharacterController>(true))
                controller.enabled = false;
            foreach (var body in go.GetComponentsInChildren<Rigidbody>(true))
            {
                body.isKinematic = true;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            Log("model: native behaviours stripped");
        }

        private static void ResetNativeModelRotation(CharacterMainControl character)
        {
            if (character.modelRoot != null)
                character.modelRoot.localRotation = Quaternion.identity;
            if (character.characterModel != null)
                character.characterModel.transform.localRotation = Quaternion.identity;
        }

        private static bool ApplyDefaultFace(CharacterMainControl character)
        {
            try
            {
                var preset = GameplayDataSettings.CustomFaceData != null
                    ? GameplayDataSettings.CustomFaceData.DefaultPreset
                    : null;
                if (preset == null)
                {
                    Log("model: default face preset missing");
                    return false;
                }
                if (character == null || character.characterModel == null ||
                    character.characterModel.CustomFace == null)
                {
                    // CharacterModel/CustomFace wakes up after the character factory call.
                    // Report not-ready so the caller retries instead of caching "applied".
                    return false;
                }
                character.characterModel.SetFaceFromPreset(preset);
                var color = preset.settings.headSetting.mainColor;
                // The shipped default preset leaves mainColor at white/transparent (the game
                // normally gets the duck yellow from the saved/local face data). Use a solid
                // yellow fallback instead of drawing a white body.
                if (color.a <= 0.01f ||
                    (color.r > 0.98f && color.g > 0.98f && color.b > 0.98f))
                {
                    color = DefaultDuckBodyColor;
                    Log("model: default preset color empty; using yellow fallback");
                }
                color.a = 1f;
                ApplyBodyTint(character, color);
                ApplyWingTint(character, color);
                Log($"model: default duck face applied color={color}");
                return true;
            }
            catch (Exception ex)
            {
                Log("model: default face failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// CustomFaceInstance.SetMainColor only walks its serialized mainRenderers; on a
        /// factory-created default model that list can be empty, leaving the body white while
        /// custom parts (beak/feet) already have their colours. Tint DuckBody explicitly.
        /// </summary>
        private static void ApplyBodyTint(CharacterMainControl character, Color color)
        {
            try
            {
                var root = character.characterModel != null
                    ? character.characterModel.gameObject
                    : character.gameObject;
                SkinnedMeshRenderer body = null;
                foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
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
                {
                    Log("model: default body tint skipped (no skinned body)");
                    return;
                }

                var material = body.sharedMaterial;
                var block = new MaterialPropertyBlock();
                body.GetPropertyBlock(block);
                var keys = material != null && material.HasProperty("_Tint")
                    ? new[] { "_Tint" }
                    : new[] { "_BaseColor", "_Color", "_Tint" };
                var applied = 0;
                foreach (var key in keys)
                {
                    if (material != null && !material.HasProperty(key))
                        continue;
                    block.SetColor(key, color);
                    applied++;
                }
                body.SetPropertyBlock(block);
                Log($"model: default body tint={color} renderer={body.name} keys={applied}");
            }
            catch (Exception ex)
            {
                Log("model: default body tint failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// The duck's hands are separate wing CustomFaceParts, not DuckBody. Their default
        /// preset colour can also be empty/white, so tint them with the same body colour.
        /// </summary>
        private static void ApplyWingTint(CharacterMainControl character, Color color)
        {
            try
            {
                var custom = character != null && character.characterModel != null
                    ? character.characterModel.CustomFace
                    : null;
                if (custom == null)
                    return;
                var tinted = 0;
                foreach (var utility in new[] { custom.wingLPart, custom.wingRPart })
                {
                    var part = utility != null ? utility.PartInstance : null;
                    if (part == null || part.customColorRenderers == null)
                        continue;
                    var block = new MaterialPropertyBlock();
                    block.SetColor(part.customColorKey, color);
                    foreach (var renderer in part.customColorRenderers)
                    {
                        if (renderer == null)
                            continue;
                        renderer.SetPropertyBlock(block);
                        tinted++;
                    }
                }
                if (tinted > 0)
                    Log($"model: default wing/hand tint={color} renderers={tinted}");
            }
            catch (Exception ex)
            {
                Log("model: default wing tint failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool ReplaceNativeModel(CharacterMainControl character,
            CharacterMainControl main)
        {
            try
            {
                var localModel = main.characterModel;
                if (localModel == null)
                {
                    Log("model: local characterModel null");
                    return false;
                }

                var clone = UnityEngine.Object.Instantiate(localModel.gameObject,
                    character.transform, false);
                clone.name = localModel.gameObject.name;
                var model = clone.GetComponent<CharacterModel>();
                if (model == null)
                {
                    Log("model: local model has no CharacterModel");
                    return false;
                }

                // SetCharacterModel destroys the factory's default (white) model, parents the
                // clone under modelRoot, wires sockets/head collider and re-fires slot events.
                character.SetCharacterModel(model);
                Log($"model: native model replaced renderers=" +
                    $"{clone.GetComponentsInChildren<Renderer>(true).Length}");
                return true;
            }
            catch (Exception ex)
            {
                Log("model: native model replace failed: " + ex.GetType().Name + ": " +
                    ex.Message);
                return false;
            }
        }

        private static ItemStatsSystem.Item CloneLocalCharacterItem(CharacterMainControl main)
        {
            try
            {
                var template = main != null ? main.CharacterItem : null;
                if (template == null)
                {
                    Log("model: local CharacterItem null for clone");
                    return null;
                }

                var go = UnityEngine.Object.Instantiate(template.gameObject);
                go.name = "mirror_character_item";
                var clone = go.GetComponent<ItemStatsSystem.Item>();
                if (clone == null)
                {
                    UnityEngine.Object.Destroy(go);
                    return null;
                }

                // Keep the prefab slot structure but drop the local player's gear so the
                // V8 loadout is the only thing plugged into the mirror.
                var content = typeof(ItemStatsSystem.Items.Slot).GetField("content",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var slots = clone.Slots;
                if (content != null && slots != null)
                {
                    foreach (var slot in slots)
                    {
                        if (slot != null)
                            content.SetValue(slot, null);
                    }
                }
                Log($"model: cloned local character item type={clone.TypeID} " +
                    $"slots={(slots != null ? slots.Count : 0)}");
                return clone;
            }
            catch (Exception ex)
            {
                Log("model: cloned local character item failed: " + ex.GetType().Name + ": " +
                    ex.Message);
                return null;
            }
        }

        private static async UniTask<CharacterMainControl> CreateNativeRemoteTask(
            LevelManager level, GameObject remote, CharacterMainControl main, bool defaultAppearance)
        {
            ItemStatsSystem.Item item = null;
            try
            {
                item = await ItemSavesUtilities.LoadItem("MainCharacterItemData");
            }
            catch
            {
                // no save (raid) - fall through to the default character item
            }
            Log($"model: native save item={(item != null)} " +
                $"({(defaultAppearance ? "default-duck" : "local-clone")})");

            if (item == null)
            {
                var type = GameplayDataSettings.ItemAssets != null
                    ? GameplayDataSettings.ItemAssets.DefaultCharacterItemTypeID
                    : 0;
                item = await ItemStatsSystem.ItemAssetsCollection.InstantiateAsync(type);
                Log($"model: native default item={(item != null)} " +
                    $"type={(item != null ? item.TypeID : -1)}");
            }

            // InstantiateAsync(0) returns a bare FallbackItem that the game cleans up.
            // A clone of the local player's real CharacterItem keeps the prefab slots.
            if ((item == null || item.TypeID == 0) && main != null)
            {
                var cloned = CloneLocalCharacterItem(main);
                if (cloned != null)
                    item = cloned;
            }

            if (item == null)
            {
                Log("model: native item null");
                return null;
            }

            CharacterModel model = null;
            if (!defaultAppearance)
            {
                var field = typeof(LevelManager).GetField("characterModel",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                model = field?.GetValue(level) as CharacterModel;
            }
            if (model == null)
                model = GameplayDataSettings.Prefabs.DefaultCharacterModel;
            if (model == null)
            {
                Log("model: native model null");
                return null;
            }

            return await level.CharacterCreator.CreateCharacter(item, model,
                remote.transform.position, remote.transform.rotation);
        }

        private static void MakeNativePhysicsPassive(GameObject go)
        {
            foreach (var agent in go.GetComponentsInChildren<NavMeshAgent>(true))
                agent.enabled = false;
            foreach (var controller in go.GetComponentsInChildren<CharacterController>(true))
                controller.enabled = false;
            foreach (var body in go.GetComponentsInChildren<Rigidbody>(true))
            {
                body.isKinematic = true;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            var animator = go.GetComponentInChildren<Animator>(true);
            if (animator != null)
                animator.applyRootMotion = false;
        }

        private static void CopyNativeMaterials(CharacterMainControl source,
            CharacterMainControl target)
        {
            var sourceRoot = source.characterModel != null
                ? source.characterModel.transform
                : source.transform;
            var targetRoot = target.characterModel != null
                ? target.characterModel.transform
                : target.transform;
            var sourceRenderers = source.GetComponentsInChildren<Renderer>(true);
            var targetRenderers = target.GetComponentsInChildren<Renderer>(true);
            var byPath = new Dictionary<string, Renderer>(targetRenderers.Length);
            foreach (var renderer in targetRenderers)
            {
                var path = RendererPath(renderer.transform, targetRoot);
                if (!byPath.ContainsKey(path))
                    byPath[path] = renderer;
            }

            var matched = 0;
            foreach (var renderer in sourceRenderers)
            {
                if (!byPath.TryGetValue(RendererPath(renderer.transform, sourceRoot),
                        out var targetRenderer))
                    continue;
                targetRenderer.sharedMaterials = renderer.sharedMaterials;
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                targetRenderer.SetPropertyBlock(block);
                matched++;
            }
            Log($"model materials native main={sourceRenderers.Length} " +
                $"target={targetRenderers.Length} matched={matched} " +
                $"unmatched={sourceRenderers.Length - matched}");
        }

        private static bool TryAttachCharacterModel(GameObject remote)
        {
            try
            {
                // Friendly models intentionally use the game's default yellow duck instead
                // of cloning the local player's customized character model. This avoids
                // white factory placeholders during base loading and keeps every remote
                // body visually stable; the remote player's V8/base loadout and face still
                // layer on top.
                var main = LocalMainCharacter();
                if (TryCreateNativeRemoteModel(remote, main, true))
                    return true;
                Log("model: default yellow duck creation failed; retrying");
                return false;
            }
            catch (Exception ex)
            {
                Log("model: default yellow duck creation failed: " + ex);
                return false;
            }
        }

        private static void ClearAllItemAgents(GameObject root)
        {
            try
            {
                if (root == null)
                    return;
                foreach (var agent in root.GetComponentsInChildren<ItemStatsSystem.ItemAgent>(true))
                {
                    if (agent != null)
                        UnityEngine.Object.Destroy(agent.gameObject);
                }
            }
            catch (Exception ex)
            {
                Log("model: clear item agents failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ClearEquipmentAgents(CharacterMainControl control)
        {
            try
            {
                var model = control != null ? control.characterModel : null;
                if (model == null)
                    return;
                var sockets = new[]
                {
                    model.ArmorSocket,
                    model.HelmatSocket,
                    model.FaceMaskSocket,
                    model.BackpackSocket,
                };
                foreach (var socket in sockets)
                {
                    if (socket == null)
                        continue;
                    foreach (var agent in socket.GetComponentsInChildren<ItemStatsSystem.ItemAgent>(true))
                    {
                        if (agent != null)
                            UnityEngine.Object.Destroy(agent.gameObject);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("model: clear equipment failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Names from <paramref name="root"/> down to <paramref name="t"/>, '/' separated.</summary>
        private static string RendererPath(Transform t, Transform root)
        {
            if (t == null)
                return string.Empty;
            var parts = new List<string>(8);
            var current = t;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private void RemoveRemotePlayer(ushort playerId)
        {
            if (_remotes.TryGetValue(playerId, out var remote) && remote != null)
                UnityEngine.Object.Destroy(remote);

            _remotes.Remove(playerId);
            _playerMirrorLastPositions.Remove(playerId);
            _remoteSeenAt.Remove(playerId);
            _remoteModelRetryAt.Remove(playerId);
            _remoteDeathAnimating.Remove(playerId);
            _remoteWasAlive.Remove(playerId);
            RemoteMaxHp.Remove(playerId);
            ClearRemoteLoadout(playerId);
            ClearPlayerV8(playerId);
            _seenPlayers.Remove(playerId);
            Log($"remote player removed: id={playerId}");
        }

        private void ClearRemotePlayers(string reason)
        {
            if (_remotes.Count == 0 && _seenPlayers.Count == 0)
                return;

            var count = _remotes.Count;
            foreach (var pair in _remotes)
            {
                if (pair.Value != null)
                    UnityEngine.Object.Destroy(pair.Value);
                ClearPlayerV8(pair.Key);
            }

            _remotes.Clear();
            _seenPlayers.Clear();
            _remoteSeenAt.Clear();
            _playerMirrorLastPositions.Clear();
            _remoteModelRetryAt.Clear();
            _remoteDeathAnimating.Clear();
            _remoteWasAlive.Clear();
            _remoteLoadoutSignature.Clear();
            RemoteMaxHp.Clear();
            Log($"remote players cleared: {reason} (count={count})");
        }

    }

    /// <summary>
    /// Marks the visible body of a player mirror. Used to resolve "the player's Health"
    /// deterministically: the same remote root also hosts the AI target proxy, which is a
    /// full CharacterMainControl with its own uninitialised Health.
    /// </summary>
    internal sealed class RemotePlayerMirrorTag : MonoBehaviour
    {
        /// <summary>Contract 2.16.0: the coop player id this mirror body belongs to.</summary>
        internal ushort PlayerId;
    }
}
