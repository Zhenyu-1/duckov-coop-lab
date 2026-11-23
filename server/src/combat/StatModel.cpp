#include "combat/StatModel.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <sstream>

#include "content/XmlDoc.h"
#include "core/Json.h"
#include "core/Log.h"

namespace coop::combat {

namespace {

namespace fs = std::filesystem;

StatModifierType ParseModifierType(const std::string& value)
{
    if (value == "PercentageAdd")
        return StatModifierType::PercentageAdd;
    if (value == "PercentageMultiply")
        return StatModifierType::PercentageMultiply;
    return StatModifierType::Add;
}

} // namespace

bool StatModel::Load(const std::string& configDir, std::string& error)
{
    const fs::path dir(configDir);
    if (!LoadCharacterBase((dir / "character_base_stats.json").string(), error))
        return false;
    if (!LoadBaseStats((dir / "base_stats.json").string(), error))
        return false;
    if (!LoadItemModifiers((dir / "item_modifiers.xml").string(), error))
        return false;
    if (!LoadBaseHash((dir / "base_stats.hash").string(), error))
        return false;
    _loaded = true;
    Log::Info("stat model loaded: characterKeys=" + std::to_string(_characterBase.size()) +
              " perkKeys=" + std::to_string(_perkBase.size()) +
              " itemModifierItems=" + std::to_string(_items.size()));
    return true;
}

bool StatModel::LoadBaseStats(const std::string& path, std::string& error)
{
    Json doc;
    if (!LoadJsonFile(path, doc, error))
        return false;
    const Json* stats = doc.Find("stats");
    if (stats == nullptr || !stats->IsObject()) {
        error = path + ": missing stats object";
        return false;
    }
    _perkBase.clear();
    for (const auto& [key, value] : stats->fields) {
        if (!value.IsObject()) {
            error = path + ": stat '" + key + "' is not an object";
            return false;
        }
        const Json* add = value.Find("add");
        const Json* percentage = value.Find("percentageAdd");
        _perkBase[key] = {add != nullptr ? add->AsNumber(0.0) : 0.0,
                          percentage != nullptr ? percentage->AsNumber(0.0) : 0.0};
    }
    return true;
}

bool StatModel::LoadCharacterBase(const std::string& path, std::string& error)
{
    Json doc;
    if (!LoadJsonFile(path, doc, error))
        return false;
    const Json* stats = doc.Find("stats");
    if (stats == nullptr || !stats->IsObject()) {
        error = path + ": missing stats object";
        return false;
    }
    _characterBase.clear();
    for (const auto& [key, value] : stats->fields) {
        if (!value.IsNumber()) {
            error = path + ": stat '" + key + "' is not a number";
            return false;
        }
        _characterBase[key] = value.number;
    }
    return true;
}

bool StatModel::LoadItemModifiers(const std::string& path, std::string& error)
{
    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root =
        xml::TableRoot(doc, path, "itemModifiers", error);
    if (root == nullptr)
        return false;

    _items.clear();
    for (const tinyxml2::XMLElement* item = root->FirstChildElement(); item != nullptr;
         item = item->NextSiblingElement()) {
        if (std::string(item->Name()) != "item") {
            error = path + ": unexpected element <" + item->Name() + ">";
            return false;
        }
        std::uint32_t itemId = 0;
        if (!xml::RequiredU32(item, "id", itemId, error, path))
            return false;
        std::vector<ItemModifier> modifiers;
        for (const tinyxml2::XMLElement* modifier = item->FirstChildElement();
             modifier != nullptr; modifier = modifier->NextSiblingElement()) {
            if (std::string(modifier->Name()) != "modifier") {
                error = path + ": unexpected element <" + modifier->Name() + ">";
                return false;
            }
            ItemModifier parsed;
            parsed.target = xml::OptionalString(modifier, "target", "Character");
            parsed.key = xml::OptionalString(modifier, "key", "");
            parsed.type = ParseModifierType(xml::OptionalString(modifier, "type", "Add"));
            if (!xml::OptionalDouble(modifier, "value", 0.0, parsed.value, error, path))
                return false;
            parsed.enableInInventory =
                xml::OptionalString(modifier, "enableInInventory", "false") == "true";
            if (!parsed.key.empty())
                modifiers.push_back(std::move(parsed));
        }
        _items[itemId] = std::move(modifiers);
    }
    return true;
}

bool StatModel::LoadBaseHash(const std::string& path, std::string& error)
{
    std::ifstream input(path);
    if (!input) {
        error = path + ": cannot open base stats hash";
        return false;
    }
    std::string hex;
    input >> hex;
    if (hex.size() != 64) {
        error = path + ": expected 64 hex characters";
        return false;
    }
    for (std::size_t i = 0; i < _baseHash.size(); ++i) {
        const unsigned value =
            static_cast<unsigned>(std::stoul(hex.substr(i * 2, 2), nullptr, 16));
        _baseHash[i] = static_cast<std::uint8_t>(value);
    }
    return true;
}

std::map<std::string, double> StatModel::Recalculate(
    const std::vector<std::uint32_t>& equippedItemIds,
    const std::vector<StatModifier>& extra) const
{
    std::map<std::string, double> stats;
    for (const auto& [key, base] : _characterBase)
        stats[key] = base;
    for (const auto& [key, perk] : _perkBase) {
        auto& value = stats[key];
        value += perk.first + perk.second;
    }

    std::vector<StatModifier> modifiers = extra;
    for (const std::uint32_t itemId : equippedItemIds) {
        const auto item = _items.find(itemId);
        if (item == _items.end())
            continue;
        for (const ItemModifier& modifier : item->second) {
            // All loadout entries are directly equipped; both Character and Parent
            // resolve to the character item in that case (see M2-stat-recalc-draft §3).
            if (modifier.target != "Character" && modifier.target != "Parent")
                continue;
            modifiers.push_back(StatModifier{modifier.key, modifier.type, modifier.value});
        }
    }

    std::map<std::string, double> adds;
    std::map<std::string, double> percentageAdds;
    for (const StatModifier& modifier : modifiers) {
        if (modifier.type == StatModifierType::Add)
            adds[modifier.key] += modifier.value;
        else if (modifier.type == StatModifierType::PercentageAdd)
            percentageAdds[modifier.key] += modifier.value;
    }
    for (const auto& [key, value] : adds)
        stats[key] += value;
    for (const auto& [key, value] : percentageAdds)
        stats[key] *= std::max(0.0, 1.0 + value);

    // PercentageMultiply is applied one by one in data order.
    for (const StatModifier& modifier : modifiers) {
        if (modifier.type == StatModifierType::PercentageMultiply)
            stats[modifier.key] *= std::max(0.0, 1.0 + modifier.value);
    }
    return stats;
}

double StatModel::FinalStat(const std::map<std::string, double>& stats,
                            const std::string& key) const
{
    const auto it = stats.find(key);
    return it == stats.end() ? 0.0 : it->second;
}

CombatStats StatModel::ToCombatStats(const std::vector<std::uint32_t>& equippedItemIds,
                                     const std::vector<StatModifier>& extra) const
{
    const auto stats = Recalculate(equippedItemIds, extra);
    auto get = [&](const char* key, double fallback) {
        const auto it = stats.find(key);
        return it == stats.end() ? fallback : it->second;
    };

    CombatStats out;
    out.gunDamageMultiplier = get("GunDamageMultiplier", 1.0);
    out.gunCritRateGain = get("GunCritRateGain", 0.0);
    out.gunCritDamageGain = get("GunCritDamageGain", 0.0);
    out.meleeDamageMultiplier = get("MeleeDamageMultiplier", 1.0);
    out.meleeCritRateGain = get("MeleeCritRateGain", 0.0);
    out.meleeCritDamageGain = get("MeleeCritDamageGain", 0.0);
    out.bodyArmor = get("BodyArmor", 0.0);
    out.headArmor = get("HeadArmor", 0.0);
    out.maxHealth = get("MaxHealth", 100.0);
    const char* keys[7] = {"ElementFactor_Physics", "ElementFactor_Fire", "ElementFactor_Ice",
                           "ElementFactor_Poison", "ElementFactor_Electricity",
                           "ElementFactor_Space", "ElementFactor_Ghost"};
    for (std::size_t i = 0; i < out.elementFactors.size(); ++i)
        out.elementFactors[i] = get(keys[i], 1.0);
    out.isMainCharacter = true;
    out.hasPreset = false;
    return out;
}

} // namespace coop::combat
