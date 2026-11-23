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
    public sealed class ModBehaviour : global::Duckov.Modding.ModBehaviour
    {
        private void Start()
        {
            var go = new GameObject("DuckovCoopClientRuntime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<CoopRuntime>();
            Debug.Log("[DuckovCoopClient] mod started");
        }
    }

    /// <summary>
    /// Coop client: connection panel (F1), transform sync and debug interaction keys.
    ///   F1 panel | F6 mark location | F2 view container | F3 take slot 0 | F4 extract
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
    }
}
