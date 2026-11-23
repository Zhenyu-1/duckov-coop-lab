using System;
using System.Reflection;
using HarmonyLib;

namespace DuckovCoopClient
{
    /// <summary>
    /// Harmony glue for M2-v12: hold the native evacuation scene load until SExtract(code=0)
    /// and suppress raid-scoped local saves (base/settings saves keep working).
    /// </summary>
    internal static class RaidFlowPatches
    {
        private const string HarmonyId = "duckovcoop.runtime.raidflow";

        private static object _harmony;
        private static bool _patched;

        internal static void Init()
        {
            if (_patched)
                return;

            try
            {
                var harmony = new Harmony(HarmonyId);
                var patched = 0;

                var levelManager = FindType("LevelManager");
                if (levelManager != null)
                {
                    patched += Patch(harmony, levelManager, "NotifyEvacuated",
                        nameof(OnNotifyEvacuated), nameof(OnNotifyEvacuatedPostfix));
                    patched += Patch(harmony, levelManager, "SaveMainCharacter",
                        nameof(SuppressDuringRaid));
                    patched += Patch(harmony, levelManager, "NotifySaveBeforeLoadScene",
                        nameof(SuppressDuringRaid));
                }
                else
                {
                    CoopRuntime.Log("[raidflow] LevelManager not found");
                }

                var savesSystem = FindType("Saves.SavesSystem") ?? FindType("SavesSystem");
                if (savesSystem != null)
                {
                    patched += Patch(harmony, savesSystem, "SaveFile", nameof(SuppressDuringRaid));
                    patched += Patch(harmony, savesSystem, "CollectSaveData", nameof(SuppressDuringRaid));
                }
                else
                {
                    CoopRuntime.Log("[raidflow] SavesSystem not found");
                }

                patched += PatchSceneLoads(harmony);
                _harmony = harmony;
                _patched = true;
                CoopRuntime.Log($"[raidflow] patches applied: {patched}");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[raidflow] init failed: " + ex.GetType().Name + ": " + ex.Message);
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
                CoopRuntime.Log("[raidflow] unpatch failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _harmony = null;
                _patched = false;
            }
        }

        private static int Patch(Harmony harmony, Type type, string methodName, string prefixName,
                                 string postfixName = null)
        {
            try
            {
                var method = AccessTools.Method(type, methodName);
                if (method == null)
                {
                    CoopRuntime.Log($"[raidflow] missing {type.FullName}.{methodName}");
                    return 0;
                }

                var prefix = new HarmonyMethod(typeof(RaidFlowPatches).GetMethod(
                    prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                var postfix = postfixName != null
                    ? new HarmonyMethod(typeof(RaidFlowPatches).GetMethod(
                        postfixName, BindingFlags.Static | BindingFlags.NonPublic))
                    : null;
                harmony.Patch(method, prefix: prefix, postfix: postfix);
                return 1;
            }
            catch (Exception ex)
            {
                CoopRuntime.Log($"[raidflow] patch {type.Name}.{methodName} failed: " +
                                $"{ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        private static int PatchSceneLoads(Harmony harmony)
        {
            var type = FindType("SceneLoader");
            if (type == null)
            {
                CoopRuntime.Log("[raidflow] SceneLoader not found");
                return 0;
            }

            var prefix = new HarmonyMethod(typeof(RaidFlowPatches).GetMethod(
                nameof(OnLoadScenePrefix), BindingFlags.Static | BindingFlags.NonPublic));
            var count = 0;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                                                    BindingFlags.DeclaredOnly))
            {
                if (!string.Equals(method.Name, "LoadScene", StringComparison.Ordinal))
                    continue;

                var hasNotify = false;
                foreach (var parameter in method.GetParameters())
                {
                    if (parameter.ParameterType == typeof(bool) &&
                        string.Equals(parameter.Name, "notifyEvacuation", StringComparison.Ordinal))
                    {
                        hasNotify = true;
                        break;
                    }
                }
                if (!hasNotify)
                    continue;

                try
                {
                    harmony.Patch(method, prefix: prefix);
                    count++;
                }
                catch (Exception ex)
                {
                    CoopRuntime.Log("[raidflow] LoadScene patch failed: " +
                                    ex.GetType().Name + ": " + ex.Message);
                }
            }
            return count;
        }

        private static Type FindType(string name)
        {
            var direct = AccessTools.TypeByName(name);
            if (direct != null)
                return direct;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(name, false);
                    if (type != null)
                        return type;

                    foreach (var candidate in assembly.GetTypes())
                    {
                        if (candidate.Name == name)
                            return candidate;
                    }
                }
                catch
                {
                    // dynamic assemblies are fine
                }
            }
            return null;
        }

        private static bool OnNotifyEvacuated()
        {
            return CoopRuntime.OnNativeEvacuation();
        }

        private static void OnNotifyEvacuatedPostfix(bool __runOriginal)
        {
            CoopRuntime.OnNativeEvacuationPostfix(__runOriginal);
        }

        private static bool OnLoadScenePrefix(object __instance, object[] __args,
                                              MethodBase __originalMethod)
        {
            return CoopRuntime.OnNativeEvacuationLoad(__instance,
                __originalMethod as MethodInfo, __args);
        }

        private static bool SuppressDuringRaid()
        {
            return !CoopRuntime.ShouldSuppressCoopRaidSaves();
        }
    }
}
