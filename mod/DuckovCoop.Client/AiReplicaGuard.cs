using HarmonyLib;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// Always-on guard for native AI replicas: clients may present hit VFX, but must
    /// never settle local damage/HP for a replica. D5 applies authoritative HP later.
    /// </summary>
    internal static class AiReplicaGuard
    {
        private const string HarmonyId = "duckov.coop.aireplica.guard";
        private static bool _patched;

        internal static void Init()
        {
            if (_patched)
                return;
            try
            {
                new Harmony(HarmonyId).PatchAll(typeof(AiReplicaGuard).Assembly);
                _patched = true;
                CoopRuntime.Log("[catalog] AI replica guard ready");
            }
            catch (System.Exception ex)
            {
                CoopRuntime.Log("[catalog] AI replica guard failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        [HarmonyPatch(typeof(DamageReceiver), "Hurt")]
        private static class DamageReceiverHurtGuard
        {
            [HarmonyPriority(Priority.Last)]
            [HarmonyPrefix]
            private static bool Prefix(DamageReceiver __instance)
            {
                return __instance == null ||
                       __instance.GetComponentInParent<RemoteAIReplicaTag>() == null;
            }
        }

        [HarmonyPatch(typeof(Health), "Hurt")]
        private static class HealthHurtGuard
        {
            [HarmonyPriority(Priority.Last)]
            [HarmonyPrefix]
            private static bool Prefix(Health __instance)
            {
                return __instance == null ||
                       __instance.GetComponentInParent<RemoteAIReplicaTag>() == null;
            }
        }

        /// <summary>
        /// Spider/robot replica controllers can enter a MagicBlend state before their
        /// playable graph is initialized; SetJobData then throws every animation frame.
        /// Skip the native blend update until the graph is initialized.
        /// </summary>
        [HarmonyPatch(typeof(KINEMATION.MagicBlend.Runtime.MagicBlending),
            "UpdateMagicBlendAsset")]
        private static class MagicBlendInitializedGuard
        {
            [HarmonyPrefix]
            private static bool Prefix(KINEMATION.MagicBlend.Runtime.MagicBlending __instance)
            {
                try
                {
                    var initialized = AccessTools.Field(
                        typeof(KINEMATION.MagicBlend.Runtime.MagicBlending), "_isInitialized");
                    if (initialized == null)
                        return true;
                    return initialized.GetValue(__instance) is bool value && value;
                }
                catch
                {
                    return true;
                }
            }
        }
    }
}
