using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DuckovCoop.Client;
using HarmonyLib;
using ItemStatsSystem;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// ADR-022 / wire v5 client side: CShootFact, CUseConsumable, SRaidRules
    /// (baseStatsHash + client-side rules), SPlayerBuff, and the 16-float CPlayerStats
    /// fields (maxHealth lives in <see cref="CoopRuntime.CombatD5"/>).
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private const byte ShootFactMelee = 1 << 0;
        private const byte RaidRuleAdvancedDebuff = 1 << 0;
        private const byte RaidRuleFogOfWar = 1 << 1;
        private const byte RaidRuleSpawnDeadbody = 1 << 2;

        private byte[] _baseStatsHash;
        private uint _v5AttackId;
        private ulong _v5ConsumableCorrelation;
        private int _lastRaidRulesCount;
        private int _lastPlayerBuffCount;
        private readonly Dictionary<ulong, PlayerBuff> _v5PlayerBuffs = new Dictionary<ulong, PlayerBuff>();
        private bool _v5FactsSubscribed;

        private void UpdateV5()
        {
            if (_client == null)
                return;

            if (_client.RaidRulesCount != _lastRaidRulesCount)
            {
                _lastRaidRulesCount = _client.RaidRulesCount;
                ApplyRaidRules(_client.LatestRaidRules);
            }

            if (_client.PlayerBuffCount != _lastPlayerBuffCount)
            {
                _lastPlayerBuffCount = _client.PlayerBuffCount;
                while (_client.PlayerBuffEvents.Count > 0)
                    HandlePlayerBuff(_client.PlayerBuffEvents.Dequeue());
            }
        }

        // ---------------------------------------------------------------- SRaidRules

        private void LoadBaseStatsHash()
        {
            try
            {
                var directory = Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location) ?? string.Empty;
                var path = Path.Combine(directory, "base_stats.hash");
                if (!File.Exists(path))
                {
                    Log($"[v5] base_stats.hash missing: {path}");
                    return;
                }

                var text = File.ReadAllText(path).Trim();
                if (text.Length != 64)
                {
                    Log($"[v5] base_stats.hash malformed (len={text.Length})");
                    return;
                }

                var bytes = new byte[32];
                for (var i = 0; i < 32; i++)
                    bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
                _baseStatsHash = bytes;
                Log($"[v5] base_stats.hash loaded: {text.ToLowerInvariant()}");
            }
            catch (Exception ex)
            {
                Log($"[v5] base_stats.hash load failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ApplyRaidRules(RaidRules rules)
        {
            if (rules == null)
                return;

            var match = _baseStatsHash != null && _baseStatsHash.Length == 32 &&
                        rules.BaseStatsHash != null && rules.BaseStatsHash.Length == 32;
            for (var i = 0; match && i < 32; i++)
                match = _baseStatsHash[i] == rules.BaseStatsHash[i];

            Log($"[v5] raid rules ruleset={rules.RulesetId} damage={rules.DamageFactorToPlayer:F2} " +
                $"enemyHp={rules.EnemyHealthFactor:F2} attack={rules.EnemyAttackTimeFactor:F2}/" +
                $"{rules.EnemyAttackTimeSpaceFactor:F2} reaction={rules.EnemyReactionTimeFactor:F2} " +
                $"flags=0x{rules.Flags:X2} recoil={rules.RecoilMultiplier:F2} weather={rules.Weather} " +
                $"time={rules.TimeOfDayMinutes} seed={rules.WeatherSeed} " +
                $"baseHash={(match ? "match" : "MISMATCH")}");

            if (!match)
            {
                Log("[v5] base stats hash mismatch; server/client attribute tables differ " +
                    "(server will recompute v5 stats)");
            }

            ApplyRulesetToLocalGame(rules);
            ApplyWeatherAndTime(rules);
        }

        /// <summary>
        /// Overwrite the local <see cref="LevelManager.Rule"/> object so native code paths
        /// (fog of war, recoil, spawn dead body, advanced debuffs) follow the server values.
        /// </summary>
        private void ApplyRulesetToLocalGame(RaidRules rules)
        {
            try
            {
                var rule = LevelManager.Rule;
                if (rule == null)
                {
                    Log("[v5] LevelManager.Rule is null; cannot apply rules");
                    return;
                }

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var type = rule.GetType();
                SetField(type, rule, flags, "damageFactor_ToPlayer", rules.DamageFactorToPlayer);
                SetField(type, rule, flags, "enemyHealthFactor", rules.EnemyHealthFactor);
                SetField(type, rule, flags, "enemyAttackTimeFactor", rules.EnemyAttackTimeFactor);
                SetField(type, rule, flags, "enemyAttackTimeSpaceFactor", rules.EnemyAttackTimeSpaceFactor);
                SetField(type, rule, flags, "enemyReactionTimeFactor", rules.EnemyReactionTimeFactor);
                SetField(type, rule, flags, "spawnDeadBody", (rules.Flags & RaidRuleSpawnDeadbody) != 0);
                SetField(type, rule, flags, "fogOfWar", (rules.Flags & RaidRuleFogOfWar) != 0);
                SetField(type, rule, flags, "advancedDebuffMode", (rules.Flags & RaidRuleAdvancedDebuff) != 0);
                SetField(type, rule, flags, "saveDeadbodyCount", (int)rules.SaveDeadbodyCount);
                SetField(type, rule, flags, "recoilMultiplier", rules.RecoilMultiplier);
                Log("[v5] local Ruleset overridden from SRaidRules");
            }
            catch (Exception ex)
            {
                Log($"[v5] rules apply failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SetField(Type type, object instance, BindingFlags flags, string name, object value)
        {
            var field = type.GetField(name, flags);
            if (field == null)
            {
                Log($"[v5] rules field missing: {name}");
                return;
            }

            field.SetValue(instance, value);
        }

        private void ApplyWeatherAndTime(RaidRules rules)
        {
            try
            {
                // Keep the native Weather enum value as frozen in ADR-022 (22 = Snow).
                Duckov.Weathers.WeatherManager.SetForceWeather(true, (Duckov.Weathers.Weather)rules.Weather);
                Log($"[v5] weather forced to {(Duckov.Weathers.Weather)rules.Weather}");
            }
            catch (Exception ex)
            {
                Log($"[v5] weather apply failed: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                var controller = LevelManager.Instance != null ? LevelManager.Instance.TimeOfDayController : null;
                if (controller == null)
                    return;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var field = controller.GetType().GetField("time", flags);
                if (field == null)
                {
                    Log("[v5] TimeOfDayController.time missing");
                    return;
                }

                field.SetValue(controller, rules.TimeOfDayMinutes / 60f);
                Log($"[v5] time of day set to {rules.TimeOfDayMinutes} minutes");
            }
            catch (Exception ex)
            {
                Log($"[v5] time apply failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void HandlePlayerBuff(PlayerBuff buff)
        {
            if (buff == null)
                return;

            var key = ((ulong)buff.PlayerId << 32) | buff.BuffId;
            if (buff.Op == PlayerBuff.OpRemove)
                _v5PlayerBuffs.Remove(key);
            else
                _v5PlayerBuffs[key] = buff;

            var op = buff.Op == PlayerBuff.OpAdd ? "add" : buff.Op == PlayerBuff.OpUpdate ? "update" : "remove";
            Log($"[v5] buff {op} player={buff.PlayerId} buff={buff.BuffId} layers={buff.Layers} " +
                $"remaining={buff.RemainingMs}ms tracked={_v5PlayerBuffs.Count}");
        }

        // ---------------------------------------------------------------- CShootFact

        internal void NoteShootFact(CharacterMainControl shooter, DuckovItemAgent agent, bool melee)
        {
            if (shooter == null || _client == null || _client.State != ClientState.InGame || !_inRaidScene)
                return;

            byte sourceKind;
            uint sourceId;
            if (shooter.IsMainCharacter || shooter == CharacterMainControl.Main)
            {
                sourceKind = 0;
                sourceId = _client.PlayerId;
            }
            else if (TryResolveAiId(shooter.gameObject, out var aiId) && OwnsAi(aiId))
            {
                sourceKind = 1;
                sourceId = aiId;
            }
            else
            {
                return;
            }

            var weapon = agent != null ? agent.Item : null;
            var weaponId = weapon != null ? (uint)weapon.TypeID : 0u;
            var ammoId = melee ? 0u : ResolveFactAmmoId(shooter);
            var fact = new ShootFact
            {
                SourceKind = sourceKind,
                SourceId = sourceId,
                WeaponId = weaponId,
                AmmoId = ammoId,
                AttackId = ++_v5AttackId,
                Flags = melee ? ShootFactMelee : (byte)0,
            };

            _client.SendShootFact(fact);
            Log($"[v5] shoot fact kind={sourceKind} source={sourceId} weapon={weaponId} ammo={ammoId} " +
                $"attack={fact.AttackId} melee={melee}");
        }

        private static uint ResolveFactAmmoId(CharacterMainControl shooter)
        {
            try
            {
                var agent = shooter != null ? shooter.CurrentHoldItemAgent : null;
                if (agent == null || !agent.GetType().Name.Contains("Gun"))
                    return 0;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var bullet = agent.GetType().GetProperty("BulletItem", flags)?.GetValue(agent);
                var typeId = bullet?.GetType().GetProperty("TypeID", flags)?.GetValue(bullet);
                return typeId is int id && id > 0 ? (uint)id : 0;
            }
            catch
            {
                return 0;
            }
        }

        // ---------------------------------------------------------------- CUseConsumable

        private void SubscribeV5Facts()
        {
            if (_v5FactsSubscribed)
                return;
            try
            {
                CA_UseItem.OnItemUsedByPlayer -= OnItemUsedByPlayer;
                CA_UseItem.OnItemUsedByPlayer += OnItemUsedByPlayer;
                _v5FactsSubscribed = true;
            }
            catch (Exception ex)
            {
                Log($"[v5] consumable hook failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void UnsubscribeV5Facts()
        {
            if (!_v5FactsSubscribed)
                return;
            try
            {
                CA_UseItem.OnItemUsedByPlayer -= OnItemUsedByPlayer;
            }
            catch
            {
                // shutting down
            }

            _v5FactsSubscribed = false;
        }

        private static void OnItemUsedByPlayer(Item item) => Active?.NoteConsumableUsed(item);

        /// <param name="flags">
        /// Contract 2.16.0: <see cref="UseConsumable.FlagSettledByItemConsume"/> when the same
        /// use was already reported as CItemConsume (which settles the HP on its own).
        /// </param>
        internal void NoteConsumableUsed(Item item, byte flags = 0)
        {
            if (item == null || _client == null || _client.State != ClientState.InGame || !_inRaidScene)
                return;

            var message = new UseConsumable
            {
                CorrelationId = ++_v5ConsumableCorrelation,
                ItemId = (uint)item.TypeID,
                Slot = FindConsumableSlot(item),
                Flags = flags,
            };
            _client.SendUseConsumable(message);
            Log($"[v5] consumable used item={message.ItemId} slot=0x{message.Slot:X2} " +
                $"flags=0x{message.Flags:X2} corr={message.CorrelationId}");
        }

        /// <summary>Character-slot index when the used item is equipped, otherwise 0xFF (ADR-022).</summary>
        private static byte FindConsumableSlot(Item item)
        {
            try
            {
                var main = CharacterMainControl.Main;
                var characterItem = main != null ? main.CharacterItem : null;
                if (characterItem == null)
                    return 0xFF;

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var slots = characterItem.GetType().GetProperty("Slots", flags)?.GetValue(characterItem) as System.Collections.IEnumerable;
                if (slots == null)
                    return 0xFF;

                var map = new Dictionary<string, byte>(StringComparer.Ordinal)
                {
                    ["PrimWeaponSlot"] = 0,
                    ["SecWeaponSlot"] = 1,
                    ["MeleeWeaponSlot"] = 2,
                    ["ArmorSlot"] = 3,
                    ["HelmatSlot"] = 4,
                    ["FaceMaskSlot"] = 5,
                    ["BackpackSlot"] = 6,
                    ["HeadsetSlot"] = 7,
                    ["Totem1"] = 8,
                    ["Totem2"] = 9,
                };

                foreach (var slot in slots)
                {
                    if (slot == null)
                        continue;
                    var content = slot.GetType().GetProperty("Content", flags)?.GetValue(slot);
                    if (!ReferenceEquals(content, item))
                        continue;
                    var key = slot.GetType().GetProperty("Key", flags)?.GetValue(slot) as string;
                    if (key != null && map.TryGetValue(key, out var index))
                        return index;
                }
            }
            catch
            {
                // slot is diagnostic only; 0xFF is the frozen "unknown" value
            }

            return 0xFF;
        }
    }

    /// <summary>
    /// v5 facts patched manually with a dedicated Harmony id. Do NOT use [HarmonyPatch] here:
    /// both AiReplicaGuard.Init and GateAProbe.Init call Harmony.PatchAll(assembly), which would
    /// apply attribute-based patches twice (one CShootFact per trigger would become two).
    /// </summary>
    internal static class V5Patches
    {
        private const string HarmonyId = "duckovcoop.v5";
        private static Harmony _harmony;

        internal static void Init()
        {
            if (_harmony != null)
                return;

            try
            {
                _harmony = new Harmony(HarmonyId);
                var shoot = AccessTools.Method(typeof(CharacterMainControl), "TriggerShootEvent");
                if (shoot != null)
                {
                    _harmony.Patch(shoot,
                        prefix: new HarmonyMethod(typeof(V5Patches), nameof(ShootPrefix)));
                }
                else
                {
                    CoopRuntime.Log("[v5] TriggerShootEvent not found");
                }

                var attack = AccessTools.Method(typeof(CharacterMainControl), "Attack");
                if (attack != null)
                {
                    _harmony.Patch(attack,
                        postfix: new HarmonyMethod(typeof(V5Patches), nameof(MeleePostfix)));
                }
                else
                {
                    CoopRuntime.Log("[v5] Attack not found");
                }

                CoopRuntime.Log("[v5] harmony patches applied (single instance)");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log($"[v5] patch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void ShootPrefix(CharacterMainControl __instance, DuckovItemAgent shootByAgent)
        {
            try
            {
                CoopRuntime.Active?.NoteShootFact(__instance, shootByAgent, melee: false);
            }
            catch
            {
                // facts are best effort
            }
        }

        private static void MeleePostfix(CharacterMainControl __instance, bool __result)
        {
            if (!__result)
                return;
            try
            {
                CoopRuntime.Active?.NoteShootFact(__instance, __instance.GetMeleeWeapon(), melee: true);
            }
            catch
            {
                // facts are best effort
            }
        }
    }
}
