using System;
using DuckovCoop.Client;
using HarmonyLib;
using ItemStatsSystem;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// D9/B: server-authoritative respawn. Native CharacterDieTask is skipped in a coop raid,
    /// and SRespawn revives the same character in place (equipment/backpack were already moved
    /// to the corpse; the pet bag stays; the server follows with SInitialInventory).
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private int _lastRespawnCount;
        private bool _deathTaskBlockedLogged;
        private Renderer[] _localDeathRenderers;

        internal void OnLocalPlayerDied(string reason)
        {
            if (_localDead)
                return;
            _localDead = true;
            var main = CharacterMainControl.Main;
            if (main == null)
                return;
            try
            {
                _localDeathRenderers = main.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in _localDeathRenderers)
                    if (renderer != null)
                        renderer.enabled = false;
                main.SetForceMoveVelocity(Vector3.zero);
                if (main.movementControl != null)
                    main.movementControl.enabled = false;
                main.enabled = false;
                Log($"[respawn] local death state entered ({reason}); waiting for SRespawn");
            }
            catch (Exception ex)
            {
                Log($"[respawn] local death state failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void SetLocalDeathState(bool dead)
        {
            var main = CharacterMainControl.Main;
            if (main == null)
            {
                _localDead = dead;
                return;
            }
            if (dead)
            {
                OnLocalPlayerDied("server death");
                return;
            }

            _localDead = false;
            try
            {
                main.enabled = true;
                if (main.movementControl != null)
                    main.movementControl.enabled = true;
                if (_localDeathRenderers != null)
                {
                    foreach (var renderer in _localDeathRenderers)
                        if (renderer != null)
                            renderer.enabled = true;
                    _localDeathRenderers = null;
                }
                Log("[respawn] local death state cleared");
            }
            catch (Exception ex)
            {
                Log($"[respawn] death state clear failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void UpdateRespawn()
        {
            if (_client == null)
                return;
            if (_client.RespawnCount == _lastRespawnCount)
                return;
            _lastRespawnCount = _client.RespawnCount;
            var respawn = _client.LastRespawn;
            if (respawn == null || respawn.PlayerId != _client.PlayerId)
                return;
            ApplyRespawn(respawn);
        }

        private void ApplyRespawn(Respawn respawn)
        {
            var main = CharacterMainControl.Main;
            var health = main != null ? main.Health : null;
            if (main == null || health == null)
            {
                Log("[respawn] no main character/health to revive");
                return;
            }

            try
            {
                SetLocalDeathState(false);
                var isDead = AccessTools.Field(typeof(Health), "isDead");
                var handled = AccessTools.Field(typeof(CharacterMainControl), "hasHandledDeath");
                isDead?.SetValue(health, false);
                handled?.SetValue(main, false);
                // Death can destroy the native CharacterItem; without it MaxHealth is 0 and
                // the old code revived the player at 0 HP while the server had full HP.
                if (main.CharacterItem == null)
                    TryBootstrapCharacterItem(main, _localCharacterItemTypeId);
                var targetHp = respawn.Hp > 0 ? respawn.Hp : health.MaxHealth;
                health.CurrentHealth = targetHp;
                // Contract 2.16.0: a revive is authoritative, not a native damage source.
                NoteAuthoritativeLocalHp(targetHp);
                main.transform.position =
                    new Vector3(respawn.X / 100f, respawn.Y / 100f, respawn.Z / 100f);
                main.transform.rotation = Quaternion.Euler(0f, respawn.Yaw, 0f);
                Log($"[respawn] revived player={respawn.PlayerId} hp={health.CurrentHealth} " +
                    $"max={health.MaxHealth} serverHp={respawn.Hp} " +
                    $"charItem={(main.CharacterItem != null)} pos={main.transform.position}");
            }
            catch (Exception ex)
            {
                Log($"[respawn] revive failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Called from the LevelManager.CharacterDieTask prefix.</summary>
        internal bool ShouldBlockNativeDeathTask()
        {
            if (_client == null || _client.State != ClientState.InGame || !_inRaidScene)
                return false;
            if (!_deathTaskBlockedLogged)
            {
                _deathTaskBlockedLogged = true;
                Log("[respawn] native CharacterDieTask suppressed; waiting for SRespawn");
            }
            return true;
        }
    }
}
