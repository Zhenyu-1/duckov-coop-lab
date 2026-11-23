#pragma once

// Server-side stat reconstruction from the PLAY attribute tables.
//
// v5 trust boundary: the server owns final stats reconstructed from base stats + equipped
// item modifiers. CPlayerStats remains a diagnostic snapshot. This module deliberately does
// not know about wire or entities; World supplies equipped item ids.

#include <array>
#include <cstdint>
#include <map>
#include <string>
#include <vector>

#include "combat/DamageFormula.h"

namespace coop::combat {

enum class StatModifierType : std::uint8_t {
    Add = 0,
    PercentageAdd = 1,
    PercentageMultiply = 2,
};

struct StatModifier {
    std::string key;
    StatModifierType type = StatModifierType::Add;
    double value = 0.0;
};

class StatModel {
public:
    /// Loads base_stats.json, character_base_stats.json, item_modifiers.xml and
    /// base_stats.hash from a config directory. Returns false on malformed required data.
    bool Load(const std::string& configDir, std::string& error);

    bool Loaded() const { return _loaded; }
    const std::array<std::uint8_t, 32>& BaseStatsHash() const { return _baseHash; }
    const std::map<std::string, double>& CharacterBase() const { return _characterBase; }

    /// Reconstructs the D5 combat stats from equipped item ids and optional extra
    /// modifiers (buffs later use the same pipeline).
    CombatStats ToCombatStats(const std::vector<std::uint32_t>& equippedItemIds,
                              const std::vector<StatModifier>& extra = {}) const;

    /// Raw final stat lookup; missing keys start at 0.
    std::map<std::string, double> Recalculate(const std::vector<std::uint32_t>& equippedItemIds,
                                              const std::vector<StatModifier>& extra = {}) const;

    double FinalStat(const std::map<std::string, double>& stats, const std::string& key) const;

private:
    struct ItemModifier {
        std::string target;
        std::string key;
        StatModifierType type = StatModifierType::Add;
        double value = 0.0;
        bool enableInInventory = false;
    };

    bool LoadBaseStats(const std::string& path, std::string& error);
    bool LoadCharacterBase(const std::string& path, std::string& error);
    bool LoadItemModifiers(const std::string& path, std::string& error);
    bool LoadBaseHash(const std::string& path, std::string& error);

    bool _loaded = false;
    std::map<std::string, double> _characterBase;
    std::map<std::string, std::pair<double, double>> _perkBase; // add, percentageAdd
    std::map<std::uint32_t, std::vector<ItemModifier>> _items;
    std::array<std::uint8_t, 32> _baseHash{};
};

} // namespace coop::combat
