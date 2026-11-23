using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// M2.0 Spike A/B: prove that a fixed 0Harmony.dll can be loaded by the
    /// game's ModManager and can patch a private game method. This class only
    /// logs; it must not change gameplay.
    /// </summary>
    internal static class HarmonySpike
    {
        private const string HarmonyId = "duckovcoop.runtime.spike";

        // Keep Harmony out of field signatures so a missing 0Harmony.dll fails
        // inside Init's try/catch instead of failing CoopRuntime type loading.
        private static object _harmony;
        private static bool _patched;
        private static int _fixedUpdateHits;

        internal static void Init()
        {
            try
            {
                var asm = typeof(Harmony).Assembly;
                CoopRuntime.Log($"[harmony] 0Harmony loaded: {asm.GetName().Name} {asm.GetName().Version} " +
                                $"path={asm.Location}");

                if (_patched)
                    return;

                var type = FindTargetType();
                if (type == null)
                {
                    CoopRuntime.Log("[harmony] target type SetActiveByPlayerDistance not found");
                    return;
                }

                var method = AccessTools.Method(type, "FixedUpdate");
                if (method == null)
                {
                    CoopRuntime.Log($"[harmony] FixedUpdate not found on {type.FullName}");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(HarmonySpike).GetMethod(
                    nameof(OnFixedUpdate), BindingFlags.Static | BindingFlags.NonPublic));
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(method, prefix: prefix);

                _harmony = harmony;
                _patched = true;

                var info = Harmony.GetPatchInfo(method);
                CoopRuntime.Log($"[harmony] patch applied: {type.FullName}.FixedUpdate " +
                                $"prefixes={info?.Prefixes?.Count ?? 0} id={HarmonyId}");

                // Self-test without touching a live scene object: invoke the private
                // method on an uninitialized instance. FixedUpdate returns early when
                // there is no player/cached list, but the Harmony prefix still fires.
                try
                {
                    var probe = FormatterServices.GetUninitializedObject(type);
                    method.Invoke(probe, null);
                    CoopRuntime.Log($"[harmony] self-test FixedUpdate invoked, hits={_fixedUpdateHits}");
                }
                catch (Exception ex)
                {
                    CoopRuntime.Log("[harmony] self-test failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[harmony] init failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void Dispose()
        {
            try
            {
                var harmony = (Harmony)_harmony;
                harmony?.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[harmony] unpatch failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _harmony = null;
                _patched = false;
            }
        }

        private static Type FindTargetType()
        {
            var type = AccessTools.TypeByName("GameplayDataSettings+SetActiveByPlayerDistance")
                       ?? AccessTools.TypeByName("GameplayDataSettings.SetActiveByPlayerDistance");
            if (type != null)
                return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var candidate in assembly.GetTypes())
                    {
                        if (candidate.Name == "SetActiveByPlayerDistance")
                            return candidate;
                    }
                }
                catch
                {
                    // dynamic/broken assemblies are fine
                }
            }

            return null;
        }

        private static bool OnFixedUpdate(object __instance)
        {
            try
            {
                var type = __instance.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var playerProperty = type.GetProperty("PlayerTransform", flags);
                var listField = type.GetField("cachedListRef", flags);
                var player = playerProperty?.GetValue(__instance) as Transform;
                var list = listField?.GetValue(__instance) as List<GameObject>;
                if (player == null || list == null)
                    return true; // let the native FixedUpdate handle an uninitialised state

                var activationSq = CoopRuntime.AiActivationRadiusMeters * CoopRuntime.AiActivationRadiusMeters;
                var deactivationSq = CoopRuntime.AiDeactivationRadiusMeters * CoopRuntime.AiDeactivationRadiusMeters;
                foreach (var item in list)
                {
                    if (item == null)
                        continue;
                    // M2-v11: the server owns AI lifecycle (AOI spawn / Cleanup despawn), and
                    // this component sleeps *parent* containers. Never sleep a container that
                    // holds an AI the owner has spawned: the other player may be standing right
                    // next to it, and a slept AI is what they see as a frozen replica.
                    var runtime = CoopRuntime.Active;
                    if (runtime != null && runtime.IsProtectedActivationRoot(item))
                    {
                        if (!item.activeSelf)
                            item.SetActive(true);
                        continue;
                    }
                    // Active objects sleep at 60 m; sleeping objects wake at 50 m.
                    var limit = item.activeSelf ? deactivationSq : activationSq;
                    var active = (player.position - item.transform.position).sqrMagnitude < limit;
                    if (item.activeSelf != active)
                        item.SetActive(active);
                }

                var hit = System.Threading.Interlocked.Increment(ref _fixedUpdateHits);
                if (hit <= 3)
                    CoopRuntime.Log($"[ai] activation hysteresis applied (wake=50m sleep=60m) hit #{hit}");
                return false;
            }
            catch
            {
                // Never let activation policy break the scene; fall back to native.
                return true;
            }
        }
    }
}
