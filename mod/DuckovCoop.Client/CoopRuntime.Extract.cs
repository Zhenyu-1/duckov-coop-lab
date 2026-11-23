using System;
using System.Reflection;
using DuckovCoop.Client;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// M2-v12 extraction gate: the native CountDownArea still owns the 5 s countdown and the
    /// native closure/base-load flow, but the authoritative extract must be accepted by the
    /// server first. Raid-related local saves are suppressed while the coop raid is active.
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private bool _extractPending;
        private ulong _extractPendingCorrelation;
        private bool _serverExtractApproved;
        private bool _nativeEvacuationNotified;
        private bool _nativeEvacuationLoadStarted;
        private float _extractReturnDeadline;
        private bool _suppressAutoExtract;

        private object _pendingEvacuationInstance;
        private MethodInfo _pendingEvacuationMethod;
        private object[] _pendingEvacuationArgs;

        internal static bool ShouldSuppressCoopRaidSaves()
        {
            var runtime = Active;
            return runtime != null && runtime._inRaidScene && runtime._client != null &&
                   runtime._client.State == ClientState.InGame;
        }

        private bool IsCoopRaidActiveForEvacuation()
        {
            return _client != null && _client.State == ClientState.InGame && _inRaidScene &&
                   !string.IsNullOrEmpty(_raidSceneName) && _raidSceneName.StartsWith("Level_");
        }

        /// <summary>Called from the LevelManager.NotifyEvacuated Harmony prefix.</summary>
        internal static bool OnNativeEvacuation()
        {
            var runtime = Active;
            if (runtime == null || !runtime.IsCoopRaidActiveForEvacuation())
                return true;
            if (runtime._nativeEvacuationNotified)
                return false;
            if (runtime._serverExtractApproved)
                return true;

            runtime.RequestServerExtractFromNative("NotifyEvacuated");
            return false;
        }

        internal static void OnNativeEvacuationPostfix(bool runOriginal)
        {
            var runtime = Active;
            if (runtime != null && runOriginal)
                runtime._nativeEvacuationNotified = true;
        }

        /// <summary>Called from the SceneLoader.LoadScene Harmony prefix when notifyEvacuation is true.</summary>
        internal static bool OnNativeEvacuationLoad(object instance, MethodInfo method, object[] args)
        {
            var runtime = Active;
            if (runtime == null || !runtime.IsCoopRaidActiveForEvacuation())
                return true;

            var notifyIndex = FindNotifyEvacuationArgument(method);
            if (notifyIndex < 0 || args == null || notifyIndex >= args.Length ||
                !(args[notifyIndex] is bool notify) || !notify)
                return true;

            if (runtime._serverExtractApproved)
            {
                runtime._nativeEvacuationLoadStarted = true;
                return true;
            }

            if (!runtime._extractPending)
                runtime.RequestServerExtractFromNative("LoadScene");

            runtime.StorePendingEvacuationLoad(instance, method, args);
            return false;
        }

        private static int FindNotifyEvacuationArgument(MethodInfo method)
        {
            if (method == null)
                return -1;
            var parameters = method.GetParameters();
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType == typeof(bool) &&
                    string.Equals(parameters[i].Name, "notifyEvacuation", StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private void StorePendingEvacuationLoad(object instance, MethodInfo method, object[] args)
        {
            if (_pendingEvacuationMethod != null)
                return;

            _pendingEvacuationInstance = instance;
            _pendingEvacuationMethod = method;
            _pendingEvacuationArgs = args != null ? (object[])args.Clone() : Array.Empty<object>();
            Log($"[extract] native base load held until server approval ({method.Name})");
        }

        private void RequestServerExtractFromNative(string source)
        {
            if (_client == null || _client.State != ClientState.InGame || _extractPending)
                return;

            _extractPending = true;
            _serverExtractApproved = false;
            _extractPendingCorrelation = ++_correlation;
            _client.SendExtract(_extractPendingCorrelation);
            _interaction = "正在向服务器确认撤离…";
            Log($"[extract] requested from {source} correlation={_extractPendingCorrelation}");
        }

        /// <summary>F4 debug seam: same authoritative request without the native exit trigger.</summary>
        private void DebugRequestExtract()
        {
            if (!IsCoopRaidActiveForEvacuation())
                return;
            RequestServerExtractFromNative("F4 debug");
        }

        private void ProcessExtractResult()
        {
            if (_client == null || _client.LastExtract == null)
                return;
            var result = _client.LastExtract;
            if (result.CorrelationId == _lastExtractCorrelation)
                return;

            _lastExtractCorrelation = result.CorrelationId;
            _lastExtractCode = result.Code;
            _extractPending = false;
            var pendingCorrelation = _extractPendingCorrelation;
            _extractPendingCorrelation = 0;

            // 7 = IdempotentReplay: a lost code 0 retry means the extraction already
            // succeeded server-side, which is just as authoritative for the client.
            if (result.Code != 0 && result.Code != (byte)ErrorCode.IdempotentReplay)
            {
                _serverExtractApproved = false;
                ClearPendingEvacuationLoad();
                _interaction = $"撤离失败 (code {result.Code})";
                Log($"[extract] rejected: code={result.Code} correlation={result.CorrelationId} " +
                    $"pending={pendingCorrelation}");
                return;
            }

            _serverExtractApproved = true;
            _nativeEvacuationNotified = false;
            _autoExtractSent = true;
            _raidSpawnAllowed = false;
            _interaction = "撤离成功，正在返回基地";
            Log($"[extract] approved: code={result.Code} correlation={result.CorrelationId} " +
                $"pending={pendingCorrelation}");

            if (_pendingEvacuationMethod != null)
            {
                ResumePendingEvacuation();
                return;
            }

            // The native path may still be between NotifyEvacuated and LoadScene (the
            // closure screen). Give it a moment to reach LoadScene before forcing the base
            // load ourselves.
            _extractReturnDeadline = Time.unscaledTime + 2.5f;
        }

        private void UpdateExtractReturn()
        {
            if (!_serverExtractApproved || !_inRaidScene || _nativeEvacuationLoadStarted)
                return;

            // The native closure ("settlement") screen waits for the player's continue click
            // before SceneLoader.LoadScene is called. Never tear it down with the fallback.
            if (IsNativeClosureVisible())
            {
                _extractReturnDeadline = Time.unscaledTime + 2.5f;
                return;
            }

            if (_extractReturnDeadline <= 0f || Time.unscaledTime < _extractReturnDeadline)
                return;
            _extractReturnDeadline = 0f;

            if (IsNativeSceneLoading())
            {
                _extractReturnDeadline = Time.unscaledTime + 2f;
                return;
            }

            Log("[extract] native load did not start; falling back to base scene");
            NotifyNativeEvacuation();
            _nativeEvacuationLoadStarted = LoadBaseSceneNative();
            if (!_nativeEvacuationLoadStarted)
                _extractReturnDeadline = Time.unscaledTime + 3f;
        }

        private void ResumePendingEvacuation()
        {
            var method = _pendingEvacuationMethod;
            var instance = _pendingEvacuationInstance;
            var args = _pendingEvacuationArgs;
            ClearPendingEvacuationLoad();

            if (method == null)
            {
                NotifyNativeEvacuation();
                LoadBaseSceneNative();
                return;
            }

            try
            {
                _nativeEvacuationLoadStarted = true;
                method.Invoke(instance, args);
                Log("[extract] replaying native scene load after approval");
            }
            catch (Exception ex)
            {
                Log("[extract] native load replay failed: " + ex.GetType().Name + ": " + ex.Message);
                NotifyNativeEvacuation();
                _nativeEvacuationLoadStarted = LoadBaseSceneNative();
            }
        }

        private void ClearPendingEvacuationLoad()
        {
            _pendingEvacuationInstance = null;
            _pendingEvacuationMethod = null;
            _pendingEvacuationArgs = null;
        }

        private void NotifyNativeEvacuation()
        {
            if (_nativeEvacuationNotified)
                return;

            try
            {
                var levelManagerType = FindTypeByName("LevelManager");
                var infoType = FindTypeByName("EvacuationInfo");
                if (levelManagerType == null || infoType == null)
                    return;

                var instance = levelManagerType
                    .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                var method = levelManagerType.GetMethod("NotifyEvacuated",
                    BindingFlags.Public | BindingFlags.Instance);
                if (instance == null || method == null)
                    return;

                var main = levelManagerType
                    .GetProperty("MainCharacter", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(instance) as CharacterMainControl;
                if (main == null)
                    return;

                var sceneId = _raidSceneName;
                var multiSceneType = FindTypeByName("MultiSceneCore");
                var activeSubScene = multiSceneType
                    ?.GetProperty("ActiveSubSceneID", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null) as string;
                if (!string.IsNullOrEmpty(activeSubScene))
                    sceneId = activeSubScene;

                var info = Activator.CreateInstance(infoType, sceneId, main.transform.position);
                method.Invoke(instance, new[] { info });
                _nativeEvacuationNotified = true;
                Log($"[extract] native NotifyEvacuated invoked scene={sceneId}");
            }
            catch (Exception ex)
            {
                Log("[extract] native NotifyEvacuated failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsNativeSceneLoading()
        {
            try
            {
                var type = FindTypeByName("SceneLoader");
                var value = type?.GetProperty("IsSceneLoading", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                return value is bool loading && loading;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNativeClosureVisible()
        {
            try
            {
                var type = FindTypeByName("ClosureView");
                var instance = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                return instance != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool LoadBaseSceneNative()
        {
            try
            {
                var type = FindTypeByName("SceneLoader");
                if (type == null)
                    return false;

                var instance = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                if (instance == null)
                    return false;

                var settingsType = FindTypeByName("GameplayDataSettings");
                var management = settingsType?.GetProperty("SceneManagement",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var baseScene = management?.GetType()
                    .GetProperty("BaseScene", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(management);
                if (baseScene != null)
                {
                    // Prefer the core LoadScene overload so the forced return does not sit
                    // behind LoadBaseScene's click-to-continue gate and never writes a save.
                    foreach (var candidate in type.GetMethods(BindingFlags.Public |
                                                              BindingFlags.Instance))
                    {
                        if (!string.Equals(candidate.Name, "LoadScene", StringComparison.Ordinal))
                            continue;
                        var parameters = candidate.GetParameters();
                        if (parameters.Length == 0 ||
                            !parameters[0].ParameterType.IsInstanceOfType(baseScene))
                            continue;

                        var hasClick = false;
                        foreach (var parameter in parameters)
                        {
                            if (string.Equals(parameter.Name, "clickToConinue", StringComparison.Ordinal))
                            {
                                hasClick = true;
                                break;
                            }
                        }
                        if (!hasClick)
                            continue;

                        var args = new object[parameters.Length];
                        args[0] = baseScene;
                        for (var i = 1; i < parameters.Length; i++)
                        {
                            var parameter = parameters[i];
                            switch (parameter.Name)
                            {
                            case "overrideCurtainScene": args[i] = null; break;
                            case "clickToConinue": args[i] = false; break;
                            case "notifyEvacuation": args[i] = false; break;
                            case "doCircleFade": args[i] = true; break;
                            case "useLocation": args[i] = false; break;
                            case "saveToFile": args[i] = false; break;
                            case "hideTips": args[i] = false; break;
                            default:
                                args[i] = parameter.ParameterType.IsValueType
                                    ? Activator.CreateInstance(parameter.ParameterType)
                                    : null;
                                break;
                            }
                        }

                        candidate.Invoke(instance, args);
                        Log("[extract] SceneLoader.LoadScene(base, no click) issued");
                        return true;
                    }
                }

                MethodInfo method = null;
                foreach (var candidate in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!string.Equals(candidate.Name, "LoadBaseScene", StringComparison.Ordinal))
                        continue;
                    var parameters = candidate.GetParameters();
                    if (parameters.Length == 2 && parameters[1].ParameterType == typeof(bool))
                    {
                        method = candidate;
                        break;
                    }
                }

                if (method != null)
                {
                    method.Invoke(instance, new object[] { null, true });
                    Log("[extract] SceneLoader.LoadBaseScene issued");
                    return true;
                }

                // Fallback: resolve the configured base scene name and use the same loader
                // seam already used for raid scene transitions.
                var baseName = (baseScene as UnityEngine.Object)?.name;
                if (!string.IsNullOrEmpty(baseName) && TryLoadSceneViaGame(baseName))
                    return true;
            }
            catch (Exception ex)
            {
                Log("[extract] base scene load failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return false;
        }

        private void BeginForcedBaseReturn(string reason)
        {
            if (!_inRaidScene && !_raidSpawnAllowed)
            {
                CleanupRaidState();
                return;
            }

            Log($"[extract] returning to base: {reason}");
            _suppressAutoExtract = true;
            _serverExtractApproved = true;
            _extractPending = false;
            _nativeEvacuationLoadStarted = true;
            ClearPendingEvacuationLoad();
            CleanupRaidState();
            _nativeEvacuationLoadStarted = LoadBaseSceneNative();
            if (!_nativeEvacuationLoadStarted)
                _interaction = "无法加载基地场景，请重试";
        }

        private void ResetExtractionState()
        {
            _extractPending = false;
            _extractPendingCorrelation = 0;
            _serverExtractApproved = false;
            _nativeEvacuationNotified = false;
            _nativeEvacuationLoadStarted = false;
            _extractReturnDeadline = 0f;
            _suppressAutoExtract = false;
            ClearPendingEvacuationLoad();
        }
    }
}
