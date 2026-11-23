using System;
using System.Reflection;
using Duckov.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DuckovCoopClient
{
    /// <summary>
    /// Friendly health bars: the game's own HealthBar above every remote PLAYER mirror,
    /// tinted blue. Replaces the old custom OnGUI bar (which hid the native one and drew
    /// its own texture).
    ///
    /// The native bar is created on <see cref="Health.RequestHealthBar"/>; a mirror never
    /// gets that call from the game (it fires from CharacterMainControl.OnEnable for the
    /// main character only), so ask once per second. The manager reuses an existing bar
    /// instead of stacking duplicates.
    /// </summary>
    public sealed partial class CoopRuntime
    {
        internal static readonly Color FriendlyBarColor = new Color(0.30f, 0.62f, 1f, 1f);

        private bool _friendlyBarInit;
        private float _nextFriendlyBarRequest;

        private void EnsureFriendlyHealthBars()
        {
            if (!_friendlyBarInit)
            {
                _friendlyBarInit = true;
                FriendlyHealthBarTint.Init();
            }

            if (Time.unscaledTime < _nextFriendlyBarRequest)
                return;
            _nextFriendlyBarRequest = Time.unscaledTime + 1f;
            if (_remotes.Count == 0)
                return;

            HealthBar[] bars = null;
            foreach (var pair in _remotes)
            {
                var remote = pair.Value;
                if (remote == null || !remote.activeInHierarchy)
                    continue;
                var health = MirrorHealth(remote);
                if (health == null)
                    continue;
                if (!health.showHealthBar)
                    health.showHealthBar = true;
                health.RequestHealthBar();
                // Safety net: the per-frame Refresh postfix only fires when HP changes.
                // A full-HP mirror would never repaint, so re-apply here too.
                if (bars == null)
                    bars = Resources.FindObjectsOfTypeAll<HealthBar>();
                foreach (var bar in bars)
                {
                    if (bar == null || bar.target != health)
                        continue;
                    FriendlyHealthBarTint.Apply(bar);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Native HealthBar.Refresh picks its fill colour from a per-prefab Gradient, so a
    /// cloned bar keeps the enemy colour. A postfix repaints only bars whose target is a
    /// coop mirror (the mirror control carries <see cref="MirrorYawFollow"/>).
    /// </summary>
    internal static class FriendlyHealthBarTint
    {
        private const string HarmonyId = "duckov.coop.healthbar.friendly";
        private static bool _patched;
        private static int _logCount;
        private static float _nextLogAt;

        internal static void Init()
        {
            if (_patched)
                return;
            try
            {
                var refresh = AccessTools.Method(typeof(HealthBar), "Refresh");
                if (refresh == null)
                {
                    CoopRuntime.Log("[ui] friendly bar: HealthBar.Refresh not found");
                    return;
                }
                var postfix = new HarmonyMethod(typeof(FriendlyHealthBarTint)
                    .GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
                new Harmony(HarmonyId).Patch(refresh, postfix: postfix);
                _patched = true;
                CoopRuntime.Log("[ui] friendly health bar tint ready");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[ui] friendly health bar tint failed: " +
                                ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Postfix(HealthBar __instance)
        {
            Apply(__instance);
        }

        /// <summary>
        /// Idempotent: repaint a bar blue if its target is a coop mirror. Safe to call from
        /// the Refresh postfix and from the 1s safety-net pass.
        ///
        /// Contract 2.16.0: the mirror body is a clone of OUR character, so the native
        /// CurrentHealth / MaxHealth ratio uses our max HP. The percentage is recomputed from
        /// the server's <c>PlayerSnapshot.maxHp</c> instead.
        /// </summary>
        internal static void Apply(HealthBar bar)
        {
            try
            {
                var target = bar != null ? bar.target : null;
                if (target == null || !IsMirrorTarget(target))
                    return;

                var mirrorTag = target.GetComponentInParent<RemotePlayerMirrorTag>();
                ushort serverMax = 0;
                if (mirrorTag != null)
                    CoopRuntime.RemoteMaxHp.TryGetValue(mirrorTag.PlayerId, out serverMax);

                if (AccessTools.Field(typeof(HealthBar), "fill")?.GetValue(bar) is Image fill)
                {
                    fill.color = CoopRuntime.FriendlyBarColor;
                    if (serverMax > 0)
                    {
                        var amount = Mathf.Clamp01(target.CurrentHealth / serverMax);
                        fill.fillAmount = amount;
                        if (_logCount < 8 && Time.unscaledTime >= _nextLogAt)
                        {
                            _nextLogAt = Time.unscaledTime + 3f;
                            _logCount++;
                            CoopRuntime.Log($"[ui] bar max player={(mirrorTag != null ? mirrorTag.PlayerId : 0)} " +
                                            $"hp={target.CurrentHealth:F0}/{serverMax} fill={amount:F2}");
                        }
                    }
                    else if (_logCount < 8 && Time.unscaledTime >= _nextLogAt)
                    {
                        _nextLogAt = Time.unscaledTime + 3f;
                        _logCount++;
                        CoopRuntime.Log($"[ui] bar tint hp={target.CurrentHealth:F0}/{target.MaxHealth:F0} " +
                                        $"fillAmount={fill.fillAmount:F2} color={fill.color} (no server max yet)");
                    }
                }

                if (AccessTools.Field(typeof(HealthBar), "followFill")?.GetValue(bar) is Image follow)
                {
                    var blue = CoopRuntime.FriendlyBarColor;
                    var alpha = follow.color.a;
                    follow.color = new Color(blue.r * 0.55f, blue.g * 0.55f, blue.b * 0.55f, alpha);
                    if (serverMax > 0)
                        follow.fillAmount = Mathf.Clamp01(target.CurrentHealth / serverMax);
                }
            }
            catch
            {
                // Presentation only: never let a bar repaint break the game loop.
            }
        }

        /// <summary>
        /// Only the visible mirror body carries <see cref="RemotePlayerMirrorTag"/>. Ancestry
        /// heuristics are not enough here: the AI target proxy also lives under the remote
        /// root and has its own Health, which produced a second (red) bar on hosts.
        /// </summary>
        private static bool IsMirrorTarget(Health target)
        {
            return target.GetComponentInParent<RemotePlayerMirrorTag>() != null;
        }
    }
}
