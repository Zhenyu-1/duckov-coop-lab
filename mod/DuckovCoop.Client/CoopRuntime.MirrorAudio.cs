using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// BUG-2026-11-12 follow-up (gunshot audio).
    ///
    /// Gunshots are FMOD events, not AudioClips. TeamSoda.Duckov.Core builds the event
    /// path from `SFX/Combat/Gun/Shoot/{shootKey}`, but the raw key ("ak47") does not
    /// match the bank entry ("Event not found: SFX/Combat/Gun/Shoot/ak47" in Player.log).
    /// So the event list of the loaded banks is enumerated once and the path whose last
    /// segment matches the weapon's shootKey (case-insensitive) is used; playback goes
    /// through FMODUnity.RuntimeManager.PlayOneShot(positional).
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private const string GunShotEventTemplate = "SFX/Combat/Gun/Shoot/";
        private const string MeleeDefaultEvent = "event:/SFX/Combat/Melee/attack_default";

        private static readonly Dictionary<uint, string> GunShotEvents = new Dictionary<uint, string>();
        private static readonly Dictionary<uint, string> GunShotKeys = new Dictionary<uint, string>();
        private static List<string> _fmodEventPaths;
        private static MethodInfo _fmodPlayOneShot;
        private static bool _fmodPlayResolved;

        private int _mirrorShotSoundLogs;
        private int _mirrorMeleeSoundLogs;
        private int _mirrorMeleeFxLogs;

        private static bool EnsureFmodPlay()
        {
            if (_fmodPlayResolved)
                return _fmodPlayOneShot != null;

            _fmodPlayResolved = true;
            try
            {
                var type = Type.GetType("FMODUnity.RuntimeManager, FMODUnity");
                _fmodPlayOneShot = type?.GetMethod("PlayOneShot",
                    new[] { typeof(string), typeof(Vector3) });
                if (_fmodPlayOneShot == null)
                    Log("[v8] mirror sound: FMODUnity.RuntimeManager.PlayOneShot not found");
            }
            catch (Exception ex)
            {
                Log($"[v8] mirror sound: FMOD resolve failed {ex.GetType().Name}: {ex.Message}");
            }

            return _fmodPlayOneShot != null;
        }

        /// <summary>Play the shooter's gunshot on the mirror (visual-only, no gameplay effect).</summary>
        private string PlayMirrorShotSound(ItemStatsSystem.ItemAgent agent, object setting,
            Vector3 position, uint weaponId)
        {
            try
            {
                var path = ResolveGunShotEvent(setting, weaponId);
                if (string.IsNullOrEmpty(path) || !EnsureFmodPlay())
                    return null;

                _fmodPlayOneShot.Invoke(null, new object[] { path, position });
                if (_mirrorShotSoundLogs < 3)
                {
                    _mirrorShotSoundLogs++;
                    var main = CharacterMainControl.Main;
                    var distance = main != null
                        ? Vector3.Distance(position, main.transform.position)
                        : -1f;
                    Log($"[v8] mirror shot sound weapon={weaponId} event={path} " +
                        $"dist={distance:F0}m");
                }

                return path;
            }
            catch (Exception ex)
            {
                if (_mirrorShotSoundLogs < 3)
                {
                    _mirrorShotSoundLogs++;
                    Log($"[v8] mirror shot sound failed: {ex.GetType().Name}: {ex.Message}");
                }

                return null;
            }
        }

        /// <summary>
        /// Slash VFX for the mirror: ItemAgent_MeleeWeapon exposes the native `slashFx`
        /// prefab (MeleeSlashFx), so the clone plays the same effect at the weapon.
        /// </summary>
        private void PlayMirrorMeleeFx(ItemStatsSystem.ItemAgent agent, Vector3 position)
        {
            try
            {
                if (_mirrorMeleeFxLogs < 3)
                {
                    _mirrorMeleeFxLogs++;
                    var slashName = FindGameObjectField(agent, "slashFx");
                    CoopRuntime.Log($"[v8] mirror melee fx agent={agent.GetType().Name} " +
                        $"slash={(slashName != null ? slashName.name : "<none>")}");
                }

                var slash = FindGameObjectField(agent, "slashFx");
                if (slash == null)
                    return;

                var source = agent.transform;
                var instance = UnityEngine.Object.Instantiate(slash, source.position, source.rotation);
                if (instance != null)
                    UnityEngine.Object.Destroy(instance, 2f);
            }
            catch (Exception ex)
            {
                if (_mirrorMeleeFxLogs < 3)
                {
                    _mirrorMeleeFxLogs++;
                    CoopRuntime.Log($"[v8] mirror melee fx failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static GameObject FindGameObjectField(object instance, string fieldName)
        {
            if (instance == null)
                return null;

            var type = instance.GetType();
            while (type != null)
            {
                var field = type.GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                    return field.GetValue(instance) as GameObject;
                type = type.BaseType;
            }

            return null;
        }

        /// <summary>Melee swing sound for the mirror (FMOD, same bank as the gunshots).</summary>
        private void PlayMirrorMeleeSound(ItemStatsSystem.ItemAgent agent, Vector3 position)        {
            try
            {
                EnsureFmodEventPaths();
                var path = ResolveMeleeEvent(agent);
                if (string.IsNullOrEmpty(path) || !EnsureFmodPlay())
                    return;

                _fmodPlayOneShot.Invoke(null, new object[] { path, position });
                if (_mirrorMeleeSoundLogs < 3)
                {
                    _mirrorMeleeSoundLogs++;
                    Log($"[v8] mirror melee sound event={path}");
                }
            }
            catch (Exception ex)
            {
                if (_mirrorMeleeSoundLogs < 3)
                {
                    _mirrorMeleeSoundLogs++;
                    Log($"[v8] mirror melee sound failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static string ResolveMeleeEvent(ItemStatsSystem.ItemAgent agent)
        {
            var key = ReadKeyField(agent, "key");
            if (_fmodEventPaths != null && _fmodEventPaths.Count > 0)
            {
                if (!string.IsNullOrEmpty(key))
                {
                    var suffix = "attack_" + key;
                    foreach (var path in _fmodEventPaths)
                    {
                        if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                            return path;
                    }
                }

                foreach (var path in _fmodEventPaths)
                {
                    if (path.IndexOf("/Melee/attack_default", StringComparison.OrdinalIgnoreCase) >= 0)
                        return path;
                }
            }

            return MeleeDefaultEvent;
        }

        /// <summary>First non-empty string field whose name contains <paramref name="token"/>.</summary>
        private static string ReadKeyField(object instance, string token)
        {
            if (instance == null)
                return null;

            var type = instance.GetType();
            while (type != null)
            {
                foreach (var field in type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.FieldType != typeof(string))
                        continue;
                    if (field.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var value = field.GetValue(instance) as string;
                    if (!string.IsNullOrEmpty(value))
                        return value;
                }

                type = type.BaseType;
            }

            return null;
        }

        private static string ResolveGunShotEvent(object setting, uint weaponId)
        {
            if (weaponId != 0 && GunShotEvents.TryGetValue(weaponId, out var cached))
                return cached;

            var key = ReadShootKey(setting);
            if (weaponId != 0)
                GunShotKeys[weaponId] = key;

            EnsureFmodEventPaths();

            var suffix = "/" + key;
            if (_fmodEventPaths != null && _fmodEventPaths.Count > 0)
            {
                foreach (var path in _fmodEventPaths)
                {
                    if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (weaponId != 0)
                            GunShotEvents[weaponId] = path;
                        return path;
                    }
                }
            }

            var fallback = GunShotEventTemplate + key;
            if (weaponId != 0)
                GunShotEvents[weaponId] = fallback;
            return fallback;
        }

        private static string ReadShootKey(object setting)
        {
            if (setting == null)
                return "Default";

            var type = setting.GetType();
            while (type != null)
            {
                var field = type.GetField("shootKey",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(string))
                {
                    var value = field.GetValue(setting) as string;
                    if (!string.IsNullOrEmpty(value))
                        return value;
                    break;
                }

                type = type.BaseType;
            }

            return "Default";
        }

        /// <summary>
        /// Enumerate every event path of the loaded FMOD banks (reflection only, no
        /// compile-time FMOD dependency). Runs once; logs the gun/shoot candidates.
        /// </summary>
        private static void EnsureFmodEventPaths()
        {
            if (_fmodEventPaths != null)
                return;

            _fmodEventPaths = new List<string>();
            try
            {
                var runtimeType = Type.GetType("FMODUnity.RuntimeManager, FMODUnity");
                var studioProperty = runtimeType?.GetProperty("StudioSystem",
                    BindingFlags.Public | BindingFlags.Static);
                var studio = studioProperty?.GetValue(null);
                if (studio == null)
                {
                    var studioField = runtimeType?.GetField("StudioSystem",
                        BindingFlags.Public | BindingFlags.Static);
                    studio = studioField?.GetValue(null);
                }

                if (studio == null)
                {
                    Log("[v8] fmod: StudioSystem unavailable");
                    return;
                }

                var getBanks = FindOutMethod(studio.GetType(), "getBankList");
                if (getBanks == null)
                {
                    Log("[v8] fmod: System.getBankList missing");
                    return;
                }

                var bankArgs = new object[] { null };
                getBanks.Invoke(studio, bankArgs);
                if (!(bankArgs[0] is Array banks))
                {
                    Log("[v8] fmod: bank list null");
                    return;
                }

                foreach (var bank in banks)
                {
                    if (bank == null)
                        continue;
                    var getEvents = FindOutMethod(bank.GetType(), "getEventList");
                    if (getEvents == null)
                        continue;

                    var eventArgs = new object[] { null };
                    getEvents.Invoke(bank, eventArgs);
                    if (!(eventArgs[0] is Array descriptions))
                        continue;

                    foreach (var description in descriptions)
                    {
                        if (description == null)
                            continue;
                        var getPath = FindOutMethod(description.GetType(), "getPath");
                        if (getPath == null)
                            continue;

                        var pathArgs = new object[] { null };
                        getPath.Invoke(description, pathArgs);
                        var path = pathArgs[0] as string;
                        if (!string.IsNullOrEmpty(path))
                            _fmodEventPaths.Add(path);
                    }
                }

                Log($"[v8] fmod events enumerated={_fmodEventPaths.Count}");
                var logged = 0;
                foreach (var path in _fmodEventPaths)
                {
                    if (path.IndexOf("Gun/Shoot", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (logged >= 20)
                        break;
                    logged++;
                    Log($"[v8] fmod gun event: {path}");
                }

                // Melee swing sounds are not wired yet; log the candidates once so the
                // event can be matched in a follow-up without another guessing round.
                var meleeLogged = 0;
                foreach (var path in _fmodEventPaths)
                {
                    if (path.IndexOf("melee", StringComparison.OrdinalIgnoreCase) < 0 &&
                        path.IndexOf("swing", StringComparison.OrdinalIgnoreCase) < 0 &&
                        path.IndexOf("slash", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (meleeLogged >= 20)
                        break;
                    meleeLogged++;
                    Log($"[v8] fmod melee event: {path}");
                }
            }
            catch (Exception ex)
            {
                Log($"[v8] fmod enumerate failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Single-out-parameter method lookup (FMOD's out-based API shape).</summary>
        private static MethodInfo FindOutMethod(Type type, string name)
        {
            if (type == null)
                return null;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != name)
                    continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 1 || !parameters[0].IsOut)
                    continue;
                return method;
            }

            return null;
        }
    }
}
