using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// Local/approximate presentation layer. It never settles damage; it only
    /// drives replica animation and gives the shooter provisional hit feedback.
    /// Authoritative hitmarker/HP/effects must come from SCombatEvent later.
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private readonly Dictionary<uint, Vector3> _aiReplicaLastPositions =
            new Dictionary<uint, Vector3>();
        private readonly Dictionary<ushort, Vector3> _playerMirrorLastPositions =
            new Dictionary<ushort, Vector3>();
        private readonly HashSet<uint> _loggedMissingAiState = new HashSet<uint>();
        private readonly HashSet<uint> _loggedReenabledAiAnimator = new HashSet<uint>();
        private static int _provisionalHitCount;
        private readonly Dictionary<string, float> _nextMirrorAnimationLog = new Dictionary<string, float>();
        private readonly Dictionary<string, int> _mirrorAnimationLogCount = new Dictionary<string, int>();

        private void LateUpdate()
        {
            UpdateAiShots();
            UpdatePlayerShots();
            UpdateRemoteProjectiles();
            DriveAiReplicaAnimations();
            DrivePlayerMirrorAnimations();
        }

        private void DriveAiReplicaAnimations()
        {
            if (_aiGhosts.Count == 0)
                return;

            var deltaTime = Time.deltaTime;
            foreach (var pair in _aiGhosts)
            {
                var ghost = pair.Value;
                if (ghost == null || ghost.GetComponent<RemoteAIReplicaTag>() == null)
                    continue;

                var animator = ghost.GetComponentInChildren<Animator>(true);
                if (animator == null)
                    continue;

                if (!animator.enabled)
                {
                    animator.enabled = true;
                    if (_loggedReenabledAiAnimator.Add(pair.Key))
                        Log($"[fx] ai animator re-enabled ai={pair.Key}");
                }
                animator.speed = 1f;

                // D5: when the owner samples animator parameters, drive the replica from
                // them (attack/reload/hand state) instead of the transform delta fallback.
                if (TryGetFreshAiAnim(pair.Key, out var sampledAnim))
                {
                    ApplyAiStateSample(animator, pair.Key);
                    ApplyAiAnimSample(animator, sampledAnim, pair.Key);
                    LogMirrorAnimation("ai", animator);
                    _aiReplicaLastPositions[pair.Key] = ghost.transform.position;
                    continue;
                }

                if (_client != null && !_client.AiStates.ContainsKey(pair.Key) &&
                    _loggedMissingAiState.Add(pair.Key))
                {
                    Log($"[fx] ai state missing ai={pair.Key}; using transform delta only");
                }

                if (!_aiReplicaLastPositions.TryGetValue(pair.Key, out var previous))
                {
                    _aiReplicaLastPositions[pair.Key] = ghost.transform.position;
                    continue;
                }

                ApplyLocomotion(animator, ghost.transform, previous, deltaTime);
                LogMirrorAnimation("ai", animator);
                _aiReplicaLastPositions[pair.Key] = ghost.transform.position;
            }
        }

        private void DrivePlayerMirrorAnimations()
        {
            if (_remotes.Count == 0)
                return;

            var deltaTime = Time.deltaTime;
            foreach (var pair in _remotes)
            {
                var remote = pair.Value;
                if (remote == null)
                    continue;

                var animator = FindBodyAnimator(remote);
                if (animator == null)
                    continue;

                if (!_playerMirrorLastPositions.TryGetValue(pair.Key, out var previous))
                {
                    _playerMirrorLastPositions[pair.Key] = remote.transform.position;
                    continue;
                }

                ApplyPlayerStateSample(animator, pair.Key);
                ApplyLocomotion(animator, remote.transform, previous, deltaTime);
                LogMirrorAnimation("player", animator);
                _playerMirrorLastPositions[pair.Key] = remote.transform.position;
            }
        }

        private static void ApplyLocomotion(Animator animator, Transform root, Vector3 previous, float deltaTime)
        {
            var delta = root.position - previous;
            delta.y = 0f;

            // Remote/teleport rebuilds can produce a huge one-frame delta; do not
            // push that into the blend tree.
            if (delta.sqrMagnitude > 25f)
            {
                SetFloatIfExists(animator, "MoveSpeed", 0f);
                SetFloatIfExists(animator, "Speed", 0f);
                SetFloatIfExists(animator, "MoveDirX", 0f);
                SetFloatIfExists(animator, "MoveDirY", 0f);
                return;
            }

            var speed = deltaTime > 0.0001f ? delta.magnitude / deltaTime : 0f;
            var localDirection = root.InverseTransformDirection(
                delta.sqrMagnitude > 0.000001f ? delta.normalized : Vector3.zero);

            SetFloatIfExists(animator, "MoveSpeed", speed);
            SetFloatIfExists(animator, "Speed", speed);
            SetFloatIfExists(animator, "MoveDirX", localDirection.x);
            SetFloatIfExists(animator, "MoveDirY", localDirection.z);
            SetFloatIfExists(animator, "HorizontalSpeed", speed);
            SetFloatIfExists(animator, "VerticalSpeed", localDirection.z);
        }

        private void LogMirrorAnimation(string kind, Animator animator)
        {
            if (animator == null)
                return;

            _mirrorAnimationLogCount.TryGetValue(kind, out var count);
            if (count >= 16)
                return;

            _nextMirrorAnimationLog.TryGetValue(kind, out var next);
            if (Time.unscaledTime < next)
                return;

            _nextMirrorAnimationLog[kind] = Time.unscaledTime + 1f;
            _mirrorAnimationLogCount[kind] = count + 1;
            try
            {
                var state = animator.GetCurrentAnimatorStateInfo(0);
                var clipText = new StringBuilder();
                foreach (var clipInfo in animator.GetCurrentAnimatorClipInfo(0))
                {
                    if (clipText.Length > 0)
                        clipText.Append(", ");
                    clipText.Append(clipInfo.clip != null ? clipInfo.clip.name : "<null>")
                        .Append(':').Append(clipInfo.weight.ToString("F2"));
                }

                Log($"[fx] anim {kind} enabled={animator.enabled} " +
                    $"speed={animator.GetFloat("MoveSpeed"):F2} " +
                    $"dir=({animator.GetFloat("MoveDirX"):F2},{animator.GetFloat("MoveDirY"):F2}) " +
                    $"state={state.fullPathHash}/{state.shortNameHash} normalized={state.normalizedTime:F2} " +
                    $"transition={animator.IsInTransition(0)} clips={clipText}");
            }
            catch (Exception ex)
            {
                Log($"[fx] anim {kind} log failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal static void LogAnimatorParameters(GameObject root, string kind)
        {
            try
            {
                var animator = root != null ? root.GetComponentInChildren<Animator>(true) : null;
                if (animator == null)
                {
                    Log($"[fx] animator {kind}: none");
                    return;
                }

                var builder = new System.Text.StringBuilder();
                foreach (var parameter in animator.parameters)
                {
                    if (builder.Length > 0)
                        builder.Append(", ");
                    builder.Append(parameter.name).Append(':').Append(parameter.type);
                }

                Log($"[fx] animator {kind}: root={root.name} " +
                    $"avatar={(animator.avatar != null ? animator.avatar.name : "<null>")} " +
                    $"human={animator.isHuman} controller=" +
                    $"{(animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "<null>")} " +
                    $"params={builder}");

                var controller = animator.runtimeAnimatorController;
                if (controller != null && controller.animationClips != null)
                {
                    var clips = new StringBuilder();
                    foreach (var clip in controller.animationClips)
                    {
                        if (clip == null)
                            continue;
                        if (clips.Length > 0)
                            clips.Append(", ");
                        clips.Append(clip.name).Append(':').Append(Animator.StringToHash(clip.name));
                    }
                    Log($"[fx] animator {kind} clips={clips}");
                }
            }
            catch (Exception ex)
            {
                Log($"[fx] animator {kind} dump failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SetFloatIfExists(Animator animator, string name, float value)
        {
            if (animator == null || string.IsNullOrEmpty(name))
                return;

            foreach (var parameter in animator.parameters)
            {
                if (parameter.type != AnimatorControllerParameterType.Float ||
                    !string.Equals(parameter.name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                animator.SetFloat(name, value);
                return;
            }
        }

        private readonly Dictionary<uint, float> _lastLocalHitAt =
            new Dictionary<uint, float>();

        internal void NoteLocalHit(uint aiId)
        {
            if (aiId != 0)
                _lastLocalHitAt[aiId] = Time.unscaledTime;
        }

        /// <summary>
        /// Native HitMarker.OnKill only checks DamageInfo.fromCharacter. A stale player
        /// DamageInfo on an AI that was actually killed by another AI can therefore show a
        /// false kill marker on the local crosshair. Only allow the native AI kill marker
        /// when the local player hit that AI recently.
        /// </summary>
        internal bool AllowNativeKillMarker(DamageInfo info)
        {
            try
            {
                var receiver = info.toDamageReceiver;
                var health = receiver != null
                    ? receiver.GetComponentInParent<Health>()
                    : null;
                if (health == null || health.IsMainCharacterHealth)
                    return true;
                if (!TryResolveAiId(health.gameObject, out var aiId) || aiId == 0)
                    return true;
                return _lastLocalHitAt.TryGetValue(aiId, out var at) &&
                       Time.unscaledTime - at < 3f;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Provisional local hitmarker for Gate A. This is intentionally
        /// prediction-only; the authoritative hitmarker will be driven by
        /// SCombatEvent and this call will be removed/guarded then.
        /// </summary>
        internal static void PlayProvisionalHitFeedback(DamageInfo info)
        {
            try
            {
                var main = CharacterMainControl.Main;
                var target = info.toDamageReceiver;
                if (main == null || target == null ||
                    info.fromCharacter != main ||
                    target.GetComponentInParent<RemoteAIReplicaTag>() == null)
                {
                    return;
                }

                var marker = FindHitMarker();
                if (marker == null)
                    return;

                var onHit = typeof(HitMarker).GetMethod("OnHit",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (onHit == null)
                    return;
                onHit.Invoke(marker, new object[] { info });
                if (_provisionalHitCount++ < 12)
                {
                    Log($"[fx] provisional hitmarker target={target.name} " +
                        $"weapon={info.fromWeaponItemID} explosion={info.isExplosion}");
                }
            }
            catch (Exception ex)
            {
                Log("[fx] provisional hitmarker failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static HitMarker FindHitMarker()
        {
            var markers = Resources.FindObjectsOfTypeAll<HitMarker>();
            foreach (var marker in markers)
            {
                if (marker != null && marker.gameObject.activeInHierarchy)
                    return marker;
            }

            return null;
        }
    }
}
