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
        private static void SyncAnimator(GameObject remote)
        {
            try
            {
                var main = CharacterMainControl.Main;
                if (main == null)
                    return;

                var source = main.GetComponentInChildren<Animator>();
                var target = remote.GetComponentInChildren<Animator>();
                if (source == null || target == null ||
                    source.runtimeAnimatorController == null || target.runtimeAnimatorController == null)
                    return;

                var sourceParams = source.parameters;
                var targetParams = target.parameters;

                for (var i = 0; i < sourceParams.Length; i++)
                {
                    var param = sourceParams[i];
                    if (param.type == AnimatorControllerParameterType.Trigger)
                        continue;

                    var exists = false;
                    for (var j = 0; j < targetParams.Length; j++)
                    {
                        if (targetParams[j].name == param.name)
                        {
                            exists = true;
                            break;
                        }
                    }

                    if (!exists)
                        continue;

                    switch (param.type)
                    {
                    case AnimatorControllerParameterType.Float:
                        target.SetFloat(param.name, source.GetFloat(param.name));
                        break;
                    case AnimatorControllerParameterType.Int:
                        target.SetInteger(param.name, source.GetInteger(param.name));
                        break;
                    case AnimatorControllerParameterType.Bool:
                        target.SetBool(param.name, source.GetBool(param.name));
                        break;
                    }
                }

                target.speed = source.speed;
            }
            catch
            {
                // animation sync must never break the mod
            }
        }

    }
}
