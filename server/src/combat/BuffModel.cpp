#include "combat/BuffModel.h"

#include <algorithm>
#include <filesystem>

#include "content/XmlDoc.h"
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

bool ParseBool(const std::string& value)
{
    return value == "true" || value == "True" || value == "1";
}

} // namespace

bool CompareElementFactor(double value, int compareType, double compareTo)
{
    // Native ElementFactorFilterTypes: GreaterThan=0, LessThan=1.
    // 2/3 are CORE extensions, not native enum values.
    if (compareType == 0)
        return value > compareTo;
    if (compareType == 1)
        return value < compareTo;
    if (compareType == 2)
        return value <= compareTo;
    if (compareType == 3)
        return value >= compareTo;
    return false;
}

int AdvanceTickAccumulator(double& accumulator, double deltaSeconds, double periodSeconds)
{
    if (periodSeconds <= 0.0)
        return 0;
    accumulator += deltaSeconds / periodSeconds;
    int count = 0;
    while (accumulator > 1.0) {
        accumulator -= 1.0;
        ++count;
    }
    return count;
}

ExclusiveDecision DecideExclusiveBuff(int oldPriority, bool oldLimited, double oldRemainingSeconds,
                                      int newPriority, bool newLimited, double newTotalSeconds)
{
    if (oldPriority > newPriority)
        return ExclusiveDecision::RejectNew;
    if (oldPriority == newPriority && oldLimited && newLimited &&
        oldRemainingSeconds > newTotalSeconds)
        return ExclusiveDecision::KeepOld;
    return ExclusiveDecision::AddNew;
}

bool BuffModel::Load(const std::string& configDir, std::string& error)
{
    const fs::path dir(configDir);
    if (!LoadBuffs((dir / "buffs.xml").string(), error))
        return false;
    if (!LoadUsage((dir / "item_usage.xml").string(), error))
        return false;
    _loaded = true;
    Log::Info("buff model loaded: buffs=" + std::to_string(_buffs.size()) +
              " usableItems=" + std::to_string(_items.size()));
    return true;
}

bool BuffModel::LoadBuffs(const std::string& path, std::string& error)
{
    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "buffs", error);
    if (root == nullptr)
        return false;

    _buffs.clear();
    for (const tinyxml2::XMLElement* buffElement = root->FirstChildElement(); buffElement != nullptr;
         buffElement = buffElement->NextSiblingElement()) {
        if (std::string(buffElement->Name()) != "buff") {
            error = path + ": unexpected element <" + buffElement->Name() + ">";
            return false;
        }
        BuffDef def;
        if (!xml::RequiredU32(buffElement, "id", def.id, error, path))
            return false;
        def.name = xml::OptionalString(buffElement, "name", "");
        std::uint32_t maxLayers = 1;
        if (!xml::OptionalU32(buffElement, "maxLayers", 1, maxLayers, error, path))
            return false;
        def.maxLayers = static_cast<std::uint8_t>(std::min<std::uint32_t>(255, std::max<std::uint32_t>(1, maxLayers)));
        def.limitedLifeTime =
            ParseBool(xml::OptionalString(buffElement, "limitedLifeTime", "false"));
        if (!xml::OptionalDouble(buffElement, "totalLifeTime", 0.0, def.totalLifeTime, error, path))
            return false;
        def.exclusiveTag = xml::OptionalString(buffElement, "exclusiveTag", "");
        std::int32_t priority = 0;
        if (!xml::OptionalI32(buffElement, "exclusiveTagPriority", 0, priority, error, path))
            return false;
        def.exclusiveTagPriority = priority;

        for (const tinyxml2::XMLElement* effectElement = buffElement->FirstChildElement();
             effectElement != nullptr; effectElement = effectElement->NextSiblingElement()) {
            if (std::string(effectElement->Name()) != "effect")
                continue;

            std::vector<BuffEffect> effectTemplates;
            std::vector<const tinyxml2::XMLElement*> triggers;
            std::vector<BuffFilter> filters;
            for (const tinyxml2::XMLElement* child = effectElement->FirstChildElement();
                 child != nullptr; child = child->NextSiblingElement()) {
                if (std::string(child->Name()) == "trigger")
                    triggers.push_back(child);
                else if (std::string(child->Name()) == "filter") {
                    BuffFilter filter;
                    const std::string type = xml::OptionalString(child, "type", "");
                    if (type == "NotHurtForSeconds") {
                        filter.kind = BuffFilter::Kind::NotHurtForSeconds;
                        if (!xml::OptionalDouble(child, "time", 0.0, filter.timeSeconds, error, path))
                            return false;
                    } else if (type == "ElementFactorFilter") {
                        filter.kind = BuffFilter::Kind::ElementFactor;
                        filter.element = xml::OptionalString(child, "element", "physics");
                        if (!xml::OptionalDouble(child, "compareTo", 1.0, filter.compareTo, error, path))
                            return false;
                        std::int32_t compareType = 0;
                        if (!xml::OptionalI32(child, "compareType", 0, compareType, error, path))
                            return false;
                        filter.compareType = compareType;
                    } else if (type == "CheckBuffFilter") {
                        filter.kind = BuffFilter::Kind::CheckBuff;
                        if (!xml::OptionalU32(child, "checkBuffId", 0, filter.checkBuffId, error, path))
                            return false;
                    } else if (type == "BuffLayersGreaterOrEqual") {
                        filter.kind = BuffFilter::Kind::BuffLayers;
                        if (!xml::OptionalU32(child, "buffId", 0, filter.buffId, error, path))
                            return false;
                        std::uint32_t layers = 1;
                        if (!xml::OptionalU32(child, "layers", 1, layers, error, path))
                            return false;
                        filter.layers = static_cast<std::uint8_t>(std::min<std::uint32_t>(255, layers));
                    } else {
                        filter.kind = BuffFilter::Kind::CharacterRunning;
                    }
                    filters.push_back(filter);
                }
            }
            for (const tinyxml2::XMLElement* trigger : triggers) {
                BuffEffect effect;
                const std::string type = xml::OptionalString(trigger, "type", "");
                if (type == "TickTrigger") {
                    effect.trigger = BuffTriggerKind::Tick;
                    if (!xml::OptionalDouble(trigger, "period", 0.0, effect.periodSeconds, error,
                                             path))
                        return false;
                    effect.allowMultipleTrigger =
                        ParseBool(xml::OptionalString(trigger, "allowMultipleTrigger", "true"));
                } else if (type == "OnShootAttackTrigger") {
                    effect.trigger = BuffTriggerKind::OnShoot;
                    effect.onShoot = ParseBool(xml::OptionalString(trigger, "onShoot", "false"));
                    effect.onAttack = ParseBool(xml::OptionalString(trigger, "onAttack", "false"));
                } else if (type == "OnTakeDamageTrigger") {
                    effect.trigger = BuffTriggerKind::OnTakeDamage;
                    if (!xml::OptionalDouble(trigger, "threshold", 0.0, effect.damageThreshold, error,
                                             path))
                        return false;
                } else if (type == "OnBuffLayerChanged") {
                    effect.trigger = BuffTriggerKind::OnLayerChanged;
                    if (!xml::OptionalU32(trigger, "buffId", 0, effect.triggerBuffId, error, path))
                        return false;
                    effect.needMaxLayers =
                        ParseBool(xml::OptionalString(trigger, "needMaxLayers", "false"));
                } else {
                    effect.trigger = BuffTriggerKind::OnSetItem;
                }
                effect.filters = filters;
                effectTemplates.push_back(effect);
            }
            if (effectTemplates.empty()) {
                BuffEffect effect; // triggerless effect: on set
                effect.filters = filters;
                effectTemplates.push_back(effect);
            }

            for (const tinyxml2::XMLElement* actionElement = effectElement->FirstChildElement();
                 actionElement != nullptr; actionElement = actionElement->NextSiblingElement()) {
                if (std::string(actionElement->Name()) != "action")
                    continue;
                BuffAction action;
                const std::string type = xml::OptionalString(actionElement, "type", "");
                if (type == "ModifierAction") {
                    action.kind = BuffAction::Kind::Modifier;
                    action.key = xml::OptionalString(actionElement, "key", "");
                    action.modifierType =
                        ParseModifierType(xml::OptionalString(actionElement, "modifierType", "Add"));
                    if (!xml::OptionalDouble(actionElement, "value", 0.0, action.modifierValue,
                                             error, path))
                        return false;
                } else if (type == "DamageAction") {
                    action.kind = BuffAction::Kind::Damage;
                    if (!xml::OptionalDouble(actionElement, "damageValue", 0.0, action.damageValue,
                                             error, path))
                        return false;
                    action.percentDamage =
                        ParseBool(xml::OptionalString(actionElement, "percentDamage", "false"));
                    if (!xml::OptionalDouble(actionElement, "percentDamageValue", 0.0,
                                             action.percentDamageValue, error, path))
                        return false;
                    action.realDamage =
                        xml::OptionalString(actionElement, "damageType", "normal") == "realDamage";
                    action.ignoreArmor =
                        ParseBool(xml::OptionalString(actionElement, "ignoreArmor", "false"));
                    action.element = xml::OptionalString(actionElement, "element", "physics");
                } else if (type == "AddBuffAction") {
                    action.kind = BuffAction::Kind::AddBuff;
                    if (!xml::RequiredU32(actionElement, "buffId", action.buffId, error, path))
                        return false;
                } else if (type == "RemoveBuffAction") {
                    action.kind = BuffAction::Kind::RemoveBuff;
                    if (!xml::RequiredU32(actionElement, "buffId", action.buffId, error, path))
                        return false;
                    action.removeOneLayer =
                        ParseBool(xml::OptionalString(actionElement, "removeOneLayer", "false"));
                } else {
                    continue; // Heal/Fx/etc. are client presentation for D5.1
                }
                for (BuffEffect& effect : effectTemplates)
                    effect.actions.push_back(action);
            }
            for (BuffEffect& effect : effectTemplates) {
                if (!effect.actions.empty())
                    def.effects.push_back(std::move(effect));
            }
        }
        _buffs[def.id] = std::move(def);
    }
    return true;
}

bool BuffModel::LoadUsage(const std::string& path, std::string& error)
{
    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "itemUsage", error);
    if (root == nullptr)
        return false;

    _items.clear();
    for (const tinyxml2::XMLElement* itemElement = root->FirstChildElement(); itemElement != nullptr;
         itemElement = itemElement->NextSiblingElement()) {
        if (std::string(itemElement->Name()) != "item") {
            error = path + ": unexpected element <" + itemElement->Name() + ">";
            return false;
        }
        ItemUsage usage;
        if (!xml::RequiredU32(itemElement, "id", usage.itemId, error, path))
            return false;
        if (!xml::OptionalDouble(itemElement, "useTime", 0.0, usage.useTime, error, path))
            return false;
        for (const tinyxml2::XMLElement* child = itemElement->FirstChildElement(); child != nullptr;
             child = child->NextSiblingElement()) {
            const std::string tag = child->Name();
            if (tag == "addBuff") {
                ItemAddBuff add;
                if (!xml::RequiredU32(child, "buffId", add.buffId, error, path))
                    return false;
                if (!xml::OptionalDouble(child, "chance", 1.0, add.chance, error, path))
                    return false;
                if (add.chance > 0.0 && add.chance <= 1.0) {
                    usage.addBuffs.push_back(add);
                    ItemUsage::Operation operation;
                    operation.kind = ItemUsage::Operation::Kind::Add;
                    operation.add = add;
                    usage.operations.push_back(operation);
                }
            } else if (tag == "removeBuff") {
                ItemRemoveBuff remove;
                if (!xml::RequiredU32(child, "buffId", remove.buffId, error, path))
                    return false;
                remove.limitLayers =
                    ParseBool(xml::OptionalString(child, "limitLayers", "true"));
                std::uint32_t layers = 1;
                if (!xml::OptionalU32(child, "layers", 1, layers, error, path))
                    return false;
                remove.layers = static_cast<std::uint8_t>(std::min<std::uint32_t>(255, layers));
                usage.removeBuffs.push_back(remove);
                ItemUsage::Operation operation;
                operation.kind = ItemUsage::Operation::Kind::Remove;
                operation.remove = remove;
                usage.operations.push_back(operation);
            } else if (tag == "drug") {
                // Contract 2.16.0: native heal amount. Optional so old exports still load.
                if (!xml::OptionalDouble(child, "heal", 0.0, usage.drugHeal, error, path))
                    return false;
            }
        }
        _items[usage.itemId] = std::move(usage);
    }
    return true;
}

const BuffDef* BuffModel::Find(std::uint32_t buffId) const
{
    const auto it = _buffs.find(buffId);
    return it == _buffs.end() ? nullptr : &it->second;
}

const ItemUsage* BuffModel::FindItem(std::uint32_t itemId) const
{
    const auto it = _items.find(itemId);
    return it == _items.end() ? nullptr : &it->second;
}

std::vector<StatModifier> BuffModel::Modifiers(std::uint32_t buffId, std::uint8_t layers) const
{
    std::vector<StatModifier> out;
    const BuffDef* def = Find(buffId);
    if (def == nullptr)
        return out;
    for (const BuffEffect& effect : def->effects) {
        for (const BuffAction& action : effect.actions) {
            if (action.kind != BuffAction::Kind::Modifier || action.key.empty())
                continue;
            out.push_back(StatModifier{action.key, action.modifierType,
                                       action.modifierValue * static_cast<double>(layers)});
        }
    }
    return out;
}

} // namespace coop::combat
