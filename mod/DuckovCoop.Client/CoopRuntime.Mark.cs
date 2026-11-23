using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    /// <summary>
    /// F6: write a greppable location mark to the client log. The mark carries the
    /// scene, player world position, nearest bridge roots and the nearest
    /// construction/cost/interactable components, so a report can say "at mark 3"
    /// instead of describing the place.
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private int _locationMarkIndex;
        private float _nextLocationMarkAt;

        private void UpdateLocationMark()
        {
            if (_consoleActivated)
                return;
            if (!UnityEngine.Input.GetKeyDown(KeyCode.F6))
                return;
            if (Time.unscaledTime < _nextLocationMarkAt)
                return;
            _nextLocationMarkAt = Time.unscaledTime + 0.25f;
            LogLocationMark();
        }

        private void LogLocationMark()
        {
            var main = CharacterMainControl.Main;
            if (main == null && LevelManager.Instance != null)
                main = LevelManager.Instance.MainCharacter;

            var scene = SceneManager.GetActiveScene();
            var position = main != null ? main.transform.position : Vector3.zero;
            var yaw = main != null ? main.transform.eulerAngles.y : 0f;
            var mainPath = main != null ? MapVariantPath(main.transform) : "none";

            var selfPos = "na";
            if (_client != null && _client.LatestSnapshot != null) {
                foreach (var player in _client.LatestSnapshot.Players) {
                    if (player.PlayerId != _client.PlayerId)
                        continue;
                    selfPos = $"{player.X / 100f:F1},{player.Y / 100f:F1},{player.Z / 100f:F1}";
                    break;
                }
            }

            var raid = _client != null
                ? $"epoch={_client.RaidEpoch} phase={_client.RaidPhase}"
                : "no-client";

            _locationMarkIndex++;
            Log($"[mark] id={_locationMarkIndex} scene={scene.name} pos={MapVariantVector(position)} " +
                $"yaw={yaw:F1} main={mainPath} server={selfPos} {raid}");

            foreach (var rootPath in MapVariantBridgeRootPaths) {
                var root = GameObject.Find(rootPath);
                if (root == null)
                    continue;
                var distance = Vector3.Distance(position, root.transform.position);
                Log($"[mark] bridge root={rootPath} pos={MapVariantVector(root.transform.position)} " +
                    $"dist={distance:F1}");
            }

            var candidates = new List<LocationMarkCandidate>();
            var seen = new HashSet<int>();
            foreach (var typeName in new[]
                     {
                         "ConstructionSite", "CostTaker", "InteractableBase", "Door",
                         "MultiSceneTeleporter", "SimpleTeleporter", "TeleportBeacon",
                     }) {
                var type = MapVariantFindType(typeName);
                if (type == null)
                    continue;
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(type, true)) {
                    if (obj is not MonoBehaviour component || component == null)
                        continue;
                    if (!seen.Add(component.GetInstanceID()))
                        continue;
                    var distance = Vector3.Distance(position, component.transform.position);
                    if (distance > 50f)
                        continue;
                    candidates.Add(new LocationMarkCandidate(distance, component));
                }
            }

            candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            var limit = Mathf.Min(10, candidates.Count);
            for (var i = 0; i < limit; i++) {
                var candidate = candidates[i];
                var component = candidate.Component;
                var typeName = component.GetType().Name;
                var cost = MapVariantReadMember(component, "cost") ??
                           MapVariantReadMember(component, "Cost");
                var requireItemId = MapVariantReadMember(component, "requireItemId");
                var wasBuilt = MapVariantReadMember(component, "wasBuilt");
                var closed = MapVariantReadMember(component, "closed");
                var noRequireItem = MapVariantReadMember(component, "NoRequireItem");
                var enabled = component is Behaviour behaviour
                    ? behaviour.enabled.ToString()
                    : "n/a";
                Log($"[mark] near {i + 1} type={typeName} path={MapVariantPath(component.transform)} " +
                    $"pos={MapVariantVector(component.transform.position)} dist={candidate.Distance:F1} " +
                    $"active={component.gameObject.activeInHierarchy} enabled={enabled} " +
                    $"requireItemId={requireItemId ?? "-"} cost={MapVariantCostText(cost)} " +
                    $"wasBuilt={wasBuilt ?? "-"} closed={closed ?? "-"} " +
                    $"noRequireItem={noRequireItem ?? "-"}");
            }

            Log($"[mark] id={_locationMarkIndex} done near={candidates.Count} shown={limit}");
        }

        private readonly struct LocationMarkCandidate
        {
            public readonly float Distance;
            public readonly MonoBehaviour Component;

            public LocationMarkCandidate(float distance, MonoBehaviour component)
            {
                Distance = distance;
                Component = component;
            }
        }
    }
}
