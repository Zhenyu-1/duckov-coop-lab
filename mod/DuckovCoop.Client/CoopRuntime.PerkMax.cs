using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Duckov.PerkTrees;
using Duckov.PerkTrees.Behaviours;
using HarmonyLib;
using ItemStatsSystem;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// Multiplayer uses a max-level skill tree on the client.
    ///
    /// Native flow (single player): every <see cref="PerkBehaviour"/> subscribes to
    /// <c>LevelManager.OnLevelInitialized</c> in Awake and re-applies its effect
    /// (OnUnlocked/OnLocked) for the freshly created character item. Reusing that flow for the
    /// forced max state was unreliable:
    ///   * the event only reaches behaviours whose GameObject already Awakened, so forcing
    ///     unlocks after level init left the character "blank" (MaxHealth 40);
    ///   * firing <c>onUnlockStateChanged</c> by hand and also letting the native level-init
    ///     run applied the same stat entries twice (MaxHealth 162).
    ///
    /// This class therefore separates the two concerns:
    ///   * unlock state: write <c>Perk._unlocked</c> silently (no event). Perk tree UI, storage,
    ///     blueprints, facilities, ... keep working through the native behaviours that run on
    ///     level init; a cheap guard re-unlocks if native save data resets the flags.
    ///   * character stats: aggregate every <see cref="ModifyCharacterStatsBase"/> entry and
    ///     write it into <c>CharacterItem.Stats</c> once per character item, exactly like the
    ///     native handler does (raw entry value added to <c>Stat.BaseValue</c>; the
    ///     <c>percentage</c> flag is display-only). Native stat behaviours are skipped while
    ///     active so nothing is applied twice.
    ///
    /// Original unlock states are restored on exit, and PerkTree.Save is suppressed while
    /// active so the local save is never rewritten with the forced states.
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private static readonly FieldInfo PerkUnlockedField = typeof(Perk).GetField(
            "_unlocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly FieldInfo PerkStatsEntriesField = typeof(ModifyCharacterStatsBase).GetField(
            "entries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private bool _perkMaxApplied;
        private float _nextPerkMaxAttempt;
        private float _nextPerkStatAttempt;
        private float _nextPerkMaxNoStatLog;
        private PerkTreeManager _perkMaxManager;
        private Item _perkMaxStatItem;
        private readonly List<Perk> _perkMaxPerks = new List<Perk>();
        private readonly List<bool> _perkMaxOriginal = new List<bool>();
        private readonly List<KeyValuePair<Perk, ModifyCharacterStatsBase>> _perkMaxStatBehaviours =
            new List<KeyValuePair<Perk, ModifyCharacterStatsBase>>();

        /// <summary>
        /// Contract 2.16.0: every player has 100 max HP. The server pins it and the client
        /// normalizes its own stat collection to the same number, so the HUD, the friendly
        /// bars and the server's hp/maxHp all agree.
        /// </summary>
        private const float PlayerMaxHp = 100f;

        private float _lastReportedLocalMaxHp = -1f;

        /// <summary>Instance id of the character item whose stats are fully applied.</summary>
        internal static int PerkMaxItemId;

        internal static bool PerkMaxActive { get; private set; }

        private void UpdatePerkMax()
        {
            // If the perk tree manager is replaced (scene/tree rebuild), rediscover the trees.
            var manager = PerkTreeManager.Instance;
            if (_perkMaxApplied && !ReferenceEquals(_perkMaxManager, manager))
            {
                Log("[perkmax] perk tree manager changed; rediscovering");
                _perkMaxApplied = false;
            }

            if (!_perkMaxApplied)
            {
                if (Time.unscaledTime < _nextPerkMaxAttempt)
                    return;
                _nextPerkMaxAttempt = Time.unscaledTime + 1f;
                UnlockAllPerks();
                if (!_perkMaxApplied)
                    return;
            }
            else
            {
                EnforcePerkUnlockStates();
            }

            if (Time.unscaledTime < _nextPerkStatAttempt)
                return;
            _nextPerkStatAttempt = Time.unscaledTime + 0.5f;
            ApplyMaxPerkStats();
            // Runs even when the perk injection above was skipped: the cap is a contract,
            // not a perk side effect.
            NormalizeLocalMaxHealth();
        }

        /// <summary>
        /// Contract 2.16.0 §3.4.4: pin the character's MaxHealth to 100.
        ///
        /// The perk tree is forced to max level, so the native total differs per account
        /// (A=101, B=86) and the same HP rendered a different bar width on each side. The
        /// delta is written into <c>Stat.BaseValue</c> (the same mechanism the perk entries
        /// use) and repeated a few times because PercentageAdd/PercentageMultiply modifiers
        /// apply after the base value.
        /// </summary>
        private void NormalizeLocalMaxHealth()
        {
            var main = CharacterMainControl.Main;
            var item = main != null ? main.CharacterItem : null;
            if (item == null)
                return;
            if (!LevelManager.LevelInited || LevelManager.LevelInitializing)
                return;

            StatCollection stats;
            try
            {
                stats = item.Stats;
            }
            catch
            {
                return;
            }

            var stat = stats != null ? stats.GetStat("MaxHealth") : null;
            if (stat == null)
                return;

            var corrected = false;
            for (var i = 0; i < 3; i++)
            {
                var delta = PlayerMaxHp - stat.Value;
                if (Mathf.Abs(delta) < 0.01f)
                    break;
                stat.BaseValue += delta;
                corrected = true;
            }

            Health health = null;
            try
            {
                health = main.Health;
            }
            catch
            {
                // health is optional for the stat normalization
            }

            if (health != null)
            {
                // Used when the character item is gone (death destroys it): the HUD must
                // still say 100 instead of the prefab default.
                var defaultField = typeof(Health).GetField("defaultMaxHealth",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (defaultField != null)
                {
                    var currentDefault = defaultField.GetValue(health) is int value ? value : 0;
                    if (currentDefault != (int)PlayerMaxHp)
                        defaultField.SetValue(health, (int)PlayerMaxHp);
                }

                if (health.CurrentHealth > PlayerMaxHp)
                    health.SetHealth(PlayerMaxHp);
            }

            if (corrected || _lastReportedLocalMaxHp < 0f)
            {
                _lastReportedLocalMaxHp = PlayerMaxHp;
                Log($"[maxhp] local MaxHealth normalized to 100 " +
                    $"(base={stat.BaseValue:F0} value={stat.Value:F0} " +
                    $"hp={(health != null ? health.CurrentHealth : -1f):F0})");
            }
        }

        private void UnlockAllPerks()
        {
            var manager = PerkTreeManager.Instance;
            if (manager == null || manager.perkTrees == null)
                return;

            if (PerkUnlockedField == null || PerkStatsEntriesField == null)
            {
                Log("[perkmax] required fields missing; max-level perks disabled");
                return;
            }

            _perkMaxPerks.Clear();
            _perkMaxOriginal.Clear();
            _perkMaxStatBehaviours.Clear();

            var seenTrees = new HashSet<int>();
            var treeCount = 0;
            foreach (var tree in manager.perkTrees)
            {
                if (tree == null || !seenTrees.Add(tree.GetInstanceID()))
                    continue;
                treeCount++;
                foreach (var perk in tree.Perks)
                {
                    if (perk == null)
                        continue;
                    _perkMaxPerks.Add(perk);
                    _perkMaxOriginal.Add(perk.Unlocked);
                    // Silent: firing onUnlockStateChanged here would apply stat entries right
                    // away and the native level-init would then apply them a second time.
                    PerkUnlockedField.SetValue(perk, true);
                    foreach (var behaviour in perk.GetComponents<PerkBehaviour>())
                    {
                        if (behaviour is ModifyCharacterStatsBase stats)
                            _perkMaxStatBehaviours.Add(new KeyValuePair<Perk, ModifyCharacterStatsBase>(perk, stats));
                    }
                }
            }

            if (_perkMaxPerks.Count == 0)
                return;

            _perkMaxManager = manager;
            _perkMaxApplied = true;
            PerkMaxActive = true;
            Log($"[perkmax] unlocked perks={_perkMaxPerks.Count} trees={treeCount} " +
                $"statBehaviours={_perkMaxStatBehaviours.Count} (stats injected on character item)");
        }

        /// <summary>
        /// Cheap guard: a native PerkTree.Load/SetupSaveData can reset the forced flags.
        /// Re-write them without firing events (stat entries are injected separately).
        /// </summary>
        private void EnforcePerkUnlockStates()
        {
            if (PerkUnlockedField == null)
                return;

            var relocked = 0;
            foreach (var perk in _perkMaxPerks)
            {
                if (perk == null || perk.Unlocked)
                    continue;
                PerkUnlockedField.SetValue(perk, true);
                relocked++;
            }

            if (relocked > 0)
                Log($"[perkmax] re-unlocked {relocked} perks (native state was reset)");
        }

        /// <summary>
        /// Apply the aggregated max-level stat entries to the current character item exactly
        /// once. Mirrors ModifyCharacterStatsBase.OnUnlocked: raw entry value is added to
        /// Stat.BaseValue and an unknown key stops that behaviour's entry list.
        /// </summary>
        private void ApplyMaxPerkStats()
        {
            var main = CharacterMainControl.Main;
            var item = main != null ? main.CharacterItem : null;
            if (item == null)
                return;
            if (ReferenceEquals(item, _perkMaxStatItem))
                return;

            // Wait for the level init to finish: the native character item (and its stats) is
            // only final after LevelManager.LevelInited flips.
            if (!LevelManager.LevelInited || LevelManager.LevelInitializing)
                return;

            StatCollection stats;
            try
            {
                stats = item.Stats;
            }
            catch
            {
                return;
            }

            if (stats == null)
                return;

            var health = stats.GetStat("MaxHealth");
            var gunDamage = stats.GetStat("GunDamageMultiplier");
            if (health == null || gunDamage == null)
                return; // stat collection not built yet

            var deltas = new Dictionary<string, float>(System.StringComparer.Ordinal);
            var statPerks = new HashSet<int>();
            var behaviours = 0;
            var entries = 0;
            var missingKeys = 0;
            foreach (var pair in _perkMaxStatBehaviours)
            {
                var perk = pair.Key;
                var behaviour = pair.Value;
                if (perk == null || behaviour == null || perk.Master == null || !perk.EnabledInCurrentLevel)
                    continue;

                behaviours++;
                statPerks.Add(perk.GetInstanceID());
                if (!(PerkStatsEntriesField.GetValue(behaviour) is IEnumerable list))
                    continue;

                foreach (var raw in list)
                {
                    if (!(raw is ModifyCharacterStatsBase.Entry entry) || string.IsNullOrEmpty(entry.key))
                        continue;
                    if (stats.GetStat(entry.key) == null)
                    {
                        missingKeys++; // native OnUnlocked breaks on the first missing stat
                        break;
                    }

                    deltas.TryGetValue(entry.key, out var sum);
                    deltas[entry.key] = sum + entry.value;
                    entries++;
                }
            }

            if (behaviours == 0)
            {
                // No stat tree enabled (or LevelConfig not readable yet). Do not mark the item:
                // keep retrying, the next level-init/level change will re-evaluate.
                if (Time.unscaledTime >= _nextPerkMaxNoStatLog)
                {
                    _nextPerkMaxNoStatLog = Time.unscaledTime + 5f;
                    Log($"[perkmax] no enabled stat perks yet item={item.GetInstanceID()} " +
                        $"hp={health.BaseValue:F0}; retrying");
                }

                return;
            }

            var hpBefore = health.BaseValue;
            var gunBefore = gunDamage.BaseValue;
            foreach (var pair in deltas)
            {
                var stat = stats.GetStat(pair.Key);
                if (stat != null)
                    stat.BaseValue += pair.Value;
            }

            var hpAfter = health.BaseValue;
            var gunAfter = gunDamage.BaseValue;

            // If the expected MaxHealth delta did not stick the stat collection was rebuilt;
            // retry on the next tick instead of marking this item as done.
            if (deltas.TryGetValue("MaxHealth", out var hpDelta) && Mathf.Abs(hpDelta) > 0.001f &&
                Mathf.Abs(hpAfter - hpBefore) < 0.001f)
            {
                Log($"[perkmax] stats not applied yet item={item.GetInstanceID()} hp={hpBefore:F0}; retrying");
                return;
            }

            // Native perks are applied during OnLevelInitialized, i.e. before LevelManager
            // heals the character. We run one frame later, so lift the current HP when the
            // character was still at the old full-health value (never heal a damaged player).
            var currentHp = -1f;
            try
            {
                var healthComponent = main.Health;
                if (healthComponent != null)
                {
                    currentHp = healthComponent.CurrentHealth;
                    if (currentHp > 0f && Mathf.Abs(currentHp - hpBefore) < 0.5f && hpAfter > hpBefore + 0.5f)
                    {
                        healthComponent.SetHealth(hpAfter);
                        currentHp = healthComponent.CurrentHealth;
                    }
                }
            }
            catch
            {
                // health is optional for the stat injection
            }

            _perkMaxStatItem = item;
            PerkMaxItemId = item.GetInstanceID();
            Log($"[perkmax] applied perks={_perkMaxPerks.Count} item={PerkMaxItemId} " +
                $"hp={hpBefore:F0}->{hpAfter:F0} cur={currentHp:F0} gun={gunBefore:F2}->{gunAfter:F2} " +
                $"statPerks={statPerks.Count} behaviours={behaviours} entries={entries} missingKeys={missingKeys}");
        }

        private void RestorePerkMax()
        {
            if (!_perkMaxApplied)
                return;

            // Restore through the setter so non-stat behaviours (storage/facilities) get their
            // native OnLocked callback. ModifyCharacterStatsBase.records is empty for us, so
            // this never subtracts the stats injected above.
            var setter = typeof(Perk)
                .GetProperty("Unlocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetSetMethod(true);
            if (setter != null)
            {
                for (var i = 0; i < _perkMaxPerks.Count; i++)
                {
                    var perk = _perkMaxPerks[i];
                    if (perk != null && i < _perkMaxOriginal.Count)
                        setter.Invoke(perk, new object[] { _perkMaxOriginal[i] });
                }
            }

            Log($"[perkmax] restored perks={_perkMaxPerks.Count}");
            _perkMaxPerks.Clear();
            _perkMaxOriginal.Clear();
            _perkMaxStatBehaviours.Clear();
            _perkMaxManager = null;
            _perkMaxStatItem = null;
            _perkMaxApplied = false;
            PerkMaxActive = false;
        }
    }

    [HarmonyPatch(typeof(PerkBehaviour), "OnLevelInitialized")]
    internal static class PerkMaxLevelInitPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(PerkBehaviour __instance)
        {
            // Stat perks are owned by CoopRuntime.ApplyMaxPerkStats. Skipping the native
            // handler unconditionally (not only once PerkMaxActive flips) also covers the
            // first level after boot: if the native handler ran with the saved partial perk
            // state, the later full injection would stack on top of it.
            return !(__instance is ModifyCharacterStatsBase);
        }
    }

    [HarmonyPatch(typeof(PerkTree), "Save")]
    internal static class PerkMaxSavePatch
    {
        [HarmonyPrefix]
        private static bool Prefix() => !CoopRuntime.PerkMaxActive;
    }
}
