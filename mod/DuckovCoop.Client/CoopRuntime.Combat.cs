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
        private ulong _lastCombatEventId;
        private object _localHealth;
        private float _localLastHp = -1f;
        private float _nextLocalHpCheck;
        private bool _localDead;
        private bool _leaveSent;
        private float _nextHealthBindLog;

        /// <summary>
        /// Contract 2.16.0: a self-damage report may not exceed one full health bar (the server
        /// clamps to the same value). Frames the native explosion hits a player with locally.
        /// </summary>
        private const int MaxSelfReportDamage = 100;

        /// <summary>
        /// While the server just set our HP, a lower local value is not local damage: it is the
        /// authoritative apply itself. Reporting it back would double the damage.
        /// </summary>
        private float _localHpAuthorityUntil;
        private float _nextSelfHpSuppressLog;

        /// <summary>
        /// Called by every code path that applies authoritative HP to the local player.
        /// Moves the sampling baseline so <see cref="ReportLocalDamage"/> does not mistake the
        /// server's own update for native (explosion/fall/environment) damage.
        /// </summary>
        private void NoteAuthoritativeLocalHp(float hpAfter)
        {
            if (hpAfter >= 0f)
                _localLastHp = hpAfter;
            _localHpAuthorityUntil = Time.unscaledTime + 0.35f;
        }

        /// <summary>
        /// Contract 2.16.0: the server only ever removes HP it knows about. Native damage that
        /// the game applies locally (explosion, fall, environment) never reaches it, so the
        /// next server HP update used to overwrite the bar. Report the delta as a self hit.
        /// </summary>
        private void ReportLocalDamage()
        {
            var main = CharacterMainControl.Main;
            if (main == null)
            {
                _localHealth = null;
                _localLastHp = -1f;
                return;
            }

            if (_localHealth == null)
            {
                const BindingFlags anyFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                var receiver = main.GetComponent<DamageReceiver>()
                               ?? main.GetComponentInChildren<DamageReceiver>(true)
                               ?? main.GetComponentInParent<DamageReceiver>();

                var health = receiver != null
                    ? typeof(DamageReceiver).GetField("health", anyFlags)?.GetValue(receiver)
                    : null;

                if (health == null)
                    health = main.GetComponent<Health>() ?? main.GetComponentInChildren<Health>(true);

                if (health != null)
                {
                    _localHealth = health;
                    _localLastHp = ReadHealth(health);
                    Log($"local health bound: hp={_localLastHp:F0} via={(receiver != null ? "DamageReceiver" : "Health")}");
                }
                else if (Time.unscaledTime >= _nextHealthBindLog)
                {
                    _nextHealthBindLog = Time.unscaledTime + 5f;
                    Log("local health not found yet");
                }
            }

            if (_localHealth == null)
                return;

            var hp = ReadHealth(_localHealth);
            if (hp < 0f)
                return;

            if (_localLastHp >= 0f && hp < _localLastHp - 0.5f)
            {
                if (GateAProbe.ConsumeAuthoritativeApply())
                {
                    Log($"[gatea] A7 authoritative HP apply detected; self-report suppressed hp={hp:F0}/{_localLastHp:F0}");
                }
                else if (Time.unscaledTime < _localHpAuthorityUntil)
                {
                    // The server just pushed this HP (damage or heal); the drop is our own
                    // baseline catching up, never native damage.
                    if (Time.unscaledTime >= _nextSelfHpSuppressLog)
                    {
                        _nextSelfHpSuppressLog = Time.unscaledTime + 5f;
                        Log($"[self] local hp change from server; not reported hp={hp:F0}/{_localLastHp:F0}");
                    }
                }
                else
                {
                    var damage = Mathf.Clamp(Mathf.RoundToInt(_localLastHp - hp), 1,
                        MaxSelfReportDamage);
                    if (_client != null && _client.State == ClientState.InGame &&
                        _client.PlayerId != 0)
                        _client.SendHitReport(++_correlation, 0, _client.PlayerId, damage);
                    Log($"[self] local damage reported: damage={damage} hp={hp:F0}/{_localLastHp:F0}");
                }
            }

            _localLastHp = hp;

            if (!_localDead && hp <= 0f)
            {
                _localDead = true;
                _raidSpawnAllowed = false;
                _interaction = "DEAD - waiting for SRespawn";
                Log("local player died; waiting for server respawn");
                // D9/B: the server keeps the session and revives this character on SRespawn.
                // Sending LeaveRaid here settled the death and cancelled the respawn, which made
                // the client stay dead at 0 HP while the server had already revived it.
            }
        }

        private void UpdateLocalHealth()
        {
            if (_client.State == ClientState.InGame && Time.unscaledTime >= _nextLocalHpCheck)
            {
                _nextLocalHpCheck = Time.unscaledTime + 0.1f;
                ReportLocalDamage();
            }
        }
        private void UpdateEvents()
        {
            if (_client.LastCombatEvent != null && _client.LastCombatEvent.EventId != _lastCombatEventId)
            {
                _lastCombatEventId = _client.LastCombatEvent.EventId;
                var combat = _client.LastCombatEvent;
                var label = combat.Type == 1 ? "died" : "hit";
                _interaction = $"combat: {label} kind={combat.TargetKind} id={combat.TargetId} value={combat.Value}";
                Log(_interaction);

                if (combat.Type == 1 && combat.TargetKind == 1)
                    RemoveAi(combat.TargetId);
                else if (combat.Type == 1 && combat.TargetKind == 0)
                {
                    // Death does not change AI ownership: the player respawns after 5 s and
                    // keeps simulating. Removing the AI ghosts here made every AI disappear
                    // until the next AOI wake cycle.
                    if (combat.TargetId == _client.PlayerId)
                    {
                        _raidSpawnAllowed = false;
                        OnLocalPlayerDied("combat event");
                    }
                    RemoveRemotePlayer((ushort)combat.TargetId);
                }
            }
        }
    }
}
