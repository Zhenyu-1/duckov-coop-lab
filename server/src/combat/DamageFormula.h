#pragma once

// D5 authoritative damage formula (native Health.Hurt semantics).
//
// Pure calculation only: it does not touch HP, death, wire or the ECS. The caller supplies
// already-validated tables/stats and the crit roll. This keeps the formula testable against
// PLAY's native calibration samples before the v4 messages are wired in.

#include <array>
#include <cstdint>
#include <optional>
#include <string>

#include "content/ContentTypes.h"

namespace coop::combat {

enum class DamageElement : std::uint8_t {
    Physics = 0,
    Fire = 1,
    Ice = 2,
    Poison = 3,
    Electricity = 4,
    Space = 5,
    Ghost = 6,
};

DamageElement ElementFromString(const std::string& name);

/// Character stats used by the native formula. Equipment/perks/totems are already folded
/// into these values by the client snapshot; the server never reads item modifiers here.
struct CombatStats {
    double gunDamageMultiplier = 1.0;
    double gunCritRateGain = 0.0;
    double gunCritDamageGain = 0.0;
    double meleeDamageMultiplier = 1.0;
    double meleeCritRateGain = 0.0;
    double meleeCritDamageGain = 0.0;
    double bodyArmor = 0.0;
    double headArmor = 0.0;
    double maxHealth = 100.0;
    std::array<double, 7> elementFactors{1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0};
    double aiCombatFactor = 1.0;
    bool hasPreset = false;
    bool isMainCharacter = true;
};

struct DamageInput {
    bool isMelee = false;
    const WeaponDef* weapon = nullptr;
    const AmmoDef* ammo = nullptr;
    const MeleeDef* melee = nullptr;

    CombatStats attacker;
    CombatStats victim;

    bool isHeadshot = false;
    /// Server rolls Random.value < critRate; tests pass the rolled value explicitly.
    std::optional<bool> critOverride;
    double hitDistanceCm = 0.0;
    double targetHp = 0.0;

    bool targetIsPlayer = false;
    bool targetIsZombie = false;
    bool isRealDamage = false;
    bool ignoreArmor = false;
    bool isExplosion = false;
    double damageFactorToPlayer = 1.0;
    double damageFactorToZombie = 1.0;
};

struct DamageResult {
    double baseDamage = 0.0;
    double critDamageFactor = 1.0;
    double critRate = 0.0;
    double armorFactor = 1.0;
    double elementFactor = 1.0;
    double finalDamage = 0.0;
    bool crit = false;
    bool headshot = false;
    DamageElement element = DamageElement::Physics;
};

/// Native Health.Hurt formula v1. Unknown/missing tables produce 0 damage; callers should
/// reject before calling. The result is not clamped to HP>=1 (D5 applies clamp outside).
DamageResult ComputeDamage(const DamageInput& input);

/// M2-v10 demo: explosion damage reported by the thrower (grenades and breakable barrels
/// have no weapon-table entry). Applies the same armor/element/overkill tail, with the
/// reported base value, no crit and physics element.
DamageResult ComputeExplosionDamage(const DamageInput& input, double reportedDamage);

} // namespace coop::combat
