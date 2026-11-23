using HarmonyLib;
using Duckov.Utilities;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// Blocks native scene spawners (night spiders, storm shadows, dynamic waves) while
    /// the coop session is authoritative. Disabling the spawner components alone is not
    /// enough: CharacterSpawnerRoot.Update may already have called StartSpawn, and the
    /// WaveCharacterSpawner coroutine keeps creating characters afterwards.
    /// Manual Harmony id (same reason as V5Patches: AiReplicaGuard/GateA probe both
    /// PatchAll the assembly, so attribute patches would be applied twice).
    /// </summary>
    internal static class SpawnGuard
    {
        private const string HarmonyId = "duckovcoop.spawnguard";
        private static Harmony _harmony;
        private static float _nextLog;
        private static float _nextLootLog;
        private static float _nextLootBoxLog;

        internal static void Init()
        {
            if (_harmony != null)
                return;
            try
            {
                _harmony = new Harmony(HarmonyId);
                var method = AccessTools.Method(typeof(CharacterSpawnerRoot), "StartSpawn");
                if (method != null)
                {
                    _harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(SpawnGuard), nameof(BlockNativeSpawner)));
                    CoopRuntime.Log("[spawn] native spawner guard installed");
                }
                else
                {
                    CoopRuntime.Log("[spawn] CharacterSpawnerRoot.StartSpawn not found");
                }

                // Native LootSpawner drops local ground items on every client. Once the
                // server owns world items, those local copies must not run: they are not
                // in the server ledger and otherwise duplicate/desync the ground loot.
                var lootStart = AccessTools.Method(typeof(LootSpawner), "Start");
                if (lootStart != null)
                {
                    _harmony.Patch(lootStart,
                        prefix: new HarmonyMethod(typeof(SpawnGuard), nameof(BlockNativeLootSpawner)));
                    CoopRuntime.Log("[spawn] native loot spawner guard installed");
                }
                else
                {
                    CoopRuntime.Log("[spawn] LootSpawner.Start not found");
                }

                var lootSetup = AccessTools.Method(typeof(LootSpawner), "Setup");
                if (lootSetup != null)
                {
                    _harmony.Patch(lootSetup,
                        prefix: new HarmonyMethod(typeof(SpawnGuard), nameof(BlockNativeLootSpawner)));
                }

                // T5: authored lootboxes must not roll local contents. The server container is
                // the only source; the native LootView is populated by CoopRuntime.Containers.
                var lootBoxSetup = AccessTools.Method(typeof(LootBoxLoader), "Setup");
                if (lootBoxSetup != null)
                {
                    _harmony.Patch(lootBoxSetup,
                        prefix: new HarmonyMethod(typeof(SpawnGuard), nameof(BlockNativeLootBox)));
                    CoopRuntime.Log("[container] native lootbox roll guard installed");
                }
                else
                {
                    CoopRuntime.Log("[container] LootBoxLoader.Setup not found");
                }

                // Keep authored actors visible: the server decides whether the container is
                // empty, not the native per-scene random active roll.
                var randomActive = AccessTools.Method(typeof(LootBoxLoader), "RandomActive");
                if (randomActive != null)
                {
                    _harmony.Patch(randomActive,
                        prefix: new HarmonyMethod(typeof(SpawnGuard),
                            nameof(BlockLootBoxRandomActive)));
                }

                // Native death creates a local dead lootbox from the character inventory.
                // In coop the server owns that loot (player/AI corpse containers), so the
                // native duplicate must not appear.
                var createFromItem = AccessTools.Method(typeof(InteractableLootbox), "CreateFromItem");
                if (createFromItem != null)
                {
                    _harmony.Patch(createFromItem,
                        prefix: new HarmonyMethod(typeof(SpawnGuard),
                            nameof(BlockNativeDeadLootbox)));
                    CoopRuntime.Log("[container] native dead lootbox guard installed");
                }
            }
            catch (System.Exception ex)
            {
                CoopRuntime.Log($"[spawn] guard failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool BlockNativeSpawner(CharacterSpawnerRoot __instance)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.ShouldBlockNativeSpawner)
                return true;

            if (Time.unscaledTime >= _nextLog)
            {
                _nextLog = Time.unscaledTime + 5f;
                CoopRuntime.Log($"[spawn] blocked native spawner '{(__instance != null ? __instance.name : "<null>")}'");
            }
            return false;
        }

        /// <summary>
        /// P2: native LootSpawner creates client-local ground items. The server will seed
        /// world items instead; block both Start and direct Setup calls while in a coop raid.
        /// </summary>
        private static bool BlockNativeLootSpawner(LootSpawner __instance)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.ShouldBlockNativeSpawner)
                return true;

            if (Time.unscaledTime >= _nextLootLog)
            {
                _nextLootLog = Time.unscaledTime + 5f;
                CoopRuntime.Log(
                    $"[spawn] blocked native loot spawner '{(__instance != null ? __instance.name : "<null>")}'");
            }
            return false;
        }

        /// <summary>
        /// T5: stop the native LootBoxLoader from rolling client-local container contents.
        /// The actor stays alive; CoopRuntime.Containers populates its Inventory from the
        /// server container mirror when the player opens it.
        /// </summary>
        private static bool BlockNativeLootBox(LootBoxLoader __instance)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.ShouldBlockNativeContainers)
                return true;

            if (Time.unscaledTime >= _nextLootBoxLog)
            {
                _nextLootBoxLog = Time.unscaledTime + 5f;
                CoopRuntime.Log(
                    $"[container] native lootbox roll blocked '{(__instance != null ? __instance.name : "<null>")}'");
            }
            return false;
        }

        private static bool BlockLootBoxRandomActive(LootBoxLoader __instance)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.ShouldBlockNativeContainers)
                return true;
            return false;
        }

        internal static bool AllowNativeDeadLootbox;

        private static bool BlockNativeDeadLootbox(ref InteractableLootbox __result)
        {
            if (AllowNativeDeadLootbox)
            {
                AllowNativeDeadLootbox = false;
                return true; // let native CharacterMainControl.OnDead build its visual
            }
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.ShouldBlockNativeContainers)
                return true;
            __result = null;
            return false;
        }
    }
}
