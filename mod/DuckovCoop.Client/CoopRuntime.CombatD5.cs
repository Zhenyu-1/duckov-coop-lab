using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DuckovCoop.Client;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// D5 v4 client glue: applies server-authoritative combat results and reports the
    /// local player stat snapshot once per raid (or after an equipment change).
    /// </summary>
    public sealed partial class CoopRuntime
    {
        internal static CoopRuntime Active { get; private set; }

        // v4 uses CHitEvent; the transitional HP-delta report must stay off.
        private const bool UseLegacyHitReport = false;

        private uint _playerStatsSentEpoch;
        private float _nextPlayerStatsTry;
        private bool _playerStatsDirty;
        // 2026-11-16: self-target combat events used to be dropped silently when the local
        // character was not available yet (spawn/respawn window). Log the drop so a settled
        // server hit can be told apart from a shot that never hit anything.
        private float _nextSelfApplyDropLog;

        // M2-v12: victim-side hurt FX on observers that are not the shooter. Native
        // HurtVisual listens to Health.OnHurtEvent, so replaying that event is enough; the
        // static Health.OnHurt (crosshair marker) stays shooter-only.
        private const float HurtFxThrottleSeconds = 0.15f;
        private readonly HashSet<ulong> _playedHurtFxEventIds = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> _hurtFxNextAt = new Dictionary<ulong, float>();

        private void UpdateCombatD5()
        {
            Active = this;
            if (_client == null)
                return;

            ApplyCombatEventsV4();
            SendPlayerStatsIfNeeded();
        }

        private bool SendHitEventIfPlaying(HitEvent hit)
        {
            if (_client == null || _client.PlayerId == 0)
                return false;
            _client.SendHitEvent(hit);
            return true;
        }

        private readonly HashSet<uint> _aiLoadoutSent = new HashSet<uint>();

        /// <summary>Report the AI's generated equipment once per spawn (CAiLoadout).</summary>
        private void SendAiLoadoutIfNeeded(uint aiId, object character)
        {
            if (character == null || _client == null || _client.PlayerId == 0)
                return;
            if (!_aiLoadoutSent.Add(aiId))
                return;

            try
            {
                var loadout = new AiLoadout
                {
                    AiId = aiId,
                    RaidEpoch = _aiSpawnEpoch,
                    WeaponId = ReadSlotItemId(character, "PrimWeaponSlot"),
                    BulletId = 0, // resolved server-side from the weapon's default bullet
                    ArmorId = ReadSlotItemId(character, "ArmorSlot"),
                    HelmetId = ReadSlotItemId(character, "HelmatSlot"),
                };

                _client.SendAiLoadout(loadout);
                Log($"[d5] ai loadout ai={aiId} weapon={loadout.WeaponId} armor={loadout.ArmorId} " +
                    $"helmet={loadout.HelmetId}");
            }
            catch (Exception ex)
            {
                Log("[d5] ai loadout failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static uint ReadSlotItemId(object character, string slotMethod)
        {
            try
            {
                var slot = character.GetType()
                    .GetMethod(slotMethod, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                    ?.Invoke(character, null);
                var content = slot?.GetType().GetProperty("Content")?.GetValue(slot);
                if (content == null)
                    return 0;
                var typeId = content.GetType().GetProperty("TypeID")?.GetValue(content);
                return typeId is int id && id > 0 ? (uint)id : 0;
            }
            catch
            {
                return 0;
            }
        }

        private void ClearAiLoadout(uint aiId) => _aiLoadoutSent.Remove(aiId);

        private void ApplyCombatEventsV4()
        {
            // Drain every event: one server tick can carry several hits (an explosion hits
            // multiple victims), and dropping all but the last made the local player miss
            // its own authoritative HP update.
            while (_client.CombatEventV4Queue.Count > 0)
                ApplyCombatEventV4(_client.CombatEventV4Queue.Dequeue());
        }

        private void ApplyCombatEventV4(CombatEventV4 combatEvent)
        {
            try
            {
                if (combatEvent.SourceKind == 0 && combatEvent.SourceId != 0 &&
                    _client.PlayerId != 0 && combatEvent.SourceId != _client.PlayerId &&
                    !_client.PlayerStateSamples.ContainsKey((ushort)combatEvent.SourceId))
                    NoteRemotePlayerAttack((ushort)combatEvent.SourceId, combatEvent.WeaponId);
                if (combatEvent.TargetKind == 0)
                {
                    if (_client.PlayerId == 0)
                    {
                        if (Time.unscaledTime >= _nextSelfApplyDropLog)
                        {
                            _nextSelfApplyDropLog = Time.unscaledTime + 2f;
                            Log($"[d5] self event dropped: playerId unknown target={combatEvent.TargetId} " +
                                $"hp={combatEvent.HpAfter} dmg={combatEvent.Damage}");
                        }
                        return;
                    }

                    if (combatEvent.TargetId == _client.PlayerId)
                    {
                        var main = LevelManager.Instance != null ? LevelManager.Instance.MainCharacter : null;
                        if (main == null || main.Health == null)
                        {
                            if (Time.unscaledTime >= _nextSelfApplyDropLog)
                            {
                                _nextSelfApplyDropLog = Time.unscaledTime + 2f;
                                Log($"[d5] self event dropped: main={main != null} " +
                                    $"health={(main != null && main.Health != null)} " +
                                    $"hp={combatEvent.HpAfter} dmg={combatEvent.Damage}");
                            }
                            return;
                        }

                        main.Health.SetHealth(combatEvent.HpAfter);
                        // Contract 2.16.0: the authoritative value is now the sampling
                        // baseline; otherwise the local drop would be reported back as
                        // self damage and the server would apply it twice.
                        NoteAuthoritativeLocalHp(combatEvent.HpAfter);
                        if (combatEvent.Type == 2)
                        {
                            // healed: no hurt feedback, no armor wear, never a death check
                            Log($"[d5] self healed hp={combatEvent.HpAfter}/{combatEvent.MaxHp}");
                        }
                        else
                        {
                            ApplyCombatFeedback(main.Health, combatEvent);
                            ApplyArmorWear(main, combatEvent);
                            if (combatEvent.HpAfter == 0)
                                OnLocalPlayerDied("hp zero");
                            Log($"[d5] self hp={combatEvent.HpAfter} dmg={combatEvent.Damage} " +
                                $"crit={combatEvent.Crit} flags=0x{combatEvent.Flags:X2}");
                        }
                    }
                    else
                    {
                        Log($"[d5] remote player event target={combatEvent.TargetId} " +
                            $"type={combatEvent.Type} hp={combatEvent.HpAfter}");
                        // Other observers still see the victim's native hurt FX (M2-v12).
                        if (combatEvent.Type == 0 &&
                            _remotes.TryGetValue((ushort)combatEvent.TargetId, out var mirror) &&
                            mirror != null)
                        {
                            var mirrorControl =
                                mirror.GetComponentInChildren<CharacterMainControl>(true);
                            PlayVictimHurtFx(mirrorControl != null ? mirrorControl.Health : null,
                                combatEvent);
                        }
                    }

                    return;
                }

                if (combatEvent.TargetKind != 1)
                    return;

                Health health = null;
                if (_realAi.TryGetValue(combatEvent.TargetId, out var realAi) && realAi != null)
                    health = FindAiHealth(realAi) as Health;
                else if (_aiGhosts.TryGetValue(combatEvent.TargetId, out var replica) && replica != null)
                    health = FindAiHealth(replica) as Health;

                if (health != null)
                {
                    health.SetHealth(combatEvent.HpAfter);
                    // The shooter keeps the native hit-marker feedback; every other observer
                    // replays only the victim's hurt FX, never the crosshair marker.
                    if (combatEvent.SourceKind == 0 &&
                        _client.PlayerId != 0 &&
                        combatEvent.SourceId == _client.PlayerId)
                        ApplyCombatFeedback(health, combatEvent);
                    else if (combatEvent.Type == 0)
                        PlayVictimHurtFx(health, combatEvent);
                    Log($"[d5] ai {combatEvent.TargetId} hp={combatEvent.HpAfter} dmg={combatEvent.Damage} " +
                        $"crit={combatEvent.Crit} by {combatEvent.SourceKind}:{combatEvent.SourceId}");
                }
                else
                {
                    Log($"[d5] ai {combatEvent.TargetId} event without target health (not spawned yet)");
                }
            }
            catch (Exception ex)
            {
                Log("[d5] apply failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Native Health.Hurt wears the equipped body/head armor by damageInfo.armorBreak.
        /// The D5 client path only applies authoritative HP, so reproduce the visible wear
        /// here; the Item.Durability setter then sends CItemDurability to the server.
        /// </summary>
        private void ApplyArmorWear(CharacterMainControl main, CombatEventV4 combatEvent)
        {
            if (main == null || combatEvent.Damage == 0)
                return;
            try
            {
                var headshot = (combatEvent.Flags & CombatEventV4.FlagHeadshot) != 0;
                var armor = headshot ? main.GetHelmatItem() : main.GetArmorItem();
                if (armor == null)
                    return;
                var loss = Mathf.Max(0.5f, combatEvent.Damage * 0.1f);
                armor.Durability = Mathf.Max(0f, armor.Durability - loss);
                Log($"[durability] armor wear headshot={headshot} item={armor.TypeID} " +
                    $"loss={loss:F2} left={armor.Durability:F2}");
            }
            catch (Exception ex)
            {
                Log($"[durability] armor wear failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// A6: replay the server hit as a local "hurt event" only. HP was already set from
        /// hpAfter, so we must not run the native Hurt (damage) path again.
        /// </summary>
        private void ApplyCombatFeedback(Health health, CombatEventV4 combatEvent)
        {
            if (health == null)
                return;

            try
            {
                var info = BuildHurtInfo(health, combatEvent);
                health.OnHurtEvent?.Invoke(info);

                var hurtField = typeof(Health).GetField("OnHurt",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                (hurtField?.GetValue(null) as Delegate)?.DynamicInvoke(health, info);
            }
            catch (Exception ex)
            {
                Log("[d5] feedback failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// M2-v12: replay only the victim's hurt FX (native <c>HurtVisual</c> listens to the
        /// instance event). Used on observers that are neither the shooter nor the victim, so
        /// the crosshair marker (static <c>Health.OnHurt</c>) stays local to the shooter.
        /// </summary>
        private void PlayVictimHurtFx(Health health, CombatEventV4 combatEvent)
        {
            if (health == null || combatEvent == null)
                return;
            if (combatEvent.EventId != 0)
            {
                if (!_playedHurtFxEventIds.Add(combatEvent.EventId))
                    return;
                if (_playedHurtFxEventIds.Count > 512)
                    _playedHurtFxEventIds.Clear();
            }

            var key = ((ulong)combatEvent.TargetKind << 32) | combatEvent.TargetId;
            var now = Time.unscaledTime;
            if (_hurtFxNextAt.TryGetValue(key, out var next) && now < next)
                return;
            _hurtFxNextAt[key] = now + HurtFxThrottleSeconds;

            try
            {
                health.OnHurtEvent?.Invoke(BuildHurtInfo(health, combatEvent));
            }
            catch (Exception ex)
            {
                Log("[d5] victim hurt fx failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private DamageInfo BuildHurtInfo(Health health, CombatEventV4 combatEvent)
        {
            var attacker = ResolveAttacker(combatEvent);
            var info = new DamageInfo(attacker)
            {
                damageValue = combatEvent.Damage,
                finalDamage = combatEvent.Damage,
                crit = combatEvent.Crit,
                critRate = combatEvent.Crit > 0 ? 1f : 0f,
                fromWeaponItemID = unchecked((int)combatEvent.WeaponId),
                damagePoint = new Vector3(
                    combatEvent.HitX / 100f, combatEvent.HitY / 100f, combatEvent.HitZ / 100f),
                damageNormal = attacker != null
                    ? (health.transform.position - attacker.transform.position).normalized
                    : Vector3.up,
                isExplosion = (combatEvent.Flags & CombatEventV4.FlagExplosion) != 0,
                damageType = (combatEvent.Flags & CombatEventV4.FlagRealDamage) != 0
                    ? DamageTypes.realDamage
                    : DamageTypes.normal,
                toDamageReceiver = health.GetComponent<DamageReceiver>(),
            };
            info.elementFactors.Add(
                new ElementFactor((ElementTypes)Mathf.Clamp(combatEvent.Element, 0, 6), 1f));
            return info;
        }

        private CharacterMainControl ResolveAttacker(CombatEventV4 combatEvent)
        {
            try
            {
                if (combatEvent.SourceKind == 0)
                {
                    if (combatEvent.SourceId == _client.PlayerId)
                        return LevelManager.Instance != null ? LevelManager.Instance.MainCharacter : null;
                    if (_remotes.TryGetValue((ushort)combatEvent.SourceId, out var remote) && remote != null)
                        return remote.GetComponentInChildren<CharacterMainControl>(true);
                }
                else if (combatEvent.SourceKind == 1)
                {
                    if (_realAi.TryGetValue(combatEvent.SourceId, out var ai) && ai != null)
                        return ai.GetComponentInChildren<CharacterMainControl>(true);
                    if (_aiGhosts.TryGetValue(combatEvent.SourceId, out var ghost) && ghost != null)
                        return ghost.GetComponentInChildren<CharacterMainControl>(true);
                }
            }
            catch
            {
                // attacker is optional feedback context
            }

            return null;
        }

        private void SendPlayerStatsIfNeeded()
        {
            if (!_inRaidScene || _client.PlayerId == 0 || Time.time < _nextPlayerStatsTry)
                return;
            if (!_playerStatsDirty && _playerStatsSentEpoch == _aiSpawnEpoch)
                return;

            _nextPlayerStatsTry = Time.time + 2f;
            var main = LevelManager.Instance != null ? LevelManager.Instance.MainCharacter : null;
            if (main == null)
                return;

            var stats = BuildPlayerStats(main);
            if (stats == null)
                return;

            _client.SendPlayerStats(stats);
            _playerStatsSentEpoch = _aiSpawnEpoch;
            _playerStatsDirty = false;
            Log($"[d5] player stats sent epoch={_aiSpawnEpoch} gunDmg={stats.GunDamageMultiplier:F2} " +
                $"critGain={stats.GunCritRateGain:F2} critDmg={stats.GunCritDamageGain:F2} " +
                $"armor={stats.BodyArmor:F2}/{stats.HeadArmor:F2} maxHp={stats.MaxHealth:F0}");
        }

        private static PlayerStats BuildPlayerStats(CharacterMainControl main)
        {
            try
            {
                var characterItem = main.CharacterItem;
                var stats = new PlayerStats
                {
                    GunDamageMultiplier = ReadStat(characterItem, "GunDamageMultiplier", 1f),
                    GunCritRateGain = ReadStat(characterItem, "GunCritRateGain", 0f),
                    GunCritDamageGain = ReadStat(characterItem, "GunCritDamageGain", 0f),
                    MeleeDamageMultiplier = ReadStat(characterItem, "MeleeDamageMultiplier", 1f),
                    MeleeCritRateGain = ReadStat(characterItem, "MeleeCritRateGain", 0f),
                    MeleeCritDamageGain = ReadStat(characterItem, "MeleeCritDamageGain", 0f),
                    BodyArmor = main.Health != null ? main.Health.BodyArmor : 0f,
                    HeadArmor = main.Health != null ? main.Health.HeadArmor : 0f,
                    MaxHealth = main.Health != null ? main.Health.MaxHealth : ReadStat(characterItem, "MaxHealth", 100f),
                };

                var elementNames = new[]
                {
                    "ElementFactor_Physics", "ElementFactor_Fire", "ElementFactor_Poison",
                    "ElementFactor_Electricity", "ElementFactor_Space", "ElementFactor_Ghost",
                    "ElementFactor_Ice",
                };
                for (var i = 0; i < elementNames.Length; i++)
                    stats.ElementFactor[i] = ReadStat(characterItem, elementNames[i], 1f);

                return stats;
            }
            catch (Exception ex)
            {
                Log("[d5] player stats build failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static float ReadStat(object item, string key, float fallback)
        {
            if (item == null)
                return fallback;

            try
            {
                var stats = item.GetType().GetProperty("Stats")?.GetValue(item) as IEnumerable;
                if (stats == null)
                    return fallback;
                foreach (var stat in stats)
                {
                    if (stat == null)
                        continue;
                    var statType = stat.GetType();
                    if (!(statType.GetProperty("Key")?.GetValue(stat) is string statKey) || statKey != key)
                        continue;
                    var value = statType.GetProperty("Value")?.GetValue(stat);
                    return value is float single ? single : fallback;
                }
            }
            catch
            {
                // stat lookup is best effort
            }

            return fallback;
        }
    }
}
