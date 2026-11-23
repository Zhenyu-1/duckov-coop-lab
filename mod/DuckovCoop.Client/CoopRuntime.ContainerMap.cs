using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    /// <summary>
    /// Maps authored server container ids to native InteractableLootbox positions.
    /// Server ids are 100000 + the 1-based container_spawns.xml order; the same
    /// order is exported to container_spawns.txt next to the mod DLL.
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private readonly List<(uint Id, Vector3 Position)> _containerMap =
            new List<(uint, Vector3)>();
        private bool _containerMapLoaded;
        private uint _nearestNativeContainerId;
        private uint _lastLoggedNativeContainerId;
        private float _nextNativeContainerScan;
        private float _lastNativeContainerDistance = -1f;

        private void UpdateNativeContainerSelection()
        {
            if (_client == null || !_inRaidScene && !SceneManager.GetActiveScene().name.StartsWith("Level_"))
                return;
            if (Time.unscaledTime < _nextNativeContainerScan)
                return;
            _nextNativeContainerScan = Time.unscaledTime + 0.5f;
            EnsureContainerMap();

            var main = CharacterMainControl.Main;
            if (main == null || _containerMap.Count == 0)
            {
                _nearestNativeContainerId = 0;
                return;
            }

            var origin = main.transform.position;
            var bestDistance = float.MaxValue;
            uint bestId = 0;
            try
            {
                var boxes = UnityEngine.Object.FindObjectsOfType<InteractableLootbox>();
                foreach (var box in boxes)
                {
                    if (box == null)
                        continue;
                    var position = box.transform.position;
                    foreach (var entry in _containerMap)
                    {
                        var delta = entry.Position - position;
                        if (delta.sqrMagnitude > 1f) // 1 m match radius
                            continue;
                        var distance = (entry.Position - origin).sqrMagnitude;
                        if (distance > 9f) // 3 m interaction range
                            continue;
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestId = entry.Id;
                        }
                    }
                }
            }
            catch
            {
                // native container type not present in this scene
            }

            _nearestNativeContainerId = bestId;
            _lastNativeContainerDistance = bestId != 0 ? Mathf.Sqrt(bestDistance) : -1f;
            if (bestId != _lastLoggedNativeContainerId)
            {
                _lastLoggedNativeContainerId = bestId;
                if (bestId != 0)
                    Log($"[spawn] native container id={bestId} dist={_lastNativeContainerDistance:F1}");
            }
        }

        /// <summary>Maps a native lootbox actor to its authored server container id (1 m).</summary>
        internal uint FindContainerIdForLootbox(InteractableLootbox box)
        {
            if (box == null)
                return 0;
            EnsureContainerMap();
            var position = box.transform.position;
            foreach (var entry in _containerMap)
            {
                if ((entry.Position - position).sqrMagnitude <= 1f)
                    return entry.Id;
            }
            return 0;
        }

        private void EnsureContainerMap()
        {            if (_containerMapLoaded)
                return;
            _containerMapLoaded = true;
            try
            {
                var path = Path.Combine(
                    Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location) ?? string.Empty,
                    "container_spawns.txt");
                if (!File.Exists(path))
                {
                    Log("[spawn] container_spawns.txt missing; F2 nearest falls back to server id=0");
                    return;
                }
                foreach (var line in File.ReadAllLines(path))
                {
                    var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4)
                        continue;
                    if (!uint.TryParse(parts[0], out var id))
                        continue;
                    if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                        !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                        !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                        continue;
                    _containerMap.Add((id, new Vector3(x / 100f, y / 100f, z / 100f)));
                }
                Log($"[spawn] container map loaded entries={_containerMap.Count}");
            }
            catch (Exception ex)
            {
                Log("[spawn] container map load failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
