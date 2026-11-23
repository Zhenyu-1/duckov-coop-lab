#include "combat/DamageFormula.h"

#include <algorithm>
#include <cctype>
#include <cmath>

namespace coop::combat {

namespace {

double Clamp(double value, double low, double high)
{
    return std::max(low, std::min(high, value));
}

} // namespace

DamageElement ElementFromString(const std::string& name)
{
    std::string lower;
    lower.reserve(name.size());
    for (char c : name)
        lower.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(c))));
    if (lower == "fire")
        return DamageElement::Fire;
    if (lower == "ice")
        return DamageElement::Ice;
    if (lower == "poison")
        return DamageElement::Poison;
    if (lower == "electricity")
        return DamageElement::Electricity;
    if (lower == "space")
        return DamageElement::Space;
    if (lower == "ghost")
        return DamageElement::Ghost;
    return DamageElement::Physics;
}

DamageResult ComputeDamage(const DamageInput& input)
{
    DamageResult result;

    if (input.isMelee) {
        if (input.melee == nullptr)
            return result;
        result.baseDamage = input.melee->damage * input.attacker.meleeDamageMultiplier;
        result.critDamageFactor =
            input.melee->critDamageFactor * (1.0 + input.attacker.meleeCritDamageGain);
        result.critRate =
            input.melee->critRate * (1.0 + input.attacker.meleeCritRateGain);
        result.element = ElementFromString(input.melee->element);
    } else {
        if (input.weapon == nullptr)
            return result;
        const double ammoMultiplier = input.ammo != nullptr ? input.ammo->damageMultiplier : 1.0;
        const double shotCount = static_cast<double>(std::max<std::uint16_t>(1, input.weapon->shotCount));
        result.baseDamage = input.weapon->damage * ammoMultiplier *
                            input.attacker.gunDamageMultiplier / shotCount;
        result.element = ElementFromString(input.weapon->element);
        if (input.weapon->damage > 1.0 && result.baseDamage < 1.0)
            result.baseDamage = 1.0;

        double distance = (input.weapon->bulletDistance + 0.4) * 100.0;
        if (!input.attacker.isMainCharacter)
            distance *= 1.05;
        if (input.hitDistanceCm > distance * 0.5) {
            const double falloff = input.weapon->dmgOverDistance <= 0.0
                                       ? 0.5
                                       : input.weapon->dmgOverDistance;
            result.baseDamage *= falloff;
        }

        const double ammoCritDamage =
            input.ammo != nullptr ? input.ammo->critDamageFactorGain : 0.0;
        result.critDamageFactor =
            (input.weapon->critDamageFactor + ammoCritDamage) *
            (1.0 + input.attacker.gunCritDamageGain);

        const double ammoCritRate = input.ammo != nullptr ? input.ammo->critRateGain : 0.0;
        result.critRate = input.weapon->critRate *
                          (1.0 + input.attacker.gunCritRateGain + ammoCritRate);
        if (input.attacker.isMainCharacter)
            result.critRate = input.isHeadshot ? 1.0 : 0.0;
    }

    result.headshot = input.isHeadshot;
    if (input.isExplosion)
        result.critRate = 0.0;
    if (input.critOverride.has_value())
        result.crit = *input.critOverride;
    else
        result.crit = result.critRate >= 1.0;
    if (input.isExplosion)
        result.crit = false;

    double damage = result.baseDamage * (result.crit ? result.critDamageFactor : 1.0);
    if (input.targetIsPlayer)
        damage *= input.damageFactorToPlayer;
    if (input.targetIsZombie)
        damage *= input.damageFactorToZombie;

    if (!input.isRealDamage && !input.ignoreArmor) {
        const double armor = result.crit ? input.victim.headArmor : input.victim.bodyArmor;
        if (armor > 0.0) {
            double armorPiercing = 0.0;
            if (input.isMelee && input.melee != nullptr)
                armorPiercing = input.melee->armorPiercing;
            else if (input.weapon != nullptr)
                armorPiercing = input.weapon->armorPiercing;
            if (input.ammo != nullptr)
                armorPiercing += input.ammo->armorPiercingGain;

            result.armorFactor = 2.0 /
                (Clamp(armor - armorPiercing, 0.0, 999.0) + 2.0);
            if (!input.attacker.isMainCharacter && !input.victim.isMainCharacter &&
                input.attacker.hasPreset && input.victim.hasPreset &&
                input.victim.aiCombatFactor > 0.0) {
                result.armorFactor *=
                    input.attacker.aiCombatFactor / input.victim.aiCombatFactor;
            }
            damage *= result.armorFactor;
        }
    }

    const std::size_t elementIndex = static_cast<std::size_t>(result.element);
    result.elementFactor = elementIndex < input.victim.elementFactors.size()
                               ? input.victim.elementFactors[elementIndex]
                               : 1.0;
    damage *= result.elementFactor;

    // Native element clamp: a positive sub-1 term becomes 1 when both the attack and victim
    // factors are positive (the weapon's per-element factor is 1 in the current data).
    if (damage > 0.0 && damage < 1.0 && result.elementFactor > 0.0)
        damage = 1.0;

    // Native overkill: finalDamage cannot exceed current HP + 1. The caller applies the
    // D5 hp clamp separately.
    if (input.targetHp >= 0.0 && damage > input.targetHp)
        damage = input.targetHp + 1.0;

    result.finalDamage = damage;
    return result;
}

DamageResult ComputeExplosionDamage(const DamageInput& input, double reportedDamage)
{
    DamageResult result;
    result.baseDamage = std::max(0.0, reportedDamage);
    result.critDamageFactor = 1.0;
    result.critRate = 0.0;
    result.crit = false;
    result.headshot = false;
    result.element = DamageElement::Physics;
    double damage = result.baseDamage;

    if (!input.isRealDamage && !input.ignoreArmor) {
        const double armor = input.victim.bodyArmor;
        if (armor > 0.0) {
            result.armorFactor = 2.0 / (Clamp(armor, 0.0, 999.0) + 2.0);
            if (!input.attacker.isMainCharacter && !input.victim.isMainCharacter &&
                input.attacker.hasPreset && input.victim.hasPreset &&
                input.victim.aiCombatFactor > 0.0) {
                result.armorFactor *= input.attacker.aiCombatFactor / input.victim.aiCombatFactor;
            }
            damage *= result.armorFactor;
        }
    }

    const std::size_t elementIndex = static_cast<std::size_t>(result.element);
    result.elementFactor = elementIndex < input.victim.elementFactors.size()
                               ? input.victim.elementFactors[elementIndex]
                               : 1.0;
    damage *= result.elementFactor;
    if (damage > 0.0 && damage < 1.0 && result.elementFactor > 0.0)
        damage = 1.0;
    if (input.targetHp >= 0.0 && damage > input.targetHp)
        damage = input.targetHp + 1.0;

    result.finalDamage = damage;
    return result;
}

} // namespace coop::combat
