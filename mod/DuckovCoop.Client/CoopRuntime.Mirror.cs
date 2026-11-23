using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DuckovCoop.Client;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private int _lastMirrorCount;
        private float _nextMirrorRetry;
        // ---------- B4: authoritative stash mirror ----------

        private void UpdateMirror()
        {
            if (_client == null || _client.MirrorCount == _lastMirrorCount)
                return;

            if (Time.unscaledTime < _nextMirrorRetry)
                return;

            var mirror = _client.LastMirror;
            if (mirror == null)
                return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"playerId\": {mirror.PlayerId},");
                sb.AppendLine($"  \"version\": {mirror.Version},");
                sb.AppendLine("  \"items\": [");
                for (var i = 0; i < mirror.Entries.Length; i++)
                {
                    var entry = mirror.Entries[i];
                    var comma = i + 1 < mirror.Entries.Length ? "," : string.Empty;
                    sb.AppendLine($"    {{ \"itemId\": {entry.ItemId}, \"count\": {entry.Count} }}{comma}");
                }
                sb.AppendLine("  ]");
                sb.AppendLine("}");

                File.WriteAllText(MirrorPath, sb.ToString(), Encoding.UTF8);
                _lastMirrorCount = _client.MirrorCount;
                _client.SendMirrorAck(mirror.Version);
                Log($"mirror written: version={mirror.Version} items={mirror.Entries.Length} -> {MirrorPath}");
            }
            catch (Exception ex)
            {
                _nextMirrorRetry = Time.unscaledTime + 3f;
                Log("mirror write failed: " + ex.Message);
            }
        }

        private static string MirrorPath =>
            Path.Combine(Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location) ?? string.Empty, "coop_mirror.json");

    }
}
