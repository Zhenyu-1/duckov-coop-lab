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
        private string _savedToken = string.Empty;
        private static string SessionPath =>
            Path.Combine(Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location) ?? string.Empty, "session.txt");

        private void LoadSessionToken()
        {
            try
            {
                if (File.Exists(SessionPath))
                {
                    _savedToken = File.ReadAllText(SessionPath).Trim();
                    if (_savedToken.Length > 0)
                        Log($"session token loaded: {_savedToken.Substring(0, System.Math.Min(8, _savedToken.Length))}...");
                }
            }
            catch (Exception ex)
            {
                Log("session token load failed: " + ex.Message);
            }
        }

        private void SaveSessionToken()
        {
            try
            {
                if (_client == null || string.IsNullOrEmpty(_client.Token))
                    return;

                _savedToken = _client.Token;
                File.WriteAllText(SessionPath, _savedToken, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log("session token save failed: " + ex.Message);
            }
        }

    }
}
