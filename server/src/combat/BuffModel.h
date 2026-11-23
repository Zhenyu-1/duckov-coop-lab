#pragma once

// D5.1 buff/item-use tables. Server-authoritative state lives in World; this module only
// parses the PLAY-generated XML and exposes pure data.

#include <cstdint>
#include <map>
#include <string>
#include <vector>

#include "combat/StatModel.h"

namespace coop::combat {

enum class BuffTriggerKind : std::uint8_t {
    OnSetItem,
    Tick,
    OnShoot,
    OnTakeDamage,
    OnLayerChanged,
};

struct BuffAction {
    enum class Kind : std::uint8_t { Modifier, Damage, AddBuff, RemoveBuff };
    Kind kind = Kind::Modifier;

    // ModifierAction
    std::string key;
    StatModifierType modifierType = StatModifierType::Add;
    double modifierValue = 0.0;

    // DamageAction
    double damageValue = 0.0;
    bool percentDamage = false;
    double percentDamageValue = 0.0;
    bool realDamage = false;
    bool ignoreArmor = false;
    std::string element = "physics";

    // AddBuffAction / RemoveBuffAction
    std::uint32_t buffId = 0;
    bool removeOneLayer = false;
};

struct BuffFilter {
    enum class Kind : std::uint8_t {
        CharacterRunning,
        NotHurtForSeconds,
        ElementFactor,
        CheckBuff,
        BuffLayers,
    };
    Kind kind = Kind::CharacterRunning;
    double timeSeconds = 0.0;
    std::string element = "physics";
    double compareTo = 1.0;
    int compareType = 0;
    std::uint32_t buffId = 0;
    std::uint32_t checkBuffId = 0;
    std::uint8_t layers = 1;
};

struct BuffEffect {
    BuffTriggerKind trigger = BuffTriggerKind::OnSetItem;
    double periodSeconds = 0.0;
    std::uint32_t triggerBuffId = 0;
    bool needMaxLayers = false;
    double damageThreshold = 0.0;
    bool onShoot = false;
    bool onAttack = false;
    bool allowMultipleTrigger = true;
    std::vector<BuffFilter> filters;
    std::vector<BuffAction> actions;
};

struct BuffDef {
    std::uint32_t id = 0;
    std::string name;
    std::uint8_t maxLayers = 1;
    bool limitedLifeTime = false;
    double totalLifeTime = 0.0;
    std::string exclusiveTag;
    int exclusiveTagPriority = 0;
    std::vector<BuffEffect> effects;
};

struct ItemAddBuff {
    std::uint32_t buffId = 0;
    double chance = 1.0;
};

struct ItemRemoveBuff {
    std::uint32_t buffId = 0;
    bool limitLayers = true;
    std::uint8_t layers = 1;
};

struct ItemUsage {
    struct Operation {
        enum class Kind : std::uint8_t { Add, Remove };
        Kind kind = Kind::Add;
        ItemAddBuff add;
        ItemRemoveBuff remove;
    };

    std::uint32_t itemId = 0;
    double useTime = 0.0;
    /// Contract 2.16.0: native `<drug heal="...">` (negative = damage). The server owns
    /// healing, so this value wins over whatever CItemConsume reported for the same item.
    double drugHeal = 0.0;
    std::vector<ItemAddBuff> addBuffs;
    std::vector<ItemRemoveBuff> removeBuffs;
    std::vector<Operation> operations; // XML order, applied by the server
};

bool CompareElementFactor(double value, int compareType, double compareTo);

/// Native TickTrigger phase: accumulate deltaTime/period, consume whole units and keep the
/// remainder so periods that are not multiples of the server tick do not drift.
int AdvanceTickAccumulator(double& accumulator, double deltaSeconds, double periodSeconds);

enum class ExclusiveDecision : std::uint8_t {
    AddNew,
    RejectNew,
    KeepOld,
};

/// Native exclusive-tag replacement decision (same tag, different buff id).
ExclusiveDecision DecideExclusiveBuff(int oldPriority, bool oldLimited, double oldRemainingSeconds,
                                      int newPriority, bool newLimited, double newTotalSeconds);

class BuffModel {
public:
    bool Load(const std::string& configDir, std::string& error);
    bool Loaded() const { return _loaded; }

    const BuffDef* Find(std::uint32_t buffId) const;
    const ItemUsage* FindItem(std::uint32_t itemId) const;
    std::size_t BuffCount() const { return _buffs.size(); }
    std::size_t ItemCount() const { return _items.size(); }

    /// Stat modifiers contributed by one active buff at `layers`.
    std::vector<StatModifier> Modifiers(std::uint32_t buffId, std::uint8_t layers) const;

private:
    bool LoadBuffs(const std::string& path, std::string& error);
    bool LoadUsage(const std::string& path, std::string& error);

    bool _loaded = false;
    std::map<std::uint32_t, BuffDef> _buffs;
    std::map<std::uint32_t, ItemUsage> _items;
};

} // namespace coop::combat
