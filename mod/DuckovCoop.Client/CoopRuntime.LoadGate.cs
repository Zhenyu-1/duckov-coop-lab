using System;
using System.Reflection;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// 2026-11-16: a coop raid needs much more "after load" work than the native loader
    /// expects: the GroundZero map variant pass takes ~8 s (two ~4 s frames), and only then
    /// can the spawn report start the server raid (containers, AI, spawn assign, inventory).
    /// Showing that work as a freeze is wrong; the correct presentation is "loading".
    ///
    /// We reuse the game's own curtain instead of drawing UI:
    ///   - SceneLoader.waitAfterSceneLoaded normally keeps the native black screen up for 1 s
    ///     after the level is initialised. Raid loads raise it so the curtain stays up;
    ///   - BlackScreen.HideAndReturnTask (public static, same call the loader uses) fades it
    ///     out once the world is actually ready, and the field is restored.
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private object _sceneLoaderInstance;
        private FieldInfo _sceneLoaderWaitField;
        private float _savedWaitAfterSceneLoaded = 1f;
        private bool _curtainExtended;
        private bool _curtainAwaitingScene;
        private bool _spawnAnchorApplied;
        private float _loadGateDeadline;
        private float _nextLoadGateLog;
        // 2026-11-16: the overlay held by the native loader only shows the black screen by the
        // time the level is active (SceneLoader hides `content` and `loadingIndicator` before
        // activation). Re-show the native curtain widgets so the wait looks like a loading
        // screen (tips + spinner) instead of a plain black frame.
        private object _curtainContent;
        private object _curtainIndicator;
        private MethodInfo _curtainContentShow;
        private MethodInfo _curtainContentHide;
        private MethodInfo _curtainIndicatorShow;
        private MethodInfo _curtainIndicatorHide;
        private PropertyInfo _loadingCommentProperty;
        private bool _curtainContentShown;

        /// <summary>
        /// True once the map variant has finished (or never applied to this scene). The spawn
        /// report waits for this so the server raid never starts while the map is still being
        /// rewritten underneath the player.
        /// </summary>
        internal bool MapVariantSettled =>
            !_mapVariantEnabled ||
            string.IsNullOrEmpty(_mapVariantSignature) ||
            _mapVariantPass >= 2;

        /// <summary>Called before a raid scene load: keep the native curtain up until we hide it.</summary>
        private void ExtendNativeLoadingCurtain()
        {
            if (_curtainExtended)
                return;
            try
            {
                var type = FindTypeByName("SceneLoader");
                var loader = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                var field = type?.GetField("waitAfterSceneLoaded",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (loader == null || field == null || field.FieldType != typeof(float))
                {
                    Log("[gate] native loading curtain unavailable; load will be uncovered");
                    return;
                }

                _sceneLoaderInstance = loader;
                _sceneLoaderWaitField = field;
                _savedWaitAfterSceneLoaded = (float)field.GetValue(loader);
                CaptureCurtainWidgets(type, loader);
                // Upper bound only: the gate hides the curtain explicitly when ready. Sized to
                // cover the server's party gate, the local variant pass, and a slow second
                // client that is still activating its raid subscene.
                field.SetValue(loader, 45f);
                _curtainExtended = true;
                _curtainContentShown = false;
                // The curtain is extended while the client is still in the base scene: keep it
                // up until the raid scene activates (or this deadline expires).
                _curtainAwaitingScene = true;
                _loadGateDeadline = Time.unscaledTime + 45f;
                Log($"[gate] native loading curtain extended: waitAfter={_savedWaitAfterSceneLoaded:F2}->45 " +
                    $"content={_curtainContent != null} indicator={_curtainIndicator != null}");
            }
            catch (Exception ex)
            {
                Log("[gate] extend curtain failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void CaptureCurtainWidgets(Type sceneLoaderType, object loader)
        {
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var contentField = sceneLoaderType.GetField("content", any);
                var indicatorField = sceneLoaderType.GetField("loadingIndicator", any);
                _curtainContent = contentField?.GetValue(loader);
                _curtainIndicator = indicatorField?.GetValue(loader);
                var fadeType = contentField?.FieldType;
                if (fadeType != null)
                {
                    _curtainContentShow = fadeType.GetMethod("Show", Type.EmptyTypes);
                    _curtainContentHide = fadeType.GetMethod("Hide", Type.EmptyTypes);
                    _curtainIndicatorShow = fadeType.GetMethod("Show", Type.EmptyTypes);
                    _curtainIndicatorHide = fadeType.GetMethod("Hide", Type.EmptyTypes);
                }
                _loadingCommentProperty = sceneLoaderType.GetProperty("LoadingComment",
                    BindingFlags.Public | BindingFlags.Static);
                Log($"[gate] curtain widgets: content={(_curtainContent != null ? _curtainContent.GetType().Name : "<null>")} " +
                    $"indicator={(_curtainIndicator != null ? _curtainIndicator.GetType().Name : "<null>")} " +
                    $"comment={_loadingCommentProperty != null}");
            }
            catch (Exception ex)
            {
                Log("[gate] capture curtain widgets failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Re-show the native curtain content (tips + loading indicator). SceneLoader hides
        /// both right before activating the level, which is why a held curtain is otherwise
        /// just a black frame.
        /// </summary>
        private void ShowCurtainContent()
        {
            if (_curtainContentShown)
                return;
            _curtainContentShown = true;
            var contentAlive = _curtainContent is UnityEngine.Object contentObject && contentObject != null;
            var indicatorAlive = _curtainIndicator is UnityEngine.Object indicatorObject && indicatorObject != null;
            if (!contentAlive && !indicatorAlive)
            {
                // SceneLoader unloads the curtain scene right after activation; if the widgets
                // live there they are gone by now and we fall back to the plain black screen.
                Log("[gate] native curtain widgets were unloaded; keeping the black screen");
                return;
            }
            try
            {
                _curtainContentShow?.Invoke(_curtainContent, null);
                _curtainIndicatorShow?.Invoke(_curtainIndicator, null);
                SetLoadingComment("正在进入战局…");
            }
            catch (Exception ex)
            {
                Log("[gate] show curtain content failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void HideCurtainContent()
        {
            if (!_curtainContentShown)
                return;
            _curtainContentShown = false;
            try
            {
                _curtainContentHide?.Invoke(_curtainContent, null);
                _curtainIndicatorHide?.Invoke(_curtainIndicator, null);
            }
            catch (Exception ex)
            {
                Log("[gate] hide curtain content failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void SetLoadingComment(string text)
        {
            try
            {
                _loadingCommentProperty?.SetValue(null, text);
            }
            catch
            {
                // cosmetic only
            }
        }

        private void RestoreNativeLoadingCurtain()
        {
            if (!_curtainExtended)
                return;
            try
            {
                _sceneLoaderWaitField?.SetValue(_sceneLoaderInstance, _savedWaitAfterSceneLoaded);
            }
            catch (Exception ex)
            {
                Log("[gate] restore curtain failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _curtainExtended = false;
                _curtainAwaitingScene = false;
                _curtainContentShown = false;
                _sceneLoaderWaitField = null;
                _sceneLoaderInstance = null;
                _curtainContent = null;
                _curtainIndicator = null;
            }
        }

        private void HideNativeLoadingCurtain()
        {
            try
            {
                var type = FindTypeByName("BlackScreen");
                var method = type?.GetMethod("HideAndReturnTask",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(AnimationCurve), typeof(float), typeof(float) }, null);
                if (method == null)
                {
                    Log("[gate] BlackScreen.HideAndReturnTask not found");
                    return;
                }
                method.Invoke(null, new object[] { null, 0f, 0.5f });
            }
            catch (Exception ex)
            {
                Log("[gate] hide curtain failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void UpdateLoadGate()
        {
            if (!_curtainExtended)
                return;

            if (!_inRaidScene)
            {
                // The curtain is extended before the scene load; the first frames still run in
                // the base / loading scenes. Only give up when the raid scene never shows up.
                if (_curtainAwaitingScene && Time.unscaledTime < _loadGateDeadline)
                {
                    if (Time.unscaledTime >= _nextLoadGateLog)
                    {
                        _nextLoadGateLog = Time.unscaledTime + 1f;
                        Log("[gate] waiting for the raid scene to activate");
                    }
                    return;
                }

                if (_curtainAwaitingScene)
                    Log("[gate] raid scene never became active; revealing");
                _curtainAwaitingScene = false;
                HideCurtainContent();
                HideNativeLoadingCurtain();
                RestoreNativeLoadingCurtain();
                _loadGateDeadline = 0f;
                return;
            }

            _curtainAwaitingScene = false;
            ShowCurtainContent();
            if (_loadGateDeadline == 0f)
                _loadGateDeadline = Time.unscaledTime + 45f;

            var variantReady = MapVariantSettled;
            var inventoryReady = _inventoryMirrorActive && _pendingApplyMask == 0;
            // Do not reveal A's world while the other party member is still activating its
            // raid subscene and has no mirror yet: with a slow second client the local spawn
            // anchor/inventory were ready ~12 s earlier, so A stared at an empty raid.
            var expectedRemotes = 0;
            var snapshot = _client != null ? _client.LatestSnapshot : null;
            if (snapshot != null && snapshot.Players != null && snapshot.Players.Length > 1)
                expectedRemotes = snapshot.Players.Length - 1;
            var anchoredRemotes = 0;
            if (expectedRemotes > 0 && _client != null && _spawnAnchorApplied)
            {
                var anchor = _client.SpawnAnchor;
                var anchorPosition = new Vector3(anchor.X / 100f, anchor.Y / 100f, anchor.Z / 100f);
                foreach (var pair in _remotes)
                {
                    var remote = pair.Value;
                    if (remote != null &&
                        (remote.transform.position - anchorPosition).sqrMagnitude <= 2500f)
                        anchoredRemotes++;
                }
            }
            var remotesReady = _remotes.Count >= expectedRemotes &&
                               anchoredRemotes >= expectedRemotes;
            var ready = variantReady && _spawnReported && _spawnAnchorApplied && inventoryReady &&
                        remotesReady;
            var timedOut = Time.unscaledTime >= _loadGateDeadline;
            if (!ready && !timedOut)
            {
                if (Time.unscaledTime >= _nextLoadGateLog)
                {
                    _nextLoadGateLog = Time.unscaledTime + 1f;
                    Log($"[gate] waiting variant={variantReady} reported={_spawnReported} " +
                        $"anchor={_spawnAnchorApplied} inventory={inventoryReady} " +
                        $"remotes={_remotes.Count}/{expectedRemotes} anchored={anchoredRemotes}");
                    SetLoadingComment(!variantReady ? "正在准备地图…"
                        : !_spawnReported ? "正在进入战局…"
                        : !_spawnAnchorApplied ? "正在同步出生点…"
                        : !remotesReady ? "正在等待队友…"
                        : "正在同步装备…");
                }
                return;
            }

            if (!ready)
                Log($"[gate] timed out; revealing world (variant={variantReady} " +
                    $"reported={_spawnReported} anchor={_spawnAnchorApplied} " +
                    $"inventory={inventoryReady} remotes={_remotes.Count}/{expectedRemotes} " +
                    $"anchored={anchoredRemotes})");
            else
                Log("[gate] world ready; lifting the loading curtain");

            _loadGateDeadline = 0f;
            _nextLoadGateLog = 0f;
            HideCurtainContent();
            HideNativeLoadingCurtain();
            RestoreNativeLoadingCurtain();
        }
    }
}
