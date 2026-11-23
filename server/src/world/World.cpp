#include "world/World.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <limits>
#include <numeric>
#include <random>

#include "core/Log.h"
#include "combat/DamageFormula.h"

namespace coop {

namespace {

/// v9 wire error mapping for a failed atomic ledger transfer.
std::uint8_t TransferErrorCode(ItemLedger::TransferError error)
{
    switch (error) {
    case ItemLedger::TransferError::Ok:
        return 0;
    case ItemLedger::TransferError::RevisionMismatch:
    case ItemLedger::TransferError::TargetSlotBusy:
        return static_cast<std::uint8_t>(ErrorCode::Conflict);
    default:
        return static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
    }
}

/// Contract 2.16.0: the native `Item.Slots` key of a gun's magazine attachment. Unplugging it
/// changes the gun's magazine capacity, so the loaded rounds must be returned to the backpack.
bool IsMagazineSlotKey(const std::string& key)
{
    std::string lower;
    lower.reserve(key.size());
    for (const char c : key)
        lower.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(c))));
    return lower == "mag" || lower == "magazine" || lower == "clip";
}

} // namespace

World::World(const ItemRegistry* registry, const LootTable* lootTable, std::uint32_t seed, IStorage* storage)
    : _storage(storage), _registry(registry), _lootTable(lootTable)
{
    if (lootTable != nullptr) {
        _ledger.CreateContainer(kWorldLootContainer, lootTable->capacity > 0 ? lootTable->capacity : 8);

        std::mt19937 rng(seed);
        const auto rolled = RollLoot(*lootTable, rng, registry);

        std::uint16_t slot = 0;
        for (const auto& entry : rolled) {
            if (slot >= (lootTable->capacity > 0 ? lootTable->capacity : 8))
                break;
            const auto uid = _ledger.CreateItem(entry.itemId, entry.count);
            std::uint16_t placed = 0;
            _ledger.TryPut(kWorldLootContainer, uid, slot, placed);
            slot = static_cast<std::uint16_t>(placed + 1);
        }
    } else {
        _ledger.CreateContainer(kWorldLootContainer, 8);

        for (std::uint16_t i = 0; i < 3; ++i) {
            const auto uid = _ledger.CreateItem(100 + i, 1);
            std::uint16_t slot = 0;
            _ledger.TryPut(kWorldLootContainer, uid, i, slot);
        }
    }

    _phase = 0; // loading until the first player joins
}

Entity World::AddPlayer(std::uint16_t playerId, std::uint64_t endpointKey, std::uint64_t identity)
{
    if (identity == 0)
        identity = playerId;

    const Entity e = _ecs.Create();
    _ecs.Add<PlayerComponent>(e, PlayerComponent{playerId, identity, 0, false});
    _ecs.Add<Position>(e);
    _ecs.Add<Health>(e, Health{100, 100, true});
    _ecs.Add<SessionRef>(e, SessionRef{endpointKey});
    _ecs.Add<PlayerProgress>(e, PlayerProgress{false});

    _ledger.CreateContainer(InventoryId(playerId), kInventoryCapacity);
    _ledger.CreateContainer(EquipmentId(playerId), kEquipmentCapacity);
    _ledger.CreateContainer(PetBagId(playerId), kPetBagCapacity);

    if (!_loadoutTickets.empty()) {
        // T3: the ticket is the only source of starting gear (never the out-of-raid stash).
        SeedPlayerInventories(playerId);
    } else if (_storage != nullptr) {
        std::vector<StoredItem> stash;
        if (_storage->LoadStash(identity, stash)) {
            std::uint16_t preferred = 0;
            for (const auto& stored : stash) {
                const auto uid = _ledger.CreateItem(stored.itemId, stored.count);
                _ledger.SetItemDetail(uid, stored.durability, stored.maxDurability,
                                      stored.loadedAmmoItemId, stored.loadedAmmoCount);
                std::uint16_t placed = 0;
                if (!_ledger.TryPut(InventoryId(playerId), uid, preferred, placed))
                    break;
                preferred = static_cast<std::uint16_t>(placed + 1);
            }
        }
    }

    if (_phase == 0) {
        _phase = 1;
        msg::RaidPhaseMessage message;
        message.phase = _phase;
        message.tick = 0;

        wire::Writer writer;
        message.Write(writer);
        Emit(Op::SRaidPhase, 0, writer.Bytes());
    }

    return e;
}

void World::RemovePlayer(Entity entity)
{
    if (const auto* player = _ecs.Get<PlayerComponent>(entity))
        _raidParticipants.erase(player->playerId);

    _ecs.Destroy(entity);
}

bool World::IsAlive(Entity entity) const
{
    const auto* health = _ecs.Get<Health>(entity);
    return health != nullptr && health->alive;
}

std::int32_t World::HealthOf(Entity entity) const
{
    const auto* health = _ecs.Get<Health>(entity);
    return health != nullptr ? health->hp : 0;
}

std::int32_t World::MaxHealthOf(Entity entity) const
{
    const auto* health = _ecs.Get<Health>(entity);
    return health != nullptr ? health->maxHp : 0;
}

void World::SubmitInput(Entity entity, const msg::Input& input)
{
    _pendingInputs.emplace_back(entity, input);
}

void World::SubmitDamage(Entity target, std::int32_t amount)
{
    _pendingDamage.emplace_back(target, amount);
}

void World::SubmitHit(Entity source, std::uint16_t playerId, const msg::HitReport& report)
{
    _pendingHits.push_back(PendingHit{source, playerId, report});
}

void World::SubmitHitEvent(std::uint16_t reporterPlayerId, const msg::HitEvent& hit)
{
    _pendingHitEvents.push_back(PendingHitEvent{reporterPlayerId, hit});
}

void World::SubmitSceneObjectState(std::uint16_t fromPlayerId, const msg::SceneObjectState& state)
{
    if (state.raidEpoch != _raidEpoch || _raidEpoch == 0)
        return;
    if (FindPlayer(fromPlayerId) == kInvalidEntity)
        return;
    if (state.kind > static_cast<std::uint8_t>(msg::SceneObjectKind::Cover))
        return;

    auto& record = _sceneObjects[state.key];
    record.kind = state.kind;
    // Breakable destruction is one-way: a late/stale "normal" report must not resurrect it.
    if (state.kind == static_cast<std::uint8_t>(msg::SceneObjectKind::Breakable) &&
        record.state == 1)
        return;
    if (record.state == state.state)
        return;
    record.state = state.state;

    msg::SceneObjectState out = state;
    out.raidEpoch = _raidEpoch;
    wire::Writer writer;
    out.Write(writer);
    Emit(Op::SObjectState, 0, writer.Bytes());
    Log::Info("scene object: key=" + std::to_string(state.key) +
              " kind=" + std::to_string(state.kind) +
              " state=" + std::to_string(state.state) +
              " by=" + std::to_string(fromPlayerId));
}

void World::SendSceneObjectsTo(std::uint16_t playerId)
{
    for (const auto& [key, record] : _sceneObjects) {
        msg::SceneObjectState out;
        out.key = key;
        out.kind = record.kind;
        out.state = record.state;
        out.raidEpoch = _raidEpoch;
        wire::Writer writer;
        out.Write(writer);
        Emit(Op::SObjectState, playerId, writer.Bytes());
    }
}

void World::SubmitExplosion(std::uint16_t fromPlayerId, const msg::ExplosionEvent& explosion)
{
    if (explosion.raidEpoch != _raidEpoch || _raidEpoch == 0)
        return;
    if (FindPlayer(fromPlayerId) == kInvalidEntity)
        return;

    PendingExplosion pending;
    pending.x = explosion.x;
    pending.y = explosion.y;
    pending.z = explosion.z;
    pending.radiusCm = explosion.radiusCm;
    pending.damage = explosion.damage;
    pending.tick = _currentTick;
    _pendingExplosions[fromPlayerId] = pending;

    msg::ExplosionEvent out = explosion;
    out.explosionId = _nextExplosionId++;
    out.raidEpoch = _raidEpoch;
    wire::Writer writer;
    out.Write(writer);
    Emit(Op::SExplosion, 0, writer.Bytes());
    if (_nextExplosionLogTick.count(fromPlayerId) == 0 ||
        _currentTick - _nextExplosionLogTick[fromPlayerId] >= 100) {
        _nextExplosionLogTick[fromPlayerId] = _currentTick;
        Log::Info("explosion: by=" + std::to_string(fromPlayerId) +
                  " at=" + std::to_string(explosion.x) + "," + std::to_string(explosion.y) +
                  "," + std::to_string(explosion.z) +
                  " radius=" + std::to_string(explosion.radiusCm) +
                  " damage=" + std::to_string(explosion.damage) +
                  " weapon=" + std::to_string(explosion.weaponId));
    }
}

std::uint16_t World::FindPendingExplosionDamage(std::uint16_t reporterPlayerId,
                                                const msg::HitEvent& hit) const
{
    const PendingExplosion* pending = FindPendingExplosion(reporterPlayerId, hit);
    return pending != nullptr ? pending->damage : 0;
}

const World::PendingExplosion* World::FindPendingExplosion(std::uint16_t reporterPlayerId,
                                                           const msg::HitEvent& hit) const
{
    const auto it = _pendingExplosions.find(reporterPlayerId);
    if (it == _pendingExplosions.end())
        return nullptr;
    const PendingExplosion& pending = it->second;
    // Explosions and their victim hits are sent on the same reliable channel, in order.
    if (_currentTick - pending.tick > 100)
        return nullptr;
    const std::int64_t dx = static_cast<std::int64_t>(hit.hitX) - pending.x;
    const std::int64_t dy = static_cast<std::int64_t>(hit.hitY) - pending.y;
    const std::int64_t dz = static_cast<std::int64_t>(hit.hitZ) - pending.z;
    const std::int64_t dist2 = dx * dx + dy * dy + dz * dz;
    const std::int64_t reach = static_cast<std::int64_t>(pending.radiusCm) + 150;
    if (dist2 > reach * reach)
        return nullptr;
    return &pending;
}

void World::SubmitImpactFx(std::uint16_t fromPlayerId, const msg::ImpactFx& impact)
{
    if (_raidEpoch == 0 || impact.raidEpoch != _raidEpoch)
        return;
    if (FindPlayer(fromPlayerId) == kInvalidEntity)
        return;

    if (impact.sourceKind == 0) {
        // A player may only report their own impacts.
        if (impact.sourceId != fromPlayerId)
            return;
    } else if (impact.sourceKind == 1) {
        const Entity entity = FindAi(impact.sourceId);
        const auto* tag = entity != kInvalidEntity ? _ecs.Get<AiTag>(entity) : nullptr;
        if (tag == nullptr || tag->ownerPlayerId != fromPlayerId || tag->state != 1)
            return;
    } else {
        return;
    }

    const std::uint64_t key =
        (static_cast<std::uint64_t>(impact.sourceKind) << 32) | impact.sourceId;
    const auto previous = _impactSeq.find(key);
    if (previous != _impactSeq.end() && impact.shotId <= previous->second)
        return;
    _impactSeq[key] = impact.shotId;

    // Presentation only: relay as-is on the unreliable channel, never touch gameplay state.
    wire::Writer writer;
    impact.Write(writer);
    Emit(Op::SImpactFx, 0, writer.Bytes(), 0);
}

void World::SubmitAiAnim(std::uint16_t fromPlayerId, const msg::AiAnim& anim)
{
    if (anim.raidEpoch != _raidEpoch)
        return;
    const Entity entity = FindAi(anim.aiId);
    const auto* tag = entity != kInvalidEntity ? _ecs.Get<AiTag>(entity) : nullptr;
    if (tag == nullptr || tag->ownerPlayerId != fromPlayerId || tag->state != 1)
        return;
    const auto previous = _aiAnimSeq.find(anim.aiId);
    if (previous != _aiAnimSeq.end() &&
        static_cast<std::int16_t>(anim.seq - previous->second) <= 0)
        return;
    _aiAnimSeq[anim.aiId] = anim.seq;

    wire::Writer writer;
    anim.Write(writer);
    Emit(Op::SAiAnim, 0, writer.Bytes(), 0);
}

void World::SendPlayerBuff(std::uint16_t playerId, std::uint32_t buffId, std::uint8_t layers,
                          std::uint32_t remainingMs, std::uint8_t op) const
{
    msg::PlayerBuff buff;
    buff.playerId = playerId;
    buff.buffId = buffId;
    buff.layers = layers;
    buff.remainingMs = remainingMs;
    buff.op = op;
    wire::Writer writer;
    buff.Write(writer);
    const_cast<World*>(this)->Emit(Op::SPlayerBuff, 0, writer.Bytes());
}

void World::SendBuffsTo(std::uint16_t playerId) const
{
    const auto it = _playerBuffs.find(playerId);
    if (it == _playerBuffs.end())
        return;
    for (const auto& [buffId, active] : it->second) {
        const std::uint32_t remaining = active.expireTick < 0
                                            ? 0
                                            : static_cast<std::uint32_t>(
                                                  std::max(0, active.expireTick - _currentTick) * 50);
        SendPlayerBuff(playerId, buffId, active.layers, remaining,
                       static_cast<std::uint8_t>(msg::PlayerBuffOp::Add));
    }
}

bool World::ApplyBuff(std::uint16_t playerId, std::uint32_t buffId, std::uint8_t layers,
                      std::uint16_t sourcePlayerId, std::uint32_t weaponId, int depth)
{
    if (_buffModel == nullptr || !_buffModel->Loaded() || depth > 8 || layers == 0)
        return false;
    const combat::BuffDef* def = _buffModel->Find(buffId);
    if (def == nullptr || FindPlayer(playerId) == kInvalidEntity)
        return false;

    auto& buffs = _playerBuffs[playerId];
    if (!def->exclusiveTag.empty() && def->exclusiveTag != "NotExclusive") {
        std::vector<std::uint32_t> remove;
        for (const auto& [existingId, active] : buffs) {
            if (existingId == buffId)
                continue;
            const combat::BuffDef* existing = _buffModel->Find(existingId);
            if (existing == nullptr || existing->exclusiveTag != def->exclusiveTag)
                continue;
            const double oldRemaining =
                !existing->limitedLifeTime || active.expireTick < 0
                    ? 0.0
                    : static_cast<double>(std::max(0, active.expireTick - _currentTick)) * 0.05;
            const auto decision = combat::DecideExclusiveBuff(
                existing->exclusiveTagPriority, existing->limitedLifeTime, oldRemaining,
                def->exclusiveTagPriority, def->limitedLifeTime, def->totalLifeTime);
            if (decision == combat::ExclusiveDecision::RejectNew)
                return false;
            if (decision == combat::ExclusiveDecision::KeepOld) {
                // Native keeps the old buff state and only updates the source.
                auto& old = buffs[existingId];
                old.sourcePlayerId = sourcePlayerId;
                old.weaponId = weaponId;
                return true;
            }
            remove.push_back(existingId);
        }
        for (const std::uint32_t existingId : remove)
            RemoveBuff(playerId, existingId, 0, true, depth + 1);
    }

    const auto existing = buffs.find(buffId);
    const bool isNew = existing == buffs.end();
    std::uint8_t newLayers = layers;
    ActiveBuff* active = nullptr;
    if (isNew) {
        ActiveBuff created;
        created.buffId = buffId;
        created.layers = std::min(def->maxLayers, layers);
        created.sourcePlayerId = sourcePlayerId;
        created.weaponId = weaponId;
        created.tickAccumulators.assign(def->effects.size(), 0.0);
        active = &buffs.emplace(buffId, std::move(created)).first->second;
    } else {
        active = &existing->second;
        newLayers = static_cast<std::uint8_t>(
            std::min<std::uint32_t>(def->maxLayers, active->layers + layers));
        active->layers = newLayers;
        active->sourcePlayerId = sourcePlayerId;
        active->weaponId = weaponId;
    }
    active->expireTick = def->limitedLifeTime && def->totalLifeTime > 0.0
                             ? _currentTick + std::max(1, static_cast<int>(def->totalLifeTime * 20.0))
                             : -1;

    const std::uint32_t remaining =
        active->expireTick < 0 ? 0 : static_cast<std::uint32_t>((active->expireTick - _currentTick) * 50);
    SendPlayerBuff(playerId, buffId, active->layers, remaining,
                   static_cast<std::uint8_t>(isNew ? msg::PlayerBuffOp::Add
                                                   : msg::PlayerBuffOp::Update));
    RefreshPlayerMaxHealth(playerId);

    if (isNew) {
        for (const combat::BuffEffect& effect : def->effects) {
            if (effect.trigger == combat::BuffTriggerKind::OnSetItem)
                ExecuteBuffEffect(playerId, *def, effect, sourcePlayerId, weaponId, depth + 1);
        }
    } else {
        TriggerPlayerBuffs(playerId, combat::BuffTriggerKind::OnLayerChanged, 0, depth + 1);
    }
    return true;
}

bool World::RemoveBuff(std::uint16_t playerId, std::uint32_t buffId, std::uint8_t layers,
                       bool removeAll, int depth)
{
    if (depth > 8)
        return false;
    const auto player = _playerBuffs.find(playerId);
    if (player == _playerBuffs.end())
        return false;
    const auto it = player->second.find(buffId);
    if (it == player->second.end())
        return false;

    if (removeAll || it->second.layers <= layers) {
        player->second.erase(it);
        SendPlayerBuff(playerId, buffId, 0, 0,
                       static_cast<std::uint8_t>(msg::PlayerBuffOp::Remove));
    } else {
        it->second.layers = static_cast<std::uint8_t>(it->second.layers - layers);
        const std::uint32_t remaining =
            it->second.expireTick < 0 ? 0 : static_cast<std::uint32_t>(
                                                   std::max(0, it->second.expireTick - _currentTick) * 50);
        SendPlayerBuff(playerId, buffId, it->second.layers, remaining,
                       static_cast<std::uint8_t>(msg::PlayerBuffOp::Update));
    }
    RefreshPlayerMaxHealth(playerId);
    TriggerPlayerBuffs(playerId, combat::BuffTriggerKind::OnLayerChanged);
    return true;
}

void World::ApplyBuffDamage(std::uint16_t playerId, std::uint32_t buffId,
                            const combat::BuffAction& action, std::uint8_t layers)
{
    const Entity entity = FindPlayer(playerId);
    auto* health = entity != kInvalidEntity ? _ecs.Get<Health>(entity) : nullptr;
    if (health == nullptr || !health->alive)
        return;
    double raw = action.damageValue;
    if (action.percentDamage)
        raw = health->maxHp * action.percentDamageValue;
    raw *= static_cast<double>(layers);
    const std::int32_t damage =
        std::max(0, static_cast<std::int32_t>(std::lround(raw)));
    const std::int32_t oldHp = health->hp;
    health->hp = std::max(1, oldHp - damage);

    msg::CombatEventV4 event;
    event.eventId = _nextEventId++;
    event.causeId = buffId;
    event.type = 0;
    event.sourceKind = 3; // buff
    event.sourceId = 0;
    event.targetKind = 0;
    event.targetId = playerId;
    event.damage = static_cast<std::uint32_t>(damage);
    event.hpAfter = health->hp;
    event.maxHp = health->maxHp;
    event.element = static_cast<std::uint8_t>(combat::ElementFromString(action.element));
    event.crit = 0;
    event.flags = action.realDamage ? (1u << 5) : 0;
    if (oldHp - damage < 1)
        event.flags |= 1u << 1; // hpClamped
    wire::Writer writer;
    event.Write(writer);
    Emit(Op::SCombatEvent, 0, writer.Bytes());
}

bool World::BuffFiltersPass(std::uint16_t playerId, const combat::BuffEffect& effect) const
{
    const auto player = _playerBuffs.find(playerId);
    if (player == _playerBuffs.end())
        return false;
    for (const combat::BuffFilter& filter : effect.filters) {
        switch (filter.kind) {
        case combat::BuffFilter::Kind::CharacterRunning:
            // ADR-022: server does not infer client movement state.
            return false;
        case combat::BuffFilter::Kind::NotHurtForSeconds: {
            const auto hurt = _lastPlayerHurtTick.find(playerId);
            const std::int32_t last = hurt == _lastPlayerHurtTick.end() ? -1000000 : hurt->second;
            if (_currentTick - last < static_cast<int>(filter.timeSeconds * 20.0))
                return false;
            break;
        }
        case combat::BuffFilter::Kind::CheckBuff: {
            const auto checked = player->second.find(filter.checkBuffId);
            if (checked == player->second.end())
                return false;
            break;
        }
        case combat::BuffFilter::Kind::BuffLayers: {
            const auto checked = player->second.find(filter.buffId);
            if (checked == player->second.end() || checked->second.layers < filter.layers)
                return false;
            break;
        }
        case combat::BuffFilter::Kind::ElementFactor: {
            combat::CombatStats stats;
            if (!BuildServerPlayerStats(playerId, stats))
                return false;
            const auto element = combat::ElementFromString(filter.element);
            const double value = stats.elementFactors[static_cast<std::size_t>(element)];
            if (!combat::CompareElementFactor(value, filter.compareType, filter.compareTo))
                return false;
            break;
        }
        }
    }
    return true;
}

void World::ExecuteBuffEffect(std::uint16_t playerId, const combat::BuffDef& def,
                              const combat::BuffEffect& effect, std::uint16_t sourcePlayerId,
                              std::uint32_t weaponId, int depth)
{
    if (!BuffFiltersPass(playerId, effect))
        return;
    const auto player = _playerBuffs.find(playerId);
    if (player == _playerBuffs.end())
        return;
    const auto activeIt = player->second.find(def.id);
    if (activeIt == player->second.end())
        return;
    const std::uint8_t layers = activeIt->second.layers;
    for (const combat::BuffAction& action : effect.actions) {
        switch (action.kind) {
        case combat::BuffAction::Kind::AddBuff:
            ApplyBuff(playerId, action.buffId, 1, sourcePlayerId, weaponId, depth + 1);
            break;
        case combat::BuffAction::Kind::RemoveBuff:
            RemoveBuff(playerId, action.buffId, 1, !action.removeOneLayer, depth + 1);
            break;
        case combat::BuffAction::Kind::Damage:
            ApplyBuffDamage(playerId, def.id, action, layers);
            break;
        case combat::BuffAction::Kind::Modifier:
            // Modifier actions are reflected by BuildServerPlayerStats on state changes.
            RefreshPlayerMaxHealth(playerId);
            break;
        }
    }
}

void World::TriggerPlayerBuffs(std::uint16_t playerId, combat::BuffTriggerKind trigger,
                               std::int32_t damage, int depth, bool attack)
{
    if (_buffModel == nullptr || !_buffModel->Loaded() || depth > 8)
        return;
    const auto player = _playerBuffs.find(playerId);
    if (player == _playerBuffs.end())
        return;
    std::vector<std::pair<std::uint32_t, std::size_t>> effects;
    for (const auto& [buffId, active] : player->second) {
        const combat::BuffDef* def = _buffModel->Find(buffId);
        if (def == nullptr)
            continue;
        for (std::size_t i = 0; i < def->effects.size(); ++i) {
            const combat::BuffEffect& effect = def->effects[i];
            if (effect.trigger != trigger)
                continue;
            if (trigger == combat::BuffTriggerKind::OnShoot) {
                if (attack ? !effect.onAttack : !effect.onShoot)
                    continue;
            }
            if (trigger == combat::BuffTriggerKind::OnTakeDamage &&
                effect.damageThreshold > static_cast<double>(damage))
                continue;
            if (trigger == combat::BuffTriggerKind::OnLayerChanged) {
                if (effect.needMaxLayers && active.layers < def->maxLayers)
                    continue;
                if (effect.triggerBuffId != 0 && effect.triggerBuffId != buffId &&
                    player->second.find(effect.triggerBuffId) == player->second.end())
                    continue;
            }
            effects.emplace_back(buffId, i);
        }
    }
    for (const auto& [buffId, index] : effects) {
        const combat::BuffDef* def = _buffModel->Find(buffId);
        if (def == nullptr || index >= def->effects.size())
            continue;
        const auto current = _playerBuffs.find(playerId);
        if (current == _playerBuffs.end())
            break;
        const auto active = current->second.find(buffId);
        if (active == current->second.end())
            continue;
        ExecuteBuffEffect(playerId, *def, def->effects[index], active->second.sourcePlayerId,
                          active->second.weaponId, depth);
    }
}

void World::TickBuffs()
{
    if (_buffModel == nullptr || !_buffModel->Loaded())
        return;
    std::vector<std::uint16_t> players;
    players.reserve(_playerBuffs.size());
    for (const auto& [playerId, _] : _playerBuffs)
        players.push_back(playerId);

    for (const std::uint16_t playerId : players) {
        const auto player = _playerBuffs.find(playerId);
        if (player == _playerBuffs.end())
            continue;
        std::vector<std::uint32_t> ids;
        ids.reserve(player->second.size());
        for (const auto& [buffId, _] : player->second)
            ids.push_back(buffId);

        std::vector<std::uint32_t> expired;
        for (const std::uint32_t buffId : ids) {
            auto activeIt = player->second.find(buffId);
            if (activeIt == player->second.end())
                continue;
            if (activeIt->second.expireTick >= 0 && _currentTick >= activeIt->second.expireTick) {
                expired.push_back(buffId);
                continue;
            }
            const combat::BuffDef* def = _buffModel->Find(buffId);
            if (def == nullptr)
                continue;
            if (activeIt->second.tickAccumulators.size() != def->effects.size())
                activeIt->second.tickAccumulators.resize(def->effects.size(), 0.0);
            for (std::size_t i = 0; i < def->effects.size(); ++i) {
                const combat::BuffEffect& effect = def->effects[i];
                if (effect.trigger != combat::BuffTriggerKind::Tick || effect.periodSeconds <= 0.0)
                    continue;
                activeIt = player->second.find(buffId);
                if (activeIt == player->second.end())
                    break;
                const int triggers = combat::AdvanceTickAccumulator(
                    activeIt->second.tickAccumulators[i], 0.05, effect.periodSeconds);
                for (int trigger = 0; trigger < triggers; ++trigger) {
                    activeIt = player->second.find(buffId);
                    if (activeIt == player->second.end())
                        break;
                    const std::uint16_t source = activeIt->second.sourcePlayerId;
                    const std::uint32_t weapon = activeIt->second.weaponId;
                    ExecuteBuffEffect(playerId, *def, effect, source, weapon, 0);
                    if (!effect.allowMultipleTrigger)
                        break;
                }
            }
        }
        for (const std::uint32_t buffId : expired)
            RemoveBuff(playerId, buffId, 0, true, 1);
    }
}

void World::SubmitAiAim(std::uint16_t fromPlayerId, const msg::AiAim& aim)
{
    if (aim.raidEpoch != _raidEpoch)
        return;
    const Entity entity = FindAi(aim.aiId);
    const auto* tag = entity != kInvalidEntity ? _ecs.Get<AiTag>(entity) : nullptr;
    if (tag == nullptr || tag->ownerPlayerId != fromPlayerId || tag->state != 1)
        return;
    const auto previous = _aiAimSeq.find(aim.aiId);
    if (previous != _aiAimSeq.end() &&
        static_cast<std::int16_t>(aim.seq - previous->second) <= 0)
        return;
    _aiAimSeq[aim.aiId] = aim.seq;

    wire::Writer writer;
    aim.Write(writer);
    Emit(Op::SAiAim, 0, writer.Bytes(), 0);
}

void World::SubmitAiStateSample(std::uint16_t fromPlayerId, const msg::AiStateSample& sample)
{
    if (sample.raidEpoch != _raidEpoch)
        return;
    const Entity entity = FindAi(sample.aiId);
    const auto* tag = entity != kInvalidEntity ? _ecs.Get<AiTag>(entity) : nullptr;
    if (tag == nullptr || tag->ownerPlayerId != fromPlayerId || tag->state != 1)
        return;
    const auto previous = _aiStateSampleSeq.find(sample.aiId);
    if (previous != _aiStateSampleSeq.end() &&
        static_cast<std::int16_t>(sample.seq - previous->second) <= 0)
        return;
    _aiStateSampleSeq[sample.aiId] = sample.seq;
    wire::Writer writer;
    sample.Write(writer);
    Emit(Op::SAiStateSample, 0, writer.Bytes(), 0);
}

void World::SubmitAiLoadoutV7(std::uint16_t fromPlayerId, const msg::AiLoadoutV7& loadout)
{
    if (loadout.raidEpoch != _raidEpoch || loadout.slots.size() > 4 || loadout.activeSlot >= 4)
        return;
    const Entity entity = FindAi(loadout.aiId);
    const auto* tag = entity != kInvalidEntity ? _ecs.Get<AiTag>(entity) : nullptr;
    if (tag == nullptr || tag->ownerPlayerId != fromPlayerId || tag->state != 1)
        return;

    msg::AiLoadoutV7 safe = loadout;
    safe.slots.clear();
    std::array<bool, 4> seen{};
    for (const auto& slot : loadout.slots) {
        if (slot.slot >= 4 || seen[slot.slot] || slot.blob.size() > 256) {
            Log::Warn("ai loadout v7 slot dropped: ai=" + std::to_string(loadout.aiId) +
                      " slot=" + std::to_string(slot.slot) +
                      " len=" + std::to_string(slot.blob.size()));
            continue;
        }
        seen[slot.slot] = true;
        safe.slots.push_back(slot);
    }
    _aiLoadoutSnapshots[loadout.aiId] = safe;
    SendAiLoadoutV7(safe, 0);
}

void World::SubmitAiShot(std::uint16_t fromPlayerId, const msg::AiShot& shot)
{
    if (shot.raidEpoch != _raidEpoch)
        return;
    const Entity entity = FindAi(shot.aiId);
    const auto* tag = entity != kInvalidEntity ? _ecs.Get<AiTag>(entity) : nullptr;
    if (tag == nullptr || tag->ownerPlayerId != fromPlayerId || tag->state != 1)
        return;
    const auto previous = _aiShotSeq.find(shot.aiId);
    if (previous != _aiShotSeq.end() && shot.shotId <= previous->second)
        return;
    _aiShotSeq[shot.aiId] = shot.shotId;
    wire::Writer writer;
    shot.Write(writer);
    Emit(Op::SAiShot, 0, writer.Bytes(), 0);
}

void World::SubmitPlayerStateSample(std::uint16_t fromPlayerId,
                                    const msg::PlayerStateSample& sample)
{
    if (sample.raidEpoch != _raidEpoch || FindPlayer(fromPlayerId) == kInvalidEntity)
        return;
    const auto previous = _playerStateSeq.find(fromPlayerId);
    if (previous != _playerStateSeq.end() &&
        static_cast<std::int16_t>(sample.seq - previous->second) <= 0)
        return;
    _playerStateSeq[fromPlayerId] = sample.seq;

    wire::Writer writer;
    writer.U16(fromPlayerId);
    sample.Write(writer);
    Emit(Op::SPlayerStateSample, 0, writer.Bytes(), 0);
}

void World::SubmitPlayerLoadoutV8(std::uint16_t fromPlayerId,
                                  const msg::PlayerLoadoutV8& loadout)
{
    if (loadout.raidEpoch != _raidEpoch || loadout.slot >= 10 || loadout.activeSlot >= 10 ||
        loadout.blob.size() > 256 || FindPlayer(fromPlayerId) == kInvalidEntity)
    {
        static std::int32_t lastWarnTick = -1000;
        if (_currentTick - lastWarnTick >= 100) {
            lastWarnTick = _currentTick;
            Log::Warn("player loadout V8 rejected: player=" + std::to_string(fromPlayerId) +
                      " epoch=" + std::to_string(loadout.raidEpoch) + "/" +
                      std::to_string(_raidEpoch) + " slot=" + std::to_string(loadout.slot) +
                      " active=" + std::to_string(loadout.activeSlot) +
                      " bytes=" + std::to_string(loadout.blob.size()));
        }
        return;
    }
    auto& mirror = _playerMirror[fromPlayerId];
    mirror.activeSlot = loadout.activeSlot;
    mirror.slots[loadout.slot] = loadout.blob;

    wire::Writer writer;
    writer.U16(fromPlayerId);
    loadout.Write(writer);
    Emit(Op::SPlayerLoadoutV8, 0, writer.Bytes());
}

void World::SubmitPlayerShot(std::uint16_t fromPlayerId, const msg::PlayerShot& shot)
{
    if (shot.raidEpoch != _raidEpoch || FindPlayer(fromPlayerId) == kInvalidEntity)
        return;
    const auto previous = _playerShotSeq.find(fromPlayerId);
    if (previous != _playerShotSeq.end() && shot.shotId <= previous->second)
        return;
    _playerShotSeq[fromPlayerId] = shot.shotId;

    wire::Writer writer;
    writer.U16(fromPlayerId);
    shot.Write(writer);
    // Gun/muzzle facts are cosmetic and ride the unreliable channel. A grenade throw reuses
    // this message (flag bit 1) to drive a visual projectile on the other clients; losing it
    // would leave the whole flight missing, so that one goes reliable.
    const std::uint8_t channel = (shot.flags & 0x02) != 0 ? 1 : 0;
    Emit(Op::SPlayerShot, 0, writer.Bytes(), channel);
}

void World::SubmitPlayerFaceV8(std::uint16_t fromPlayerId, const msg::PlayerFaceV8& face)
{
    if (face.raidEpoch != _raidEpoch || face.face.size() > 1024 ||
        FindPlayer(fromPlayerId) == kInvalidEntity)
        return;
    auto& mirror = _playerMirror[fromPlayerId];
    mirror.hasFace = true;
    mirror.face = face.face;

    wire::Writer writer;
    writer.U16(fromPlayerId);
    face.Write(writer);
    Emit(Op::SPlayerFaceV8, 0, writer.Bytes());
}

void World::SendPlayerMirrorTo(std::uint16_t targetPlayerId, std::uint16_t sourcePlayerId,
                               const PlayerMirrorState& mirror)
{
    if (mirror.hasFace) {
        wire::Writer writer;
        writer.U16(sourcePlayerId);
        msg::PlayerFaceV8 face;
        face.raidEpoch = _raidEpoch;
        face.face = mirror.face;
        face.Write(writer);
        Emit(Op::SPlayerFaceV8, targetPlayerId, writer.Bytes());
    }
    // M2-v8.1 D-1: appearance is replayable state, so every slot is sent even when the
    // blob is empty. The client needs all 8 visible slots to distinguish "empty" from
    // "not received yet" (it ignores 8/9).
    for (std::size_t slot = 0; slot < mirror.slots.size(); ++slot) {
        wire::Writer writer;
        writer.U16(sourcePlayerId);
        msg::PlayerLoadoutV8 loadout;
        loadout.raidEpoch = _raidEpoch;
        loadout.activeSlot = mirror.activeSlot;
        loadout.slot = static_cast<std::uint8_t>(slot);
        loadout.blob = mirror.slots[slot];
        loadout.Write(writer);
        Emit(Op::SPlayerLoadoutV8, targetPlayerId, writer.Bytes());
    }
}

void World::ReplayPlayerMirror(std::uint16_t joiningPlayerId)
{
    for (const auto& [playerId, mirror] : _playerMirror) {
        if (playerId == joiningPlayerId)
            continue;
        SendPlayerMirrorTo(joiningPlayerId, playerId, mirror);
    }
}

void World::BroadcastPlayerMirror(std::uint16_t playerId)
{
    const auto mirror = _playerMirror.find(playerId);
    if (mirror == _playerMirror.end())
        return;
    for (const auto& [entity, player] : _ecs.All<PlayerComponent>()) {
        (void)entity;
        if (player.playerId == 0 || player.playerId == playerId)
            continue;
        SendPlayerMirrorTo(player.playerId, playerId, mirror->second);
    }
}

void World::SubmitShootFact(std::uint16_t fromPlayerId, const msg::ShootFact& fact)
{
    if (fact.sourceKind == 0) {
        if (fact.sourceId != fromPlayerId)
            return;
    } else if (fact.sourceKind == 1) {
        const Entity ai = FindAi(fact.sourceId);
        const auto* tag = ai != kInvalidEntity ? _ecs.Get<AiTag>(ai) : nullptr;
        if (tag == nullptr || tag->ownerPlayerId != fromPlayerId)
            return;
    } else {
        return;
    }
    if (fact.sourceKind == 0)
        TriggerPlayerBuffs(fromPlayerId, combat::BuffTriggerKind::OnShoot, 0, 0,
                           (fact.flags & 1u) != 0);
    Log::Info("shoot fact: reporter=" + std::to_string(fromPlayerId) +
              " source=" + std::to_string(fact.sourceId) +
              " weapon=" + std::to_string(fact.weaponId) +
              " attack=" + std::to_string(fact.attackId));
}

void World::SubmitUseConsumable(std::uint16_t fromPlayerId, const msg::UseConsumable& use)
{
    if (_buffModel == nullptr || !_buffModel->Loaded())
        return;
    if (FindPlayer(fromPlayerId) == kInvalidEntity)
        return;

    const auto dedupKey = std::make_pair(fromPlayerId, use.correlationId);
    if (_consumableDedup.find(dedupKey) != _consumableDedup.end())
        return;
    const auto nextIt = _consumableNextTick.find(fromPlayerId);
    if (nextIt != _consumableNextTick.end() && _currentTick < nextIt->second)
        return;

    const combat::ItemUsage* usage = _buffModel->FindItem(use.itemId);
    if (usage == nullptr) {
        Log::Warn("consumable rejected: player=" + std::to_string(fromPlayerId) +
                  " item=" + std::to_string(use.itemId));
        return;
    }
    _consumableDedup[dedupKey] = _currentTick;
    _consumableNextTick[fromPlayerId] =
        _currentTick + std::max(1, static_cast<int>(std::lround(usage->useTime * 20.0)));

    std::uniform_real_distribution<double> chance(0.0, 1.0);
    for (const auto& operation : usage->operations) {
        if (operation.kind == combat::ItemUsage::Operation::Kind::Add) {
            if (chance(_rng) <= operation.add.chance)
                ApplyBuff(fromPlayerId, operation.add.buffId, 1, fromPlayerId, 0, 0);
        } else {
            RemoveBuff(fromPlayerId, operation.remove.buffId, operation.remove.layers,
                       !operation.remove.limitLayers, 0);
        }
    }

    // Contract 2.16.0: durable medical items (first-aid kits, stack=1) keep the native use
    // path, so they never send CItemConsume. Their native `<drug heal>` is settled here, on
    // the same completion event, and broadcast as CombatEventV4 type=2. A stackable item
    // reports the same use as CItemConsume as well (flag set), which already healed it.
    if (usage->drugHeal > 0.0 &&
        (use.flags & msg::UseConsumable::kFlagSettledByItemConsume) == 0)
        ApplyConsumeHeal(fromPlayerId,
                         static_cast<std::uint32_t>(std::lround(usage->drugHeal)));

    Log::Info("consumable used: player=" + std::to_string(fromPlayerId) +
              " item=" + std::to_string(use.itemId) +
              " slot=" + std::to_string(use.slot) +
              " heal=" + std::to_string(std::lround(usage->drugHeal)) +
              " correlation=" + std::to_string(use.correlationId));
}

Entity World::FindPlayer(std::uint16_t playerId) const
{
    for (const auto& [entity, player] : _ecs.All<PlayerComponent>()) {
        if (player.playerId == playerId)
            return entity;
    }
    return kInvalidEntity;
}

bool World::BuildServerPlayerStats(std::uint16_t playerId, combat::CombatStats& out) const
{
    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity || _stats == nullptr || !_stats->Loaded())
        return false;
    const auto* player = _ecs.Get<PlayerComponent>(entity);
    if (player == nullptr || !player->hasLoadout)
        return false;

    std::vector<std::uint32_t> itemIds;
    for (const std::uint32_t itemId : player->loadout) {
        if (itemId != 0)
            itemIds.push_back(itemId);
    }
    if (itemIds.empty())
        return false;
    std::vector<combat::StatModifier> buffModifiers;
    if (_buffModel != nullptr && _buffModel->Loaded()) {
        const auto playerBuffs = _playerBuffs.find(playerId);
        if (playerBuffs != _playerBuffs.end()) {
            for (const auto& [buffId, active] : playerBuffs->second) {
                const auto modifiers = _buffModel->Modifiers(buffId, active.layers);
                buffModifiers.insert(buffModifiers.end(), modifiers.begin(), modifiers.end());
            }
        }
    }
    out = _stats->ToCombatStats(itemIds, buffModifiers);
    ApplyWeather(out);
    out.isMainCharacter = true;
    out.hasPreset = false;
    return true;
}

void World::ApplyWeather(combat::CombatStats& stats) const
{
    if (_weather != 2) // only Rainy has native element modifiers
        return;
    stats.elementFactors[1] = std::clamp(stats.elementFactors[1] - 0.15, 0.0, 2.0);
    stats.elementFactors[4] = std::clamp(stats.elementFactors[4] + 0.20, 0.0, 2.0);
}

void World::RefreshPlayerMaxHealth(std::uint16_t playerId)
{
    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity)
        return;
    // Contract 2.16.0: max HP is fixed. Neither the reconstructed content stats (perk trees,
    // equipment) nor the client-reported CPlayerStats.maxHealth decide the health bar any
    // more, so every player renders hp / kPlayerMaxHp.
    combat::CombatStats reconstructed;
    if (BuildServerPlayerStats(playerId, reconstructed) &&
        std::abs(reconstructed.maxHealth - static_cast<double>(kPlayerMaxHp)) > 0.5 &&
        _maxHpMismatchLogged.insert(playerId).second) {
        // Once per player: the content keeps its own number, only the wire value is pinned.
        Log::Warn("player max hp pinned: player=" + std::to_string(playerId) +
                  " reconstructed=" + std::to_string(reconstructed.maxHealth) + " -> " +
                  std::to_string(kPlayerMaxHp));
    }
    if (auto* health = _ecs.Get<Health>(entity)) {
        health->maxHp = kPlayerMaxHp;
        health->hp = std::min(health->hp, health->maxHp);
    }
}

void World::EmitCombat(std::uint8_t type, std::uint8_t targetKind, std::uint32_t targetId,
                       std::uint16_t playerId, std::int32_t value)
{
    msg::CombatEventV4 event;
    event.eventId = _nextEventId++;
    event.type = type;
    event.sourceKind = 0; // legacy CHitReport path: reported by a player
    event.sourceId = playerId;
    event.reporterPlayerId = playerId;
    event.targetKind = targetKind;
    event.targetId = targetId;
    event.damage = 0;
    event.hpAfter = value;
    // Contract 2.16.0: every combat event carries the max HP the client renders. The legacy
    // CHitReport/self-damage path used to leave this at 0.
    const Entity target = targetKind == 1 ? FindAi(targetId)
                                          : FindPlayer(static_cast<std::uint16_t>(targetId));
    if (target != kInvalidEntity) {
        if (const auto* health = _ecs.Get<Health>(target))
            event.maxHp = health->maxHp;
    }
    event.flags = type == 1 ? (1u << 2) : 0; // died flag for legacy death events

    wire::Writer writer;
    event.Write(writer);
    Emit(Op::SCombatEvent, 0, writer.Bytes());
}

void World::ApplyHitEvents()
{
    auto events = std::move(_pendingHitEvents);
    _pendingHitEvents.clear();

    for (const auto& pending : events) {
        const auto& hit = pending.hit;
        const bool isExplosionHit =
            (hit.flags & static_cast<std::uint8_t>(msg::HitFlags::Explosion)) != 0;
        // M2-v10 diagnostics: explosion hits must be visible even if a later validation
        // drops them silently.
        if (isExplosionHit) {
            const std::uint32_t diagKey = 0x40000000u | hit.hitId;
            const auto dlast = _hitDropLogTick.find(diagKey);
            if (dlast == _hitDropLogTick.end() || _currentTick - dlast->second >= 20) {
                _hitDropLogTick[diagKey] = _currentTick;
                Log::Info("explosion hit in: reporter=" + std::to_string(pending.reporterPlayerId) +
                          " src=" + std::to_string(hit.sourceKind) + ":" +
                          std::to_string(hit.sourceId) + " tgt=" +
                          std::to_string(hit.targetKind) + ":" +
                          std::to_string(hit.targetId) +
                          " weapon=" + std::to_string(hit.weaponId));
            }
        }

        // M2-v10: a self-thrown grenade hit enters ApplyHitEvents and then disappears
        // without a combat event. Log every pre-damage rejection instead of silently
        // continuing. Rate-limited per hit id so retransmits do not spam the log.
        const auto logExplosionDrop = [&](const std::string& reason) {
            if (!isExplosionHit)
                return;
            const std::uint32_t key = 0x70000000u | (hit.hitId & 0x0FFFFFFFu);
            const auto last = _hitDropLogTick.find(key);
            if (last != _hitDropLogTick.end() && _currentTick - last->second < 20)
                return;
            _hitDropLogTick[key] = _currentTick;
            Log::Info("explosion hit dropped: reason=" + reason + " reporter=" +
                      std::to_string(pending.reporterPlayerId) + " src=" +
                      std::to_string(hit.sourceKind) + ":" + std::to_string(hit.sourceId) +
                      " tgt=" + std::to_string(hit.targetKind) + ":" +
                      std::to_string(hit.targetId) + " weapon=" +
                      std::to_string(hit.weaponId));
        };

        Entity source = kInvalidEntity;
        if (hit.sourceKind == 0) {
            if (hit.reporterPlayerId != static_cast<std::uint16_t>(hit.sourceId)) {
                logExplosionDrop("reporter!=source");
                continue;
            }
            source = FindPlayer(static_cast<std::uint16_t>(hit.sourceId));
        } else if (hit.sourceKind == 1) {
            source = FindAi(hit.sourceId);
            const auto* sourceTag = source != kInvalidEntity ? _ecs.Get<AiTag>(source) : nullptr;
            if (sourceTag == nullptr || sourceTag->ownerPlayerId != pending.reporterPlayerId ||
                sourceTag->state != 1) {
                logExplosionDrop("source-ai-invalid");
                continue;
            }
        } else {
            // Environment/AOE attribution is not enabled in D5 yet.
            logExplosionDrop("source-kind-unsupported");
            continue;
        }

        const Entity target = hit.targetKind == 1
                                  ? FindAi(hit.targetId)
                                  : FindPlayer(static_cast<std::uint16_t>(hit.targetId));
        if (source == kInvalidEntity || target == kInvalidEntity) {
            logExplosionDrop(source == kInvalidEntity ? "source-not-found" : "target-not-found");
            continue;
        }
        // Self-hits are rejected for weapons (exploit guard), but a grenade at your own
        // feet is a legitimate native self-damage path (canHurtSelf).
        if (source == target &&
            (hit.flags & static_cast<std::uint8_t>(msg::HitFlags::Explosion)) == 0)
            continue;

        auto* targetHealth = _ecs.Get<Health>(target);
        if (targetHealth == nullptr || !targetHealth->alive) {
            logExplosionDrop(targetHealth == nullptr
                                 ? "target-no-health"
                                 : "target-dead(hp=" + std::to_string(targetHealth->hp) + ")");
            continue;
        }
        // M2-v11: dormant AI is not part of the world for combat purposes.
        if (hit.targetKind == 1) {
            const auto* targetTag = _ecs.Get<AiTag>(target);
            if (targetTag == nullptr || targetTag->state != 1)
                continue;
        }

        const std::uint64_t dedupKey =
            (static_cast<std::uint64_t>(pending.reporterPlayerId) << 32) | hit.hitId;
        const auto duplicate = _hitEventDedup.find(dedupKey);
        if (duplicate != _hitEventDedup.end() && _currentTick - duplicate->second < 200) {
            logExplosionDrop("duplicate");
            continue;
        }
        _hitEventDedup[dedupKey] = _currentTick;

        const auto* sourcePos = _ecs.Get<Position>(source);
        const auto* targetPos = _ecs.Get<Position>(target);
        if (sourcePos != nullptr && targetPos != nullptr) {
            const std::int64_t dx = static_cast<std::int64_t>(sourcePos->x) - targetPos->x;
            const std::int64_t dy = static_cast<std::int64_t>(sourcePos->y) - targetPos->y;
            const std::int64_t dz = static_cast<std::int64_t>(sourcePos->z) - targetPos->z;
            const std::int64_t sourceDist2 = dx * dx + dy * dy + dz * dz;
            if (sourceDist2 > kMaxHitRangeCm * kMaxHitRangeCm) {
                // The server only receives coarse AI transform updates; a moving AI can
                // legitimately be farther from its last synced position than the limit
                // while its client-side shot point is still on the player. For an
                // owner-reported AI鈫抪layer hit, validate the shot point instead.
                const std::int64_t hx = static_cast<std::int64_t>(hit.hitX) - targetPos->x;
                const std::int64_t hy = static_cast<std::int64_t>(hit.hitY) - targetPos->y;
                const std::int64_t hz = static_cast<std::int64_t>(hit.hitZ) - targetPos->z;
                const std::int64_t hitDist2 = hx * hx + hy * hy + hz * hz;
                if (hit.sourceKind != 1 || hit.targetKind != 0 || hitDist2 > kMaxHitRangeCm * kMaxHitRangeCm) {
                    // Diagnostic: the hit was discarded before the weapon/damage checks.
                    // Rate-limit to one line per source/target pair per 5s so a burst of
                    // AI fire does not flood the log.
                    const std::uint32_t dropKey = (hit.sourceId << 16) ^ hit.targetId;
                    const auto last = _hitDropLogTick.find(dropKey);
                    if (last == _hitDropLogTick.end() || _currentTick - last->second >= 100) {
                        _hitDropLogTick[dropKey] = _currentTick;
                        Log::Info("hit drop range: source=" + std::to_string(hit.sourceId) +
                                  " target=" + std::to_string(hit.targetId) +
                                  " reporter=" + std::to_string(pending.reporterPlayerId) +
                                  " srcPos=" + std::to_string(sourcePos->x) + "," +
                                  std::to_string(sourcePos->y) + "," + std::to_string(sourcePos->z) +
                                  " tgtPos=" + std::to_string(targetPos->x) + "," +
                                  std::to_string(targetPos->y) + "," + std::to_string(targetPos->z) +
                                  " hit=" + std::to_string(hit.hitX) + "," +
                                  std::to_string(hit.hitY) + "," + std::to_string(hit.hitZ) +
                                  " sourceDist2=" + std::to_string(sourceDist2) +
                                  " hitDist2=" + std::to_string(hitDist2));
                    }
                    logExplosionDrop("range");
                    continue;
                }
            }
        }

        const MonsterDef* sourceMonster = nullptr;
        if (hit.sourceKind == 1 && _content != nullptr) {
            if (const auto* tag = _ecs.Get<AiTag>(source))
                sourceMonster = _content->FindMonster(tag->monsterId);
        }
        const MonsterDef* targetMonster = nullptr;
        if (hit.targetKind == 1 && _content != nullptr) {
            if (const auto* tag = _ecs.Get<AiTag>(target))
                targetMonster = _content->FindMonster(tag->monsterId);
        }

        const std::string sourceTeam = sourceMonster != nullptr ? sourceMonster->team : "player";
        const std::string targetTeam = targetMonster != nullptr ? targetMonster->team : "player";
        // Native explosions are AOE: the client computes damage for every character in
        // radius, including teammates and the thrower. The friendly-fire gate only guards
        // direct weapon/melee hits between teammates.
        if (!isExplosionHit && source != target && !sourceTeam.empty() &&
            sourceTeam != "all" && sourceTeam == targetTeam) {
            logExplosionDrop("same-team(" + sourceTeam + ")");
            continue;
        }

        combat::CombatStats attacker;
        attacker.isMainCharacter = hit.sourceKind == 0;
        if (sourceMonster != nullptr) {
            attacker.gunDamageMultiplier = sourceMonster->damageMultiplier;
            attacker.meleeDamageMultiplier = sourceMonster->meleeDamageMultiplier;
            attacker.gunCritRateGain = sourceMonster->gunCritRateGain;
            attacker.aiCombatFactor = sourceMonster->aiCombatFactor;
            attacker.elementFactors = sourceMonster->elementFactors;
            attacker.hasPreset = true;
        } else if (hit.sourceKind == 0) {
            combat::CombatStats reconstructed;
            if (BuildServerPlayerStats(static_cast<std::uint16_t>(hit.sourceId), reconstructed)) {
                attacker = reconstructed;
            } else {
                msg::PlayerStats stats;
                if (GetPlayerStats(static_cast<std::uint16_t>(hit.sourceId), stats)) {
                    attacker.gunDamageMultiplier = stats.gunDamageMultiplier;
                    attacker.gunCritRateGain = stats.gunCritRateGain;
                    attacker.gunCritDamageGain = stats.gunCritDamageGain;
                    attacker.meleeDamageMultiplier = stats.meleeDamageMultiplier;
                    attacker.meleeCritRateGain = stats.meleeCritRateGain;
                    attacker.meleeCritDamageGain = stats.meleeCritDamageGain;
                    for (std::size_t i = 0; i < attacker.elementFactors.size(); ++i)
                        attacker.elementFactors[i] = stats.elementFactors[i];
                }
            }
        }

        combat::CombatStats victim;
        victim.isMainCharacter = hit.targetKind == 0;
        if (targetMonster != nullptr) {
            victim.elementFactors = targetMonster->elementFactors;
            ApplyWeather(victim);
            victim.hasPreset = true;
            victim.aiCombatFactor = targetMonster->aiCombatFactor;
            msg::AiLoadout loadout;
            if (GetAiLoadout(hit.targetId, loadout) && _content != nullptr) {
                if (const ArmorDef* armor = _content->FindArmor(loadout.armorId))
                    victim.bodyArmor += armor->bodyArmor;
                if (const ArmorDef* helmet = _content->FindArmor(loadout.helmetId))
                    victim.headArmor += helmet->headArmor;
            }
        } else {
            combat::CombatStats reconstructed;
            if (BuildServerPlayerStats(static_cast<std::uint16_t>(hit.targetId), reconstructed)) {
                victim = reconstructed;
            } else {
                msg::PlayerStats stats;
                if (GetPlayerStats(static_cast<std::uint16_t>(hit.targetId), stats)) {
                    victim.bodyArmor = stats.bodyArmor;
                    victim.headArmor = stats.headArmor;
                    for (std::size_t i = 0; i < victim.elementFactors.size(); ++i)
                        victim.elementFactors[i] = stats.elementFactors[i];
                }
            }
        }

        std::uint32_t weaponId = hit.weaponId;
        std::uint32_t ammoId = hit.ammoId;
        if (hit.sourceKind == 1) {
            msg::AiLoadout loadout;
            // A zero-loadout report means the client has not resolved the AI's current
            // weapon (e.g. an early spawn/mirror). Do not let it overwrite the weapon id
            // carried by the authoritative hit event; otherwise the melee/ranged lookup
            // fails and the hit is silently dropped.
            if (GetAiLoadout(hit.sourceId, loadout) && loadout.weaponId != 0) {
                weaponId = loadout.weaponId;
                ammoId = loadout.bulletId;
            }
        }

        const WeaponDef* weapon = _content != nullptr ? _content->FindWeapon(weaponId) : nullptr;
        const MeleeDef* melee = _content != nullptr ? _content->FindMelee(weaponId) : nullptr;
        const AmmoDef* ammo = _content != nullptr ? _content->FindAmmo(ammoId) : nullptr;
        const bool isMelee = melee != nullptr && weapon == nullptr;
        // M2-v10: explosion-flagged hits whose weapon id has no table entry (grenades,
        // breakable barrels with weaponId 0) take their damage from the reporter's most
        // recent CExplosion. Every other hit still needs a known weapon.
        std::uint16_t reportedExplosionDamage = 0;
        const PendingExplosion* pendingExplosion = nullptr;
        if (!isMelee && weapon == nullptr) {
            if (!isExplosionHit)
                continue;
            pendingExplosion = FindPendingExplosion(pending.reporterPlayerId, hit);
            reportedExplosionDamage =
                pendingExplosion != nullptr ? pendingExplosion->damage : 0;
            if (reportedExplosionDamage == 0) {
                const std::uint32_t dropKey =
                    0x80000000u | (static_cast<std::uint32_t>(hit.targetId) << 8) |
                    hit.targetKind;
                const auto last = _hitDropLogTick.find(dropKey);
                if (last == _hitDropLogTick.end() || _currentTick - last->second >= 40) {
                    _hitDropLogTick[dropKey] = _currentTick;
                    const auto it = _pendingExplosions.find(pending.reporterPlayerId);
                    Log::Info(
                        "explosion hit drop: reporter=" + std::to_string(pending.reporterPlayerId) +
                        " target=" + std::to_string(hit.targetId) +
                        " kind=" + std::to_string(hit.targetKind) +
                        " weapon=" + std::to_string(hit.weaponId) +
                        " hit=" + std::to_string(hit.hitX) + "," + std::to_string(hit.hitY) +
                        "," + std::to_string(hit.hitZ) +
                        " pending=" + (it == _pendingExplosions.end() ? "none" : "yes") +
                        (it == _pendingExplosions.end()
                             ? ""
                             : " center=" + std::to_string(it->second.x) + "," +
                                   std::to_string(it->second.y) + "," +
                                   std::to_string(it->second.z) +
                                   " radius=" + std::to_string(it->second.radiusCm) +
                                   " age=" + std::to_string(_currentTick - it->second.tick)));
                }
                continue;
            }
        }

        // One native blast can report the same victim several times: the client's remote
        // mirror carries more than one damageable Health under a single player id, so AOE
        // overlap damages each of them and every hit maps to the same player. A single
        // explosion must apply once per victim or the victim loses double HP.
        if (pendingExplosion != nullptr) {
            const std::uint64_t key =
                (static_cast<std::uint64_t>(pending.reporterPlayerId) << 32) |
                (static_cast<std::uint64_t>(hit.targetKind) << 31) |
                static_cast<std::uint64_t>(hit.targetId);
            const auto last = _explosionHitTick.find(key);
            if (last != _explosionHitTick.end() && last->second == pendingExplosion->tick) {
                logExplosionDrop("duplicate-same-explosion");
                continue;
            }
            _explosionHitTick[key] = pendingExplosion->tick;
        }

        combat::DamageInput input;
        input.isMelee = isMelee;
        input.weapon = isMelee ? nullptr : weapon;
        input.melee = melee;
        input.ammo = isMelee ? nullptr : ammo;
        input.attacker = attacker;
        input.victim = victim;
        input.isHeadshot =
            (hit.flags & static_cast<std::uint8_t>(msg::HitFlags::Headshot)) != 0;
        input.isRealDamage =
            (hit.flags & static_cast<std::uint8_t>(msg::HitFlags::RealDamage)) != 0;
        input.isExplosion =
            (hit.flags & static_cast<std::uint8_t>(msg::HitFlags::Explosion)) != 0;
        input.hitDistanceCm =
            sourcePos != nullptr && targetPos != nullptr
                ? static_cast<double>(std::sqrt(
                      static_cast<double>(sourcePos->x - targetPos->x) *
                          (sourcePos->x - targetPos->x) +
                      static_cast<double>(sourcePos->y - targetPos->y) *
                          (sourcePos->y - targetPos->y) +
                      static_cast<double>(sourcePos->z - targetPos->z) *
                          (sourcePos->z - targetPos->z)))
                : 0.0;
        input.targetHp = targetHealth->hp;
        input.targetIsPlayer = hit.targetKind == 0;

        combat::DamageResult result;
        if (reportedExplosionDamage > 0) {
            input.isExplosion = true;
            result = combat::ComputeExplosionDamage(
                input, static_cast<double>(std::min<std::uint16_t>(reportedExplosionDamage, 500)));
        } else {
            const auto preview = combat::ComputeDamage(input);
            std::uniform_real_distribution<double> critDist(0.0, 1.0);
            input.critOverride = critDist(_rng) < preview.critRate;
            result = combat::ComputeDamage(input);
        }
        if (result.finalDamage <= 0.0) {
            if ((hit.flags & static_cast<std::uint8_t>(msg::HitFlags::Explosion)) != 0) {
                const std::uint32_t key = 0x60000000u | hit.hitId;
                const auto last = _hitDropLogTick.find(key);
                if (last == _hitDropLogTick.end() || _currentTick - last->second >= 20) {
                    _hitDropLogTick[key] = _currentTick;
                    Log::Info("explosion zero damage: reporter=" +
                              std::to_string(pending.reporterPlayerId) + " tgt=" +
                              std::to_string(hit.targetKind) + ":" +
                              std::to_string(hit.targetId) + " base=" +
                              std::to_string(reportedExplosionDamage) + " armor=" +
                              std::to_string(input.victim.bodyArmor) + " element=" +
                              std::to_string(input.victim.elementFactors[0]) + " hp=" +
                              std::to_string(input.targetHp));
                }
            }
            continue;
        }

        const std::int32_t oldHp = targetHealth->hp;
        const std::int32_t damage =
            static_cast<std::int32_t>(std::max(0.0, std::round(result.finalDamage)));
        targetHealth->hp = std::max(0, oldHp - damage);
        const bool lethal = targetHealth->hp == 0;
        const bool hpClamped = lethal;

        if (hit.targetKind == 1)
            _lastAiHit[hit.targetId] = {pending.reporterPlayerId, _currentTick};

        if (hit.targetKind == 0) {
            const auto victimId = static_cast<std::uint16_t>(hit.targetId);
            _lastPlayerHurtTick[victimId] = _currentTick;
            TriggerPlayerBuffs(victimId, combat::BuffTriggerKind::OnTakeDamage, damage);
        }

        std::uint8_t flags = 0;
        if (input.isExplosion)
            flags |= 1u << 0;
        if (hpClamped)
            flags |= 1u << 1;
        if (input.isHeadshot)
            flags |= 1u << 4;
        if (input.isRealDamage)
            flags |= 1u << 5;

        msg::CombatEventV4 event;
        event.eventId = _nextEventId++;
        event.causeId = hit.hitId;
        event.type = 0; // damaged
        event.sourceKind = hit.sourceKind;
        event.sourceId = hit.sourceId;
        event.reporterPlayerId = pending.reporterPlayerId;
        event.targetKind = hit.targetKind;
        event.targetId = hit.targetId;
        event.damage = static_cast<std::uint32_t>(damage);
        event.hpAfter = targetHealth->hp;
        event.maxHp = targetHealth->maxHp;
        event.element = static_cast<std::uint8_t>(result.element);
        event.crit = result.crit ? 1 : 0;
        event.hitX = hit.hitX;
        event.hitY = hit.hitY;
        event.hitZ = hit.hitZ;
        event.weaponId = weaponId;
        event.flags = flags;

        wire::Writer writer;
        event.Write(writer);
        Emit(Op::SCombatEvent, 0, writer.Bytes());

        Log::Info("combat hit: source=" + std::to_string(hit.sourceId) +
                  " target=" + std::to_string(hit.targetId) +
                  " damage=" + std::to_string(damage) + " hp=" + std::to_string(oldHp) +
                  "->" + std::to_string(targetHealth->hp) + " crit=" +
                  std::to_string(event.crit) + " clamped=" + std::to_string(hpClamped));

        if (!lethal)
            continue;

        // D6: a lethal hit actually dies now. The old D5 behavior clamped HP to 1, which
        // left AI/players permanently at 1 HP and made corpses unreachable.
        if (hit.targetKind == 1) {
            if (auto* tag = _ecs.Get<AiTag>(target))
                tag->state = 2;
            targetHealth->alive = false;
            SendAiDespawn(hit.targetId, static_cast<std::uint8_t>(msg::AiDespawnReason::Death));
            _events.push_back(WorldEvent{EventType::AiDied, target,
                                         static_cast<std::uint16_t>(hit.sourceId), 0});
            // sourceId is an AI id when sourceKind == 1 (AI vs AI), so label the kind.
            Log::Info("ai died: id=" + std::to_string(hit.targetId) +
                      (hit.sourceKind == 1 ? " by ai " : " by player ") +
                      std::to_string(hit.sourceId));
            DropAiLoot(target, static_cast<std::uint16_t>(hit.sourceId));
            EmitCombat(1, 1, hit.targetId, static_cast<std::uint16_t>(hit.sourceId), 0);
        } else {
            const std::uint16_t victimId = static_cast<std::uint16_t>(hit.targetId);
            EmitCombat(1, 0, victimId, static_cast<std::uint16_t>(hit.sourceId), 0);
            HandlePlayerDeath(target, victimId);
        }
    }
}

void World::ApplyHits()
{
    auto hits = std::move(_pendingHits);
    _pendingHits.clear();

    for (const auto& hit : hits) {
        const Entity target = hit.report.targetKind == 1
                                  ? FindAi(hit.report.targetId)
                                  : FindPlayer(static_cast<std::uint16_t>(hit.report.targetId));

        if (target == kInvalidEntity)
            continue;

        const auto* sourcePos = _ecs.Get<Position>(hit.source);
        const auto* targetPos = _ecs.Get<Position>(target);
        if (sourcePos != nullptr && targetPos != nullptr) {
            const std::int64_t dx = static_cast<std::int64_t>(sourcePos->x) - targetPos->x;
            const std::int64_t dy = static_cast<std::int64_t>(sourcePos->y) - targetPos->y;
            const std::int64_t dz = static_cast<std::int64_t>(sourcePos->z) - targetPos->z;
            if (dx * dx + dy * dy + dz * dz > kMaxHitRangeCm * kMaxHitRangeCm)
                continue;
        }

        const std::uint64_t key = (static_cast<std::uint64_t>(hit.playerId) << 32) ^
                                  (static_cast<std::uint64_t>(hit.report.targetKind) << 31) ^
                                  hit.report.targetId;
        const bool selfReport = hit.report.targetKind == 0 && hit.report.targetId == hit.playerId;
        const auto cooldown = _lastHitTick.find(key);
        if (!selfReport && cooldown != _lastHitTick.end() &&
            _currentTick - cooldown->second < kHitCooldownTicks)
            continue;
        _lastHitTick[key] = _currentTick;

        auto* health = _ecs.Get<Health>(target);
        if (health == nullptr || !health->alive)
            continue;

        if (hit.report.targetKind == 0) {
            const auto* victim = _ecs.Get<PlayerComponent>(target);
            if (victim != nullptr) {
                const auto protect = _spawnProtectUntil.find(victim->playerId);
                if (protect != _spawnProtectUntil.end() && protect->second > _currentTick)
                    continue;
            }
        }

        // D5: remember who recently damaged which AI for the aggro director
        if (hit.report.targetKind == 1)
            _lastAiHit[hit.report.targetId] = {hit.playerId, _currentTick};

        // Contract 2.16.0: a client may report the damage it already took locally
        // (explosion, fall, environment). It only ever damages the reporter, so it is bounded
        // by kMaxSelfReportDamage instead of the cross-player hit cap.
        const std::int32_t damage =
            selfReport
                ? std::max(1, std::min(kMaxSelfReportDamage, hit.report.damage))
                : std::max(1, std::min(kMaxHitDamage, hit.report.damage));
        health->hp = std::max(0, health->hp - damage);

        if (selfReport) {
            Log::Info("self damage: player=" + std::to_string(hit.playerId) + " amount=" +
                      std::to_string(damage) + " hp=" + std::to_string(health->hp) + "/" +
                      std::to_string(health->maxHp));
        }

        EmitCombat(0, hit.report.targetKind, hit.report.targetId, hit.playerId, health->hp);

        if (health->hp > 0)
            continue;

        health->alive = false;
        EmitCombat(1, hit.report.targetKind, hit.report.targetId, hit.playerId, 0);

        if (hit.report.targetKind == 1) {
            if (auto* tag = _ecs.Get<AiTag>(target))
                tag->state = 2;
            SendAiDespawn(hit.report.targetId, static_cast<std::uint8_t>(msg::AiDespawnReason::Death));
            _events.push_back(WorldEvent{EventType::AiDied, target, hit.playerId, 0});
            Log::Info("ai died: id=" + std::to_string(hit.report.targetId) +
                      " by player " + std::to_string(hit.playerId));
            DropAiLoot(target, hit.playerId);
        } else {
            // the victim may differ from the reporter (future PvP / bad reports): all
            // bookkeeping must use the player that actually died
            const auto* victim = _ecs.Get<PlayerComponent>(target);
            const std::uint16_t victimId = victim != nullptr ? victim->playerId : hit.playerId;

            _events.push_back(WorldEvent{EventType::PlayerDied, target, victimId, 0});
            HandlePlayerDeath(target, victimId);
        }
    }
}

Entity World::SpawnAi(std::uint32_t aiId, std::uint16_t ownerPlayerId, std::int32_t x, std::int32_t y, std::int32_t z,
                      std::uint32_t monsterId, std::uint8_t state)
{
    const Entity e = _ecs.Create();
    _ecs.Add<AiTag>(e, AiTag{aiId, monsterId, ownerPlayerId, state});
    _ecs.Add<Position>(e, Position{x, y, z, 0});
    _ecs.Add<Health>(e, Health{100, 100, true});
    _aiByExternalId[aiId] = e;
    return e;
}

Entity World::FindAi(std::uint32_t aiId) const
{
    const auto it = _aiByExternalId.find(aiId);
    return it == _aiByExternalId.end() ? kInvalidEntity : it->second;
}

void World::SetAiSpawns(std::vector<AiSpawnDef> spawns)
{
    _aiSpawns = std::move(spawns);
}

void World::SetContentStore(const IContentStore* content)
{
    _content = content;
}

void World::SetStatModel(const combat::StatModel* stats)
{
    _stats = stats;
}

void World::SetBuffModel(const combat::BuffModel* buffs)
{
    _buffModel = buffs;
}

void World::SetSpawnTableId(std::string id)
{
    _spawnTableId = std::move(id);
}

void World::SetPlayerStats(std::uint16_t playerId, const msg::PlayerStats& stats)
{
    auto clamp = [](float value, float low, float high, float fallback) {
        if (!std::isfinite(value))
            return fallback;
        return std::max(low, std::min(high, value));
    };
    msg::PlayerStats safe = stats;
    safe.gunDamageMultiplier = clamp(stats.gunDamageMultiplier, 0.0f, 10.0f, 1.0f);
    safe.gunCritRateGain = clamp(stats.gunCritRateGain, -1.0f, 10.0f, 0.0f);
    safe.gunCritDamageGain = clamp(stats.gunCritDamageGain, -1.0f, 10.0f, 0.0f);
    safe.meleeDamageMultiplier = clamp(stats.meleeDamageMultiplier, 0.0f, 10.0f, 1.0f);
    safe.meleeCritRateGain = clamp(stats.meleeCritRateGain, -1.0f, 10.0f, 0.0f);
    safe.meleeCritDamageGain = clamp(stats.meleeCritDamageGain, -1.0f, 10.0f, 0.0f);
    for (float& value : safe.elementFactors)
        value = clamp(value, 0.0f, 100.0f, 1.0f);
    safe.bodyArmor = clamp(stats.bodyArmor, 0.0f, 1000.0f, 0.0f);
    safe.headArmor = clamp(stats.headArmor, 0.0f, 1000.0f, 0.0f);
    safe.maxHealth = clamp(stats.maxHealth, 1.0f, 10000.0f, 100.0f);

    // Contract 2.16.0: the reported max health is kept as diagnostic data only. The HP cap is
    // kPlayerMaxHp, so a client whose perk tree / gear produced 86 or 101 is logged once per
    // distinct value instead of silently giving that player a different health bar.
    const auto previous = _playerStats.find(playerId);
    const bool changed = previous == _playerStats.end() ||
                         std::abs(previous->second.maxHealth - safe.maxHealth) > 0.5f;
    if (changed && std::abs(safe.maxHealth - static_cast<float>(kPlayerMaxHp)) > 0.5f) {
        Log::Warn("player max hp report ignored: player=" + std::to_string(playerId) +
                  " clientMaxHp=" + std::to_string(safe.maxHealth) + " -> " +
                  std::to_string(kPlayerMaxHp));
    }

    _playerStats[playerId] = safe;
}

bool World::GetPlayerStats(std::uint16_t playerId, msg::PlayerStats& out) const
{
    const auto it = _playerStats.find(playerId);
    if (it == _playerStats.end())
        return false;
    out = it->second;
    return true;
}

bool World::SetAiLoadout(std::uint16_t reporterPlayerId, const msg::AiLoadout& loadout)
{
    if (loadout.raidEpoch != _raidEpoch)
        return false;
    const auto aiIt = _aiByExternalId.find(loadout.aiId);
    if (aiIt == _aiByExternalId.end())
        return false;
    const auto* tag = _ecs.Get<AiTag>(aiIt->second);
    if (tag == nullptr || tag->ownerPlayerId != reporterPlayerId || tag->state != 1)
        return false;
    if (_content != nullptr) {
        if (loadout.weaponId != 0 && _content->FindWeapon(loadout.weaponId) == nullptr &&
            _content->FindMelee(loadout.weaponId) == nullptr)
            return false;
        if (loadout.bulletId != 0 && _content->FindAmmo(loadout.bulletId) == nullptr)
            return false;
        if (loadout.armorId != 0 && _content->FindArmor(loadout.armorId) == nullptr)
            return false;
        if (loadout.helmetId != 0 && _content->FindArmor(loadout.helmetId) == nullptr)
            return false;
    }
    _aiLoadouts[loadout.aiId] = loadout;
    return true;
}

bool World::GetAiLoadout(std::uint32_t aiId, msg::AiLoadout& out) const
{
    const auto it = _aiLoadouts.find(aiId);
    if (it == _aiLoadouts.end())
        return false;
    out = it->second;
    return true;
}

int World::PlayerBuffLayers(std::uint16_t playerId, std::uint32_t buffId) const
{
    const auto player = _playerBuffs.find(playerId);
    if (player == _playerBuffs.end())
        return 0;
    const auto buff = player->second.find(buffId);
    return buff == player->second.end() ? 0 : static_cast<int>(buff->second.layers);
}

void World::ResetAiSpawn()
{
    _aiSpawned = false;
}

void World::SendAiSpawn(std::uint32_t aiId, std::uint16_t ownerPlayerId, std::uint32_t monsterId,
                        const Position& position, std::int32_t hp, std::uint16_t targetPlayer,
                        std::uint16_t activationRadius, std::uint16_t deactivationRadius)
{
    msg::AiSpawn spawn;
    spawn.aiId = aiId;
    spawn.ownerPlayerId = ownerPlayerId;
    spawn.monsterId = monsterId;
    spawn.x = position.x;
    spawn.y = position.y;
    spawn.z = position.z;
    spawn.yaw = position.yaw;
    spawn.hp = static_cast<std::uint16_t>(std::min<std::int32_t>(65535, std::max(0, hp)));
    spawn.raidEpoch = _raidEpoch;
    spawn.activationRadius = activationRadius;
    spawn.deactivationRadius = deactivationRadius;

    wire::Writer writer;
    spawn.Write(writer);
    Emit(Op::SAiSpawn, targetPlayer, writer.Bytes());
}

void World::SendAiDespawn(std::uint32_t aiId, std::uint8_t reason)
{
    msg::AiDespawn despawn;
    despawn.aiId = aiId;
    despawn.raidEpoch = _raidEpoch;
    despawn.reason = reason;

    wire::Writer writer;
    despawn.Write(writer);
    Emit(Op::SAiDespawn, 0, writer.Bytes());
}

void World::ReplayActiveAi(std::uint16_t playerId)
{
    if (!_raidStarted || playerId == 0)
        return;

    SendRaidRulesTo(playerId);
    SendBuffsTo(playerId);
    ReplayPlayerMirror(playerId);
    // T1b: a resumed player keeps the same playerId, so the others already have no
    // baseline for the new session. The join path is a no-op (no mirror yet).
    BroadcastPlayerMirror(playerId);

    for (const auto& [entity, tag] : _ecs.All<AiTag>()) {
        // M2-v11: dormant AI (state 0) is not streamed to anyone until it activates.
        if (tag.state != 1)
            continue;
        const auto* position = _ecs.Get<Position>(entity);
        const auto* health = _ecs.Get<Health>(entity);
        if (position != nullptr) {
            const auto activation = _aiActivationRadius.find(tag.aiId);
            const auto deactivation = _aiDeactivationRadius.find(tag.aiId);
            SendAiSpawn(tag.aiId, tag.ownerPlayerId, tag.monsterId, *position,
                        health != nullptr ? health->hp : 100, playerId,
                        activation != _aiActivationRadius.end() ? activation->second : 0,
                        deactivation != _aiDeactivationRadius.end() ? deactivation->second : 0);
            const auto snapshot = _aiLoadoutSnapshots.find(tag.aiId);
            if (snapshot != _aiLoadoutSnapshots.end())
                SendAiLoadoutV7(snapshot->second, playerId);
        }
    }
}

void World::SendAiLoadout(const msg::AiLoadout& loadout, std::uint16_t playerId)
{
    wire::Writer writer;
    loadout.Write(writer);
    Emit(Op::SAiLoadout, playerId, writer.Bytes());
}

void World::SendAiLoadoutV7(const msg::AiLoadoutV7& loadout, std::uint16_t playerId)
{
    wire::Writer writer;
    loadout.Write(writer);
    Emit(Op::SAiLoadoutV7, playerId, writer.Bytes());
}

void World::SendRaidRulesTo(std::uint16_t playerId)
{
    msg::RaidRules rules;
    rules.rulesetId = 13; // StandardChallenge
    rules.flags = 0x06;   // advanced=false, fog=true, spawnDeadBody=true
    rules.weather = _weather; // 0 Sunny, 2 Rainy; server formula uses the same value
    rules.timeOfDayMinutes = _timeOfDayMinutes;
    rules.saveDeadbodyCount = 1;
    rules.weatherSeed = _weatherSeed;
    if (_stats != nullptr && _stats->Loaded())
        rules.baseStatsHash = _stats->BaseStatsHash();

    wire::Writer writer;
    rules.Write(writer);
    Emit(Op::SRaidRules, playerId, writer.Bytes());
}

void World::SpawnMapContainers()
{
    _mapContainers.clear();
    _rolledMapContainers.clear();
    if (_content == nullptr)
        return;

    const SpawnTableDef* table = _content->FindSpawnTable(_spawnTableId);
    const std::uint16_t mapId = table != nullptr ? table->mapId : 0;
    const auto& spawns = _content->ContainerSpawns();
    const auto& types = _content->ContainerTypes();
    std::uint32_t index = 0;
    for (const auto& spawn : spawns) {
        if (mapId != 0 && spawn.mapId != mapId)
            continue;
        const ContainerTypeDef* type = _content->FindContainerType(spawn.typeId);
        if (type == nullptr)
            continue;
        ++index;
        const std::uint32_t containerId = kMapContainerBase + index;
        if (_ledger.Capacity(containerId) == 0) {
            if (!_ledger.CreateContainer(containerId, std::max<std::uint16_t>(1, type->capacity)))
                continue;
        } else {
            _ledger.ClearContainer(containerId);
        }
        _mapContainers.emplace(containerId, MapContainerInstance{spawn, *type});
    }

    Log::Info("map containers spawned: " + std::to_string(_mapContainers.size()) +
              " (types=" + std::to_string(types.size()) + ")");
}

void World::SpawnWorldItems()
{
    if (_content == nullptr)
        return;

    const SpawnTableDef* table = _content->FindSpawnTable(_spawnTableId);
    const std::uint16_t mapId = table != nullptr ? table->mapId : 0;
    std::size_t spawned = 0;
    for (const auto& def : _content->WorldItemSpawns()) {
        if (mapId != 0 && def.mapId != mapId)
            continue;
        const std::uint32_t worldItemId =
            SpawnWorldItem(def.itemId, def.count, def.scene, def.x, def.y, def.z, def.yaw, 0);
        if (worldItemId != 0)
            ++spawned;
    }

    Log::Info("world items spawned: " + std::to_string(spawned) + " (mapId=" +
              std::to_string(mapId) + ")");
}

void World::SpawnDebugGroundItem()
{
    if (_debugGroundItemId == 0 || _content == nullptr)
        return;

    const SpawnTableDef* table = _content->FindSpawnTable(_spawnTableId);
    const std::uint16_t mapId = table != nullptr ? table->mapId : 0;
    for (const auto& spawn : _content->PlayerSpawns()) {
        if (mapId != 0 && spawn.mapId != mapId)
            continue;
        // Put it ~1.5 m beside the first player entry point, not under the player.
        const std::uint32_t worldItemId = SpawnWorldItem(
            _debugGroundItemId, 1, spawn.scene, spawn.x + 150, spawn.y, spawn.z, spawn.yaw, 0);
        if (worldItemId != 0) {
            Log::Info("debug ground item spawned: item=" + std::to_string(_debugGroundItemId) +
                      " worldItemId=" + std::to_string(worldItemId) + " near " + spawn.scene +
                      " " + std::to_string(spawn.x + 150) + "," + std::to_string(spawn.y) + "," +
                      std::to_string(spawn.z));
        }
        return;
    }
}

void World::EnsureMapContainerRolled(std::uint32_t containerId)
{
    const auto it = _mapContainers.find(containerId);
    if (it == _mapContainers.end() || _rolledMapContainers.find(containerId) != _rolledMapContainers.end())
        return;
    _rolledMapContainers.insert(containerId);
    if (it->second.type.activeChance < 1.0) {
        std::uniform_real_distribution<double> chance(0.0, 1.0);
        if (chance(_rng) > it->second.type.activeChance)
            return; // v1: container exists but rolled empty (native may hide actor)
    }
    const LootTable* table = _lootTable;
    if (_lootTables != nullptr) {
        const auto configured = _lootTables->find(it->second.type.lootTableId);
        if (configured != _lootTables->end())
            table = &configured->second;
    }
    if (table == nullptr)
        return;
    // v2: the loot table owns the item count ([rollMin, rollMax]); the container
    // type's native capacity is the hard cap and activeChance its empty-chance.
    const auto rolled = RollLoot(*table, _rng, nullptr);
    std::uint16_t slot = 0;
    for (const auto& entry : rolled) {
        if (slot >= it->second.type.capacity)
            break;
        const auto uid = _ledger.CreateItem(entry.itemId, entry.count);
        std::uint16_t placed = 0;
        if (!_ledger.TryPut(containerId, uid, slot, placed))
            break;
        slot = static_cast<std::uint16_t>(placed + 1);
    }
}

void World::BeginRaid(std::int32_t anchorX, std::int32_t anchorY, std::int32_t anchorZ, std::uint16_t ownerPlayerId)
{
    if (_aiSpawned)
        return;

    _aiSpawned = true;
    _raidStarted = true;
    _raidStartTick = _currentTick;
    // M2-v10: scene objects are per-raid state; a new raid starts from the authored scene.
    _sceneObjects.clear();
    _pendingExplosions.clear();

    // T3: every raid starts from the selected ticket (fresh uids, revision 1). Players who
    // joined before the raid are reseeded here; late joiners are seeded in AddPlayer().
    if (!_loadoutTickets.empty()) {
        _playerTicketIndex.clear(); // re-roll each raid
        _raidTicketOrder.resize(_loadoutTickets.size());
        for (std::size_t i = 0; i < _raidTicketOrder.size(); ++i)
            _raidTicketOrder[i] = i;
        std::shuffle(_raidTicketOrder.begin(), _raidTicketOrder.end(), _rng);
        _ticketDealCursor = 0;
        for (auto& [entity, player] : _ecs.All<PlayerComponent>()) {
            (void)entity;
            SeedPlayerInventories(player.playerId);
        }
    }

    SpawnMapContainers();
    SpawnWorldItems();
    SpawnDebugGroundItem();

    const SpawnTableDef* table = _content != nullptr ? _content->FindSpawnTable(_spawnTableId) : nullptr;
    _weather = table != nullptr ? table->weather : 0;
    _timeOfDayMinutes = table != nullptr ? table->timeOfDayMinutes : 720;
    if (table != nullptr && table->weatherSeed != 0) {
        _weatherSeed = table->weatherSeed;
    } else {
        std::uniform_int_distribution<std::uint32_t> seedDist;
        _weatherSeed = seedDist(_rng);
    }
    SendRaidRulesTo(0);
    std::size_t spawned = 0;

    auto choosePreset = [this](const SpawnerDef& spawner) -> std::uint32_t {
        double total = 0.0;
        for (const auto& preset : spawner.presets)
            total += preset.weight;
        if (total <= 0.0)
            return 0;
        std::uniform_real_distribution<double> dist(0.0, total);
        double pick = dist(_rng);
        for (const auto& preset : spawner.presets) {
            pick -= preset.weight;
            if (pick <= 0.0)
                return preset.monsterId;
        }
        return spawner.presets.back().monsterId;
    };

    auto spawnAt = [&](std::uint32_t monsterId, std::int32_t x, std::int32_t y, std::int32_t z,
                       std::uint16_t yaw) {
        if (monsterId == 0)
            return;
        const std::uint32_t aiId = _nextAiInstanceId++;
        // M2-v11: create dormant; ApplyAiStreaming activates AI near players and only then
        // broadcasts SAiSpawn. The entity keeps its hp across sleep/wake cycles. With
        // streaming disabled (tests/debug) everything spawns immediately.
        const Entity entity = SpawnAi(aiId, ownerPlayerId, x, y, z, monsterId,
                                      _aiStreamingEnabled ? 0 : 1);
        std::int32_t hp = 100;
        if (_content != nullptr) {
            if (const MonsterDef* def = _content->FindMonster(monsterId); def != nullptr)
                hp = std::max(1, def->hp);
        }
        if (auto* health = _ecs.Get<Health>(entity)) {
            health->hp = hp;
            health->maxHp = hp;
        }
        if (auto* position = _ecs.Get<Position>(entity))
            position->yaw = yaw;
        const std::uint16_t tableRadius = table != nullptr ? table->activationRadius : 5000;
        std::uint16_t activation = tableRadius;
        if (_content != nullptr) {
            if (const MonsterDef* def = _content->FindMonster(monsterId);
                def != nullptr && def->activationRadius > 0)
                activation = def->activationRadius;
        }
        _aiActivationRadius[aiId] = activation;
        _aiDeactivationRadius[aiId] =
            static_cast<std::uint16_t>(std::min<int>(65535, activation + 1000));
        if (!_aiStreamingEnabled) {
            if (const auto* position = _ecs.Get<Position>(entity))
                SendAiSpawn(aiId, ownerPlayerId, monsterId, *position, hp, 0, activation,
                            _aiDeactivationRadius[aiId]);
        }
        ++spawned;
    };

    if (table != nullptr) {
        for (const auto& spawner : table->spawners) {
            if (!spawner.enabled || spawner.points.empty())
                continue;

            if (spawner.mode == "exact") {
                // exact mode: each configured native point is one spawn; the point's
                // monsterId pins the preset, otherwise the spawner's weighted presets apply.
                for (const auto& point : spawner.points) {
                    const std::uint32_t monsterId =
                        point.monsterId != 0 ? point.monsterId : choosePreset(spawner);
                    spawnAt(monsterId, point.x, point.y, point.z,
                            point.yaw != 0 ? point.yaw : spawner.yaw);
                }
                continue;
            }

            std::uint16_t count = spawner.countMin;
            if (spawner.countMax > spawner.countMin) {
                std::uniform_int_distribution<int> dist(spawner.countMin, spawner.countMax);
                count = static_cast<std::uint16_t>(dist(_rng));
            }
            count = static_cast<std::uint16_t>(
                std::min<std::size_t>(count, spawner.points.size()));

            std::vector<std::size_t> indices(spawner.points.size());
            std::iota(indices.begin(), indices.end(), 0);
            std::shuffle(indices.begin(), indices.end(), _rng);
            for (std::size_t i = 0; i < count; ++i) {
                const auto& point = spawner.points[indices[i]];
                spawnAt(choosePreset(spawner), point.x, point.y, point.z,
                        point.yaw != 0 ? point.yaw : spawner.yaw);
            }
        }

        for (const auto& manual : table->manualSpawns) {
            for (std::uint16_t i = 0; i < manual.count; ++i)
                spawnAt(manual.monsterId, manual.x, manual.y, manual.z, manual.yaw);
        }
    } else {
        // legacy fallback for old --data directories (relative anchor rows)
        for (const auto& def : _aiSpawns) {
            const std::int32_t x = anchorX + def.dx * 100;
            const std::int32_t z = anchorZ + def.dz * 100;
            const Entity entity = SpawnAi(def.aiId, ownerPlayerId, x, anchorY, z);
            if (auto* health = _ecs.Get<Health>(entity)) {
                health->hp = def.hp;
                health->maxHp = def.hp;
            }
            if (auto* position = _ecs.Get<Position>(entity))
                position->yaw = def.yaw;
            if (const auto* position = _ecs.Get<Position>(entity))
                SendAiSpawn(def.aiId, ownerPlayerId, 0, *position, def.hp);
            ++spawned;
        }
    }

    Log::Info("raid ai spawned: " + std::to_string(spawned) +
              (table != nullptr ? " (spawn table " + _spawnTableId + ")" : " (legacy ai_spawns)"));
}

void World::SubmitAiState(std::uint16_t fromPlayerId, const msg::AiState& state)
{
    _pendingAiState.emplace_back(fromPlayerId, state);
}

void World::SubmitLoot(Entity player, std::uint16_t playerId, const msg::LootRequest& request)
{
    _pendingLoot.push_back(PendingLoot{player, playerId, request});
}

void World::SubmitExtract(Entity player, std::uint16_t playerId, const msg::ExtractRequest& request)
{
    _pendingExtract.push_back(PendingExtract{player, playerId, request});
}

void World::SubmitContainerView(std::uint16_t playerId, const msg::ContainerViewRequest& request)
{
    _pendingView.push_back(PendingView{playerId, request});
}

void World::SubmitItemTransfer(std::uint16_t playerId, const msg::ItemTransfer& transfer)
{
    _pendingTransfers.push_back(PendingTransfer{playerId, transfer});
}

void World::SubmitItemModDetach(std::uint16_t playerId, const msg::ItemModDetach& request)
{
    _pendingModDetaches.push_back(PendingItemModDetach{playerId, request});
}

void World::SubmitItemModAttach(std::uint16_t playerId, const msg::ItemModAttach& request)
{
    _pendingModAttaches.push_back(PendingItemModAttach{playerId, request});
}

void World::SubmitItemArrange(std::uint16_t playerId, const msg::ItemArrange& arrange)
{
    _pendingArranges.push_back(PendingArrange{playerId, arrange});
}

void World::SubmitItemConsume(std::uint16_t playerId, const msg::ItemConsume& consume)
{
    _pendingConsumes.push_back(PendingConsume{playerId, consume});
}

void World::SubmitItemReload(std::uint16_t playerId, const msg::ItemReload& reload)
{
    _pendingItemReloads.push_back(PendingItemReload{playerId, reload});
}

void World::SubmitItemWeaponFire(std::uint16_t playerId, const msg::ItemWeaponFire& fire)
{
    _pendingItemWeaponFires.push_back(PendingItemWeaponFire{playerId, fire});
}

void World::SubmitItemDurability(std::uint16_t playerId, const msg::ItemDurability& durability)
{
    _pendingItemDurabilities.push_back(PendingItemDurability{playerId, durability});
}

void World::SubmitBackpackCapacity(std::uint16_t playerId, const msg::BackpackCapacity& capacity)
{
    _pendingBackpackCapacities.push_back(PendingBackpackCapacity{playerId, capacity});
}

void World::SubmitInventorySync(std::uint16_t playerId, const msg::InventorySyncRequest& request)
{
    _pendingInventorySyncs.push_back(PendingInventorySync{playerId, request});
}

void World::SubmitContainerOpen(std::uint16_t playerId, const msg::ContainerOpen& request)
{
    _pendingContainerOpens.push_back(PendingContainerOpen{playerId, request});
}

void World::SubmitWorldItemPickup(std::uint16_t playerId, const msg::WorldItemPickup& request)
{
    _pendingWorldPickups.push_back(PendingWorldPickup{playerId, request});
}

void World::SubmitWorldItemDrop(std::uint16_t playerId, const msg::WorldItemDrop& request)
{
    _pendingWorldDrops.push_back(PendingWorldDrop{playerId, request});
}

void World::ResetInputBaselines()
{
    for (auto& [entity, player] : _ecs.All<PlayerComponent>()) {
        (void)entity;
        player.hasInput = false;
        player.teleportPending = false;
    }
}

void World::ResetInputBaseline(std::uint16_t playerId)
{
    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity)
        return;

    if (auto* player = _ecs.Get<PlayerComponent>(entity)) {
        player->hasInput = false;
        player->teleportPending = false;
    }
}

void World::SetPlayerTeleportTarget(std::uint16_t playerId, std::int32_t x, std::int32_t y,
                                    std::int32_t z)
{
    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity)
        return;

    auto* player = _ecs.Get<PlayerComponent>(entity);
    if (player == nullptr || player->leftRaid)
        return;
    if (const auto* progress = _ecs.Get<PlayerProgress>(entity);
        progress != nullptr && progress->extracted)
        return;

    // The teleport is authoritative as soon as the assignment is sent: snapshots and
    // any combat that arrives before the client's first post-teleport input use the
    // assigned point instead of the pre-teleport spawn anchor.
    player->teleportPending = true;
    player->teleportX = x;
    player->teleportY = y;
    player->teleportZ = z;
    player->hasInput = false;
    player->lastSeq = 0;

    if (auto* position = _ecs.Get<Position>(entity)) {
        position->x = x;
        position->y = y;
        position->z = z;
    }
}

bool World::SetPlayerPosition(Entity entity, std::int32_t x, std::int32_t y, std::int32_t z,
                              const std::string& scene, std::uint32_t raidEpoch)
{
    // Only an active raid accepts spawn reports, and only for its own epoch. A late
    // report from the previous raid must never resurrect a player.
    if (_raidEpoch == 0 || raidEpoch != _raidEpoch || _phase != 1)
        return false;

    auto* player = _ecs.Get<PlayerComponent>(entity);
    auto* progress = _ecs.Get<PlayerProgress>(entity);
    if (player == nullptr || player->leftRaid)
        return false;
    if (progress != nullptr && progress->extracted)
        return false;
    if (const auto* health = _ecs.Get<Health>(entity); health != nullptr && !health->alive)
        return false;

    if (auto* position = _ecs.Get<Position>(entity)) {
        position->x = x;
        position->y = y;
        position->z = z;
    }
    player->teleportPending = false;

    // spawning into a fresh raid always resets the character
    if (auto* health = _ecs.Get<Health>(entity)) {
        health->alive = true;
        health->maxHp = kPlayerMaxHp;
        health->hp = kPlayerMaxHp;
    }

    if (progress != nullptr)
        progress->extracted = false;

    player->hasInput = false;
    player->scene = scene;
    _raidParticipants.insert(player->playerId);
    // Late joiners (and respawns) need the current door/breakable states.
    SendSceneObjectsTo(player->playerId);
    return true;
}

bool World::IsInRaid(std::uint16_t playerId) const
{
    if (_phase != 1 || !_raidStarted)
        return false;

    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity)
        return false;

    const auto* player = _ecs.Get<PlayerComponent>(entity);
    const auto* health = _ecs.Get<Health>(entity);
    const auto* progress = _ecs.Get<PlayerProgress>(entity);
    if (player == nullptr || health == nullptr)
        return false;

    return health->alive && !player->leftRaid && !player->scene.empty() &&
           (progress == nullptr || !progress->extracted);
}

std::string World::PlayerScene(std::uint16_t playerId) const
{
    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity)
        return {};

    const auto* player = _ecs.Get<PlayerComponent>(entity);
    return player != nullptr ? player->scene : std::string{};
}

bool World::PickPlayerSpawn(const std::string& scene, std::uint16_t seat,
                            std::int32_t& x, std::int32_t& y, std::int32_t& z,
                            std::uint16_t& yaw) const
{
    if (_content == nullptr)
        return false;

    const SceneDef* sceneDef = _content->FindScene(scene);
    // Never borrow another map's player spawns for a scene the server content does not
    // know. Example: the client can start Level_Farm_Main while the shipped server data is
    // GroundZero-only; mapId 0 used to match every GroundZero spawn and dropped the second
    // player through the Farm terrain. Returning false lets Gate fall back to the party's
    // first-report anchor, which is inside the scene that client actually loaded.
    if (sceneDef == nullptr)
        return false;

    const std::uint16_t mapId = sceneDef->mapId;
    const PlayerSpawnDef* exact = nullptr;
    std::vector<const PlayerSpawnDef*> matching;
    for (const auto& def : _content->PlayerSpawns()) {
        const bool sameMap = mapId != 0 ? def.mapId == mapId : def.scene == scene;
        if (!sameMap)
            continue;
        matching.push_back(&def);
        if (def.seat == seat)
            exact = &def;
    }
    if (matching.empty())
        return false;

    const PlayerSpawnDef* chosen = exact;
    if (chosen == nullptr)
        chosen = matching[seat % matching.size()];
    x = chosen->x;
    y = chosen->y;
    z = chosen->z;
    yaw = chosen->yaw;
    return true;
}

bool World::IsPlayerAtExit(std::uint16_t playerId) const
{
    if (_content == nullptr || _content->Exits().empty())
        return true; // legacy data: extraction remains position-agnostic

    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity)
        return false;
    const auto* position = _ecs.Get<Position>(entity);
    const auto* player = _ecs.Get<PlayerComponent>(entity);
    if (position == nullptr || player == nullptr)
        return false;

    const SceneDef* sceneDef = _content->FindScene(player->scene);
    const std::uint16_t mapId = sceneDef != nullptr ? sceneDef->mapId : 0;
    for (const auto& exit : _content->Exits()) {
        if (mapId != 0 && exit.mapId != mapId)
            continue;
        const std::int64_t dx = static_cast<std::int64_t>(position->x) - exit.x;
        const std::int64_t dz = static_cast<std::int64_t>(position->z) - exit.z;
        const std::int64_t radius = exit.radius;
        if (dx * dx + dz * dz <= radius * radius)
            return true;
    }
    return false;
}

void World::ProtectPlayer(std::uint16_t playerId, std::int32_t ticks)
{
    if (ticks <= 0)
        _spawnProtectUntil.erase(playerId);
    else
        _spawnProtectUntil[playerId] = _currentTick + ticks;
}

bool World::SetLoadout(std::uint16_t playerId, const std::vector<msg::LoadoutEntry>& entries)
{
    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity)
        return false;

    auto* player = _ecs.Get<PlayerComponent>(entity);
    if (player == nullptr || entries.size() > 10)
        return false;

    std::array<std::uint32_t, 10> next{};
    std::array<bool, 10> seen{};
    for (const auto& entry : entries) {
        if (entry.slot >= next.size() || seen[entry.slot])
            return false;

        // B2: only registry items are accepted; 0 clears the slot
        if (entry.itemId != 0 && _registry != nullptr && _registry->Find(entry.itemId) == nullptr)
            return false;

        seen[entry.slot] = true;
        next[entry.slot] = entry.itemId;
    }

    player->loadout = next;
    player->hasLoadout = true;

    // Contract 2.16.0: the rebuild never changes the max HP (it is pinned to kPlayerMaxHp);
    // it only re-checks the cap after an equipment change.
    RefreshPlayerMaxHealth(playerId);
    return true;
}

bool World::GetLoadout(std::uint16_t playerId, std::vector<msg::LoadoutEntry>& out) const
{
    out.clear();

    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity)
        return false;

    const auto* player = _ecs.Get<PlayerComponent>(entity);
    if (player == nullptr)
        return false;

    for (std::uint8_t slot = 0; slot < player->loadout.size(); ++slot) {
        if (player->loadout[slot] == 0)
            continue;
        out.push_back(msg::LoadoutEntry{slot, player->loadout[slot]});
    }

    return true;
}

std::vector<StoredItem> World::GetStoredItems(std::uint16_t playerId) const
{
    return CollectInventory(playerId);
}

void World::ApplyAiDirector()
{
    if (_phase != 1 || !_raidStarted)
        return;

    // 200 ms cadence at 20 Hz
    if ((_currentTick % 4) != 0)
        return;

    for (const auto& [aiEntity, tag] : _ecs.All<AiTag>()) {
        if (tag.state != 1 || tag.ownerPlayerId == 0)
            continue;

        const auto* aiPos = _ecs.Get<Position>(aiEntity);
        const Entity ownerEntity = FindPlayer(tag.ownerPlayerId);
        if (aiPos == nullptr || ownerEntity == kInvalidEntity)
            continue;

        const auto* owner = _ecs.Get<PlayerComponent>(ownerEntity);
        if (owner == nullptr || owner->scene.empty())
            continue;

        std::uint16_t bestId = 0;
        std::int64_t bestScore = std::numeric_limits<std::int64_t>::max();

        for (const auto& [playerEntity, player] : _ecs.All<PlayerComponent>()) {
            if (player.leftRaid || player.scene.empty() || player.scene != owner->scene)
                continue;

            // M2-v12: a freshly spawned player is damage-immune for a few seconds; letting
            // every nearby AI switch to them produced "the whole pack turns and shoots, but
            // nothing happens". Keep them out of target selection until protection expires.
            const auto protect = _spawnProtectUntil.find(player.playerId);
            if (protect != _spawnProtectUntil.end() && protect->second > _currentTick)
                continue;

            const auto* health = _ecs.Get<Health>(playerEntity);
            const auto* pos = _ecs.Get<Position>(playerEntity);
            if (health == nullptr || !health->alive || pos == nullptr)
                continue;

            const std::int64_t dx = static_cast<std::int64_t>(pos->x) - aiPos->x;
            const std::int64_t dz = static_cast<std::int64_t>(pos->z) - aiPos->z;
            std::int64_t score = dx * dx + dz * dz;

            // recent damage pulls aggro hard (2 s window)
            const auto hit = _lastAiHit.find(tag.aiId);
            if (hit != _lastAiHit.end() && hit->second.first == player.playerId &&
                _currentTick - hit->second.second <= 40)
                score -= 1000000000LL;

            if (score < bestScore) {
                bestScore = score;
                bestId = player.playerId;
            }
        }

        auto& current = _aiTargets[tag.aiId];
        if (current.playerId != 0 && _currentTick - current.switchTick < 40)
            continue; // 2 s switch cooldown
        if (current.playerId == bestId)
            continue;

        current.playerId = bestId;
        current.switchTick = _currentTick;

        msg::AiTarget message;
        message.aiId = tag.aiId;
        message.playerId = bestId;
        message.tick = _currentTick;

        wire::Writer writer;
        message.Write(writer);
        Emit(Op::SAiTarget, tag.ownerPlayerId, writer.Bytes());
    }
}

void World::ApplyAiStreaming()
{
    if (!_aiStreamingEnabled || !_raidStarted || _phase != 1)
        return;

    // Only live players still in the raid keep AI awake.
    std::vector<std::pair<std::int32_t, std::int32_t>> players;
    players.reserve(8);
    for (const auto& [entity, player] : _ecs.All<PlayerComponent>()) {
        if (player.leftRaid)
            continue;
        const auto* health = _ecs.Get<Health>(entity);
        const auto* position = _ecs.Get<Position>(entity);
        if (health == nullptr || !health->alive || position == nullptr)
            continue;
        players.emplace_back(position->x, position->z);
    }
    if (players.empty())
        return;

    // Budgeted per tick: a cluster never bursts dozens of SAiSpawn/despawn at once, and the
    // receiving clients build at most a couple of replicas per frame.
    constexpr int kActivateBudget = 2;
    constexpr int kDeactivateBudget = 2;
    int budgetActivate = kActivateBudget;
    int budgetDeactivate = kDeactivateBudget;

    // Nearest player for an AI, plus the squared distance. The native Duckov client cannot
    // drive 20+ moving AI at once, so each player also gets a budget of active AI
    // (_aiMaxActivePerPlayer); candidates are activated nearest-first.
    auto nearestPlayer = [&players](std::int32_t x, std::int32_t z) {
        std::size_t bestIndex = 0;
        std::int64_t best = std::numeric_limits<std::int64_t>::max();
        for (std::size_t i = 0; i < players.size(); ++i) {
            const std::int64_t dx = static_cast<std::int64_t>(players[i].first) - x;
            const std::int64_t dz = static_cast<std::int64_t>(players[i].second) - z;
            const std::int64_t distance = dx * dx + dz * dz;
            if (distance < best) {
                best = distance;
                bestIndex = i;
            }
        }
        return std::make_pair(bestIndex, best);
    };

    std::vector<int> activePerPlayer(players.size(), 0);
    struct Candidate {
        Entity entity = kInvalidEntity;
        std::size_t playerIndex = 0;
        std::int64_t distanceSq = 0;
        bool boss = false;
    };
    std::vector<Candidate> candidates;

    for (auto& [entity, tag] : _ecs.All<AiTag>()) {
        if (tag.state == 2)
            continue; // dead AI never comes back (V2c respawn is out of scope)

        // M2-v12: only stream AI whose owner is actually in the raid. If the owner is still
        // behind its loading curtain nobody simulates the AI, and streaming the spawn only
        // gives the other clients frozen replicas.
        if (tag.ownerPlayerId != 0) {
            const Entity ownerEntity = FindPlayer(tag.ownerPlayerId);
            const auto* owner = ownerEntity != kInvalidEntity
                                    ? _ecs.Get<PlayerComponent>(ownerEntity)
                                    : nullptr;
            if (owner == nullptr || owner->leftRaid || owner->scene.empty())
                continue;
        }
        const auto* position = _ecs.Get<Position>(entity);
        if (position == nullptr)
            continue;

        const auto activationIt = _aiActivationRadius.find(tag.aiId);
        const auto deactivationIt = _aiDeactivationRadius.find(tag.aiId);
        const std::uint16_t activation =
            activationIt != _aiActivationRadius.end() ? activationIt->second : 5000;
        const std::uint16_t deactivation =
            deactivationIt != _aiDeactivationRadius.end()
                ? deactivationIt->second
                : static_cast<std::uint16_t>(std::min<int>(65535, activation + 1000));

        const auto [playerIndex, nearest] = nearestPlayer(position->x, position->z);
        const std::int64_t activationSq = static_cast<std::int64_t>(activation) * activation;
        const std::int64_t deactivationSq = static_cast<std::int64_t>(deactivation) * deactivation;

        if (tag.state == 1) {
            ++activePerPlayer[playerIndex];
            if (nearest >= deactivationSq && budgetDeactivate > 0) {
                tag.state = 0;
                --budgetDeactivate;
                SendAiDespawn(tag.aiId, static_cast<std::uint8_t>(msg::AiDespawnReason::Cleanup));
                Log::Info("ai streaming sleep: id=" + std::to_string(tag.aiId) + " dist=" +
                          std::to_string(static_cast<int>(std::sqrt(static_cast<double>(nearest)))) +
                          "cm radius=" + std::to_string(deactivation));
            }
            continue;
        }

        if (nearest > activationSq)
            continue;
        bool boss = false;
        if (_content != nullptr) {
            if (const MonsterDef* def = _content->FindMonster(tag.monsterId); def != nullptr)
                boss = def->isBoss;
        }
        candidates.push_back(Candidate{entity, playerIndex, nearest, boss});
    }

    std::sort(candidates.begin(), candidates.end(),
              [](const Candidate& a, const Candidate& b) { return a.distanceSq < b.distanceSq; });

    for (const auto& candidate : candidates) {
        if (budgetActivate <= 0)
            break;
        auto* tag = _ecs.Get<AiTag>(candidate.entity);
        const auto* position = _ecs.Get<Position>(candidate.entity);
        if (tag == nullptr || position == nullptr || tag->state != 0)
            continue;
        if (!candidate.boss && _aiMaxActivePerPlayer > 0 &&
            activePerPlayer[candidate.playerIndex] >= _aiMaxActivePerPlayer) {
            continue;
        }

        tag->state = 1;
        --budgetActivate;
        ++activePerPlayer[candidate.playerIndex];
        const auto* health = _ecs.Get<Health>(candidate.entity);
        const auto activationIt = _aiActivationRadius.find(tag->aiId);
        const auto deactivationIt = _aiDeactivationRadius.find(tag->aiId);
        const std::uint16_t activation =
            activationIt != _aiActivationRadius.end() ? activationIt->second : 5000;
        const std::uint16_t deactivation =
            deactivationIt != _aiDeactivationRadius.end()
                ? deactivationIt->second
                : static_cast<std::uint16_t>(std::min<int>(65535, activation + 1000));
        SendAiSpawn(tag->aiId, tag->ownerPlayerId, tag->monsterId, *position,
                    health != nullptr ? health->hp : 100, 0, activation, deactivation);
        Log::Info("ai streaming spawn: id=" + std::to_string(tag->aiId) + " dist=" +
                  std::to_string(static_cast<int>(std::sqrt(static_cast<double>(candidate.distanceSq)))) +
                  "cm radius=" + std::to_string(activation) + " active=" +
                  std::to_string(activePerPlayer[candidate.playerIndex]));
    }

    if ((_currentTick % 100) != 0)
        return;
    std::size_t active = 0;
    std::size_t dormant = 0;
    std::size_t dead = 0;
    for (const auto& [entity, tag] : _ecs.All<AiTag>()) {
        (void)entity;
        if (tag.state == 1)
            ++active;
        else if (tag.state == 2)
            ++dead;
        else
            ++dormant;
    }
    Log::Info("ai streaming: active=" + std::to_string(active) + " dormant=" +
              std::to_string(dormant) + " dead=" + std::to_string(dead));
}

void World::ReopenRaid()
{
    if (_phase != 3)
        return;

    _phase = 1;
    _settleTicks = 0;
    // A closed raid no longer owns an active simulation. CStartRaid must be allowed to
    // run EndRaid + BeginRaid for the next one; leaving _raidStarted=true here made every
    // subsequent start fail with "raid already running phase=1".
    _raidStarted = false;
    _raidParticipants.clear();

    msg::RaidPhaseMessage message;
    message.phase = _phase;
    message.tick = _lastSnapshot.tick;

    wire::Writer writer;
    message.Write(writer);
    Emit(Op::SRaidPhase, 0, writer.Bytes());
}

void World::ApplyContainerViews()
{
    auto requests = std::move(_pendingView);
    _pendingView.clear();

    for (const auto& pending : requests) {
        std::uint32_t containerId = pending.request.containerId;
        bool accessible = false;

        if (containerId != 0) {
            // Explicit id: unchanged P1 behaviour (raid + scene group + 5 m).
            accessible = CanUseMapContainer(pending.playerId, containerId);
        } else if (IsInRaid(pending.playerId)) {
            // T3.5b: the nearest-container *selection* is filtered by raid + scene group +
            // 5 m, not only the post-selection access check.
            const std::string playerScene = PlayerScene(pending.playerId);
            const Entity player = FindPlayer(pending.playerId);
            const auto* position = player != kInvalidEntity ? _ecs.Get<Position>(player) : nullptr;
            if (position != nullptr && !playerScene.empty()) {
                std::int64_t best = std::numeric_limits<std::int64_t>::max();
                for (const auto& [id, instance] : _mapContainers) {
                    if (!SameSceneGroup(playerScene, instance.spawn.scene))
                        continue;
                    const std::int64_t dx =
                        static_cast<std::int64_t>(position->x) - instance.spawn.x;
                    const std::int64_t dz =
                        static_cast<std::int64_t>(position->z) - instance.spawn.z;
                    const std::int64_t distance = dx * dx + dz * dz;
                    if (distance > 500 * 500) // 5 m interaction range
                        continue;
                    if (distance < best) {
                        best = distance;
                        containerId = id;
                    }
                }
            }
            if (containerId == 0) {
                // No authored container nearby: the legacy box remains available in-raid.
                containerId = kWorldLootContainer;
            }
            accessible = CanUseMapContainer(pending.playerId, containerId);
        }
        // Outside a raid the request keeps containerId=0 and returns an empty view.

        msg::ContainerView view;
        view.correlationId = pending.request.correlationId;
        view.containerId = containerId;
        if (accessible) {
            EnsureMapContainerRolled(containerId);
            view.capacity = _ledger.Capacity(containerId);

            if (const auto* slots = _ledger.Slots(containerId)) {
                for (std::uint16_t slot = 0; slot < slots->size(); ++slot) {
                    const ItemUid uid = (*slots)[slot];
                    if (uid == ItemLedger::kNoItem)
                        continue;

                    const ItemInstance* item = _ledger.GetItem(uid);
                    if (item == nullptr)
                        continue;

                    view.entries.push_back(
                        msg::ContainerViewEntry{slot, uid, item->itemId, item->count});
                }
            }
        }

        wire::Writer writer;
        view.Write(writer);
        Emit(Op::SContainerView, pending.playerId, writer.Bytes());
    }
}

// ---------------------------------------------------------------------------
// v9 item contract: ticket seeding, initial inventory, transfers, snapshots
// ---------------------------------------------------------------------------

const LoadoutTicketDef* World::TicketForPlayer(std::uint16_t playerId)
{
    if (_loadoutTickets.empty())
        return nullptr;
    if (_loadoutTickets.size() == 1)
        return _loadoutTickets.front();

    const auto it = _playerTicketIndex.find(playerId);
    if (it != _playerTicketIndex.end())
        return _loadoutTickets[it->second];

    std::size_t index = 0;
    if (_raidTicketOrder.size() == _loadoutTickets.size() && !_raidTicketOrder.empty()) {
        // Deal without replacement so two players in the same raid do not share a kit;
        // the order is shuffled once per raid.
        index = _raidTicketOrder[_ticketDealCursor % _raidTicketOrder.size()];
        ++_ticketDealCursor;
    } else {
        std::uniform_int_distribution<std::size_t> dist(0, _loadoutTickets.size() - 1);
        index = dist(_rng);
    }
    _playerTicketIndex.emplace(playerId, index);
    return _loadoutTickets[index];
}

void World::SeedPlayerInventories(std::uint16_t playerId)
{
    const LoadoutTicketDef* ticket = TicketForPlayer(playerId);
    if (ticket == nullptr)
        return;
    Log::Info("seed: player=" + std::to_string(playerId) + " ticket=" + ticket->id);

    const std::uint32_t backpackId = InventoryId(playerId);
    const std::uint32_t equipmentId = EquipmentId(playerId);
    const std::uint32_t petBagId = PetBagId(playerId);

    auto prepare = [this](std::uint32_t containerId, std::uint16_t capacity) {
        if (_ledger.Capacity(containerId) == 0)
            _ledger.CreateContainer(containerId, capacity);
        else
            _ledger.ClearContainer(containerId);
    };
    prepare(backpackId, kBackpackCapacity);
    prepare(equipmentId, kEquipmentCapacity);
    prepare(petBagId, kPetBagCapacity);

    for (const auto& slot : ticket->equipment) {
        if (slot.slot >= kEquipmentCapacity || slot.itemId == 0 || slot.count == 0)
            continue;
        const ItemUid uid = _ledger.CreateItem(slot.itemId, slot.count);
        std::uint16_t placed = 0;
        _ledger.TryPut(equipmentId, uid, slot.slot, placed); // explicit equipment slot
        // Contract 2.14.0: pre-installed attachments travel with the item uid.
        if (!slot.mods.empty()) {
            std::vector<ItemAttachment> mods;
            mods.reserve(slot.mods.size());
            for (const auto& mod : slot.mods) {
                if (mod.itemId == 0 || mod.slotKey.empty())
                    continue;
                mods.push_back(ItemAttachment{mod.slotKey, mod.itemId});
            }
            if (!mods.empty())
                _ledger.SetItemMods(uid, std::move(mods));
        }
    }

    auto fillBag = [this](std::uint32_t containerId, const std::vector<LoadoutItemDef>& rows) {
        const std::uint16_t capacity = _ledger.Capacity(containerId);
        std::uint16_t next = 0;
        for (const auto& row : rows) {
            if (row.itemId == 0 || row.count == 0 || next >= capacity)
                continue;
            const ItemUid uid = _ledger.CreateItem(row.itemId, row.count);
            std::uint16_t placed = 0;
            if (!_ledger.TryPut(containerId, uid, next, placed))
                continue;
            next = static_cast<std::uint16_t>(placed + 1);
        }
    };
    fillBag(backpackId, ticket->backpack);
    fillBag(petBagId, ticket->petBag);

    _ledger.SetRevision(backpackId, kInventorySeedRevision);
    _ledger.SetRevision(equipmentId, kInventorySeedRevision);
    _ledger.SetRevision(petBagId, kInventorySeedRevision);
}

bool World::ResolveInventory(std::uint16_t playerId, std::uint8_t kind, std::uint32_t ownerId,
                             std::uint32_t& containerId) const
{
    switch (static_cast<msg::InventoryKind>(kind)) {
    case msg::InventoryKind::PlayerEquipment:
        if (ownerId != playerId)
            return false;
        containerId = EquipmentId(playerId);
        return _ledger.Capacity(containerId) != 0;
    case msg::InventoryKind::PlayerBackpack:
        if (ownerId != playerId)
            return false;
        containerId = InventoryId(playerId);
        return _ledger.Capacity(containerId) != 0;
    case msg::InventoryKind::PlayerPetBag:
        if (ownerId != playerId)
            return false;
        containerId = PetBagId(playerId);
        return _ledger.Capacity(containerId) != 0;
    case msg::InventoryKind::CorpseContainer:
        if (_corpses.find(ownerId) == _corpses.end())
            return false;
        containerId = ownerId;
        return _ledger.Capacity(ownerId) != 0;
    case msg::InventoryKind::MapContainer:
        if (ownerId == 0)
            return false;
        // Only real map containers (100000+) or the legacy world loot box may be addressed;
        // player inventory ids must never be reachable through the MapContainer kind.
        if (ownerId != kWorldLootContainer && ownerId < kMapContainerBase)
            return false;
        containerId = ownerId;
        return _ledger.Capacity(ownerId) != 0;
    default:
        return false;
    }
}

msg::InventorySnapshot World::BuildInventorySnapshot(std::uint8_t kind, std::uint32_t ownerId,
                                                     std::uint32_t containerId) const
{
    msg::InventorySnapshot snapshot;
    snapshot.kind = kind;
    snapshot.ownerId = ownerId;
    snapshot.revision = _ledger.Revision(containerId);
    snapshot.capacity = _ledger.Capacity(containerId);

    if (const auto* slots = _ledger.Slots(containerId)) {
        for (std::uint16_t slot = 0; slot < slots->size(); ++slot) {
            const ItemUid uid = (*slots)[slot];
            if (uid == ItemLedger::kNoItem)
                continue;
            const ItemInstance* item = _ledger.GetItem(uid);
            if (item == nullptr)
                continue;

            msg::InventoryEntry entry;
            entry.slot = slot;
            entry.itemUid = uid;
            entry.itemId = item->itemId;
            entry.count = item->count;
            snapshot.entries.push_back(entry);
        }
    }
    return snapshot;
}

void World::EmitInventorySnapshot(std::uint16_t playerId, std::uint8_t kind, std::uint32_t ownerId,
                                  std::uint32_t containerId, std::uint64_t correlationId)
{
    msg::InventoryState state;
    state.correlationId = correlationId;
    state.snapshot = BuildInventorySnapshot(kind, ownerId, containerId);

    wire::Writer writer;
    state.Write(writer);
    Emit(Op::SInventorySnapshot, playerId, writer.Bytes());
    SendItemDetails(playerId, containerId);
}

void World::SendItemDetails(std::uint16_t playerId, std::uint32_t containerId)
{
    const auto* slots = _ledger.Slots(containerId);
    if (slots == nullptr)
        return;
    for (const ItemUid uid : *slots) {
        if (uid == ItemLedger::kNoItem)
            continue;
        const ItemInstance* item = _ledger.GetItem(uid);
        if (item == nullptr)
            continue;
        const bool hasDetail = item->durability != 0 || item->maxDurability != 0 ||
                               item->loadedAmmoItemId != 0 || item->loadedAmmoCount != 0;
        if (hasDetail) {
            msg::ItemDetail detail;
            detail.itemUid = uid;
            detail.durability = item->durability;
            detail.maxDurability = item->maxDurability;
            detail.loadedAmmoItemId = item->loadedAmmoItemId;
            detail.loadedAmmoCount = item->loadedAmmoCount;

            wire::Writer writer;
            detail.Write(writer);
            Emit(Op::SItemDetail, playerId, writer.Bytes());
        }
        // Always emit the attachment list (even when empty): it is the authoritative set for
        // this uid, so a client can drop an attachment that a detach removed server-side.
        msg::ItemMods mods;
        mods.itemUid = uid;
        mods.mods.reserve(item->mods.size());
        for (const auto& mod : item->mods)
            mods.mods.push_back(msg::ItemMod{mod.slotKey, mod.itemId});

        wire::Writer writer;
        mods.Write(writer);
        Emit(Op::SItemMods, playerId, writer.Bytes());
    }
}

void World::SendInitialInventory(std::uint16_t playerId)
{
    const std::uint32_t backpackId = InventoryId(playerId);
    const std::uint32_t equipmentId = EquipmentId(playerId);
    const std::uint32_t petBagId = PetBagId(playerId);

    msg::InitialInventory message;
    const LoadoutTicketDef* ticket = TicketForPlayer(playerId);
    message.ticketId = ticket != nullptr ? ticket->id : std::string();
    message.revision = std::max({_ledger.Revision(backpackId), _ledger.Revision(equipmentId),
                                 _ledger.Revision(petBagId)});
    // The wire InitialInventory carries one revision for all three player containers. Make
    // the server match that contract; otherwise a pet-bag/backpack transfer right after a
    // respawn uses the global max revision against a lower per-container revision and is
    // rejected as a conflict.
    _ledger.SetRevision(backpackId, message.revision);
    _ledger.SetRevision(equipmentId, message.revision);
    _ledger.SetRevision(petBagId, message.revision);
    message.equipmentCapacity = _ledger.Capacity(equipmentId);
    message.backpackCapacity = _ledger.Capacity(backpackId);
    message.petBagCapacity = _ledger.Capacity(petBagId);

    std::size_t equipmentEntries = 0;
    std::size_t backpackEntries = 0;
    std::size_t petBagEntries = 0;
    auto append = [&](std::uint8_t kind, std::uint32_t containerId, std::size_t& counter) {
        const auto* slots = _ledger.Slots(containerId);
        if (slots == nullptr)
            return;
        for (std::uint16_t slot = 0; slot < slots->size(); ++slot) {
            const ItemUid uid = (*slots)[slot];
            if (uid == ItemLedger::kNoItem)
                continue;
            const ItemInstance* item = _ledger.GetItem(uid);
            if (item == nullptr)
                continue;

            msg::InitialInventoryEntry row;
            row.kind = kind;
            row.entry.slot = slot;
            row.entry.itemUid = uid;
            row.entry.itemId = item->itemId;
            row.entry.count = item->count;
            message.entries.push_back(row);
            ++counter;
        }
    };
    append(static_cast<std::uint8_t>(msg::InventoryKind::PlayerEquipment), equipmentId,
           equipmentEntries);
    append(static_cast<std::uint8_t>(msg::InventoryKind::PlayerBackpack), backpackId,
           backpackEntries);
    append(static_cast<std::uint8_t>(msg::InventoryKind::PlayerPetBag), petBagId, petBagEntries);

    wire::Writer writer;
    message.Write(writer);
    Log::Info("initial inventory wire: player=" + std::to_string(playerId) +
              " entries=" + std::to_string(message.entries.size()) +
              " bytes=" + std::to_string(writer.Bytes().size()));
    Emit(Op::SInitialInventory, playerId, writer.Bytes());
    SendItemDetails(playerId, equipmentId);
    SendItemDetails(playerId, backpackId);
    SendItemDetails(playerId, petBagId);

    Log::Info("initial inventory sent: player " + std::to_string(playerId) + " ticket='" +
              message.ticketId + "' equipment=" + std::to_string(equipmentEntries) +
              " backpack=" + std::to_string(backpackEntries) + " petBag=" +
              std::to_string(petBagEntries) + " revision=" + std::to_string(message.revision));
}

bool World::IsBackpackUnequipBlocked(std::uint16_t playerId, std::uint32_t sourceId,
                                     std::uint16_t sourceSlot) const
{
    if (sourceId != EquipmentId(playerId) || sourceSlot != kBackpackEquipSlot)
        return false;
    // Native rule: a backpack cannot be unequipped while it still holds items.
    return _ledger.AnyItem(InventoryId(playerId));
}

void World::ApplyItemTransfers()
{
    auto requests = std::move(_pendingTransfers);
    _pendingTransfers.clear();

    for (const auto& pending : requests) {
        const auto& transfer = pending.request;
        msg::ItemTransferResult result;
        result.correlationId = transfer.correlationId;

        const std::uint64_t cacheKey = (static_cast<std::uint64_t>(pending.playerId) << 32) |
                                       (transfer.correlationId & 0xFFFFFFFFu);
        const auto cached = _transferCache.find(cacheKey);
        const bool replay = cached != _transferCache.end();
        std::uint32_t sourceContainer = 0;
        std::uint32_t targetContainer = 0;

        if (replay) {
            result = cached->second;
            result.code = static_cast<std::uint8_t>(ErrorCode::IdempotentReplay);
        } else {
            const bool resolved =
                ResolveInventory(pending.playerId, transfer.sourceKind, transfer.sourceId,
                                 sourceContainer) &&
                ResolveInventory(pending.playerId, transfer.targetKind, transfer.targetId,
                                 targetContainer);

            // T3.5: an existing map container is still only usable in-raid/scene/5m.
            ContainerAccess sourceAccess = ContainerAccess::Ok;
            ContainerAccess targetAccess = ContainerAccess::Ok;
            if (resolved &&
                transfer.sourceKind == static_cast<std::uint8_t>(msg::InventoryKind::MapContainer))
                sourceAccess = CheckMapContainerAccess(pending.playerId, sourceContainer);
            if (resolved &&
                transfer.sourceKind == static_cast<std::uint8_t>(msg::InventoryKind::CorpseContainer))
                sourceAccess = CheckCorpseAccess(pending.playerId, sourceContainer);
            if (resolved &&
                transfer.targetKind == static_cast<std::uint8_t>(msg::InventoryKind::MapContainer))
                targetAccess = CheckMapContainerAccess(pending.playerId, targetContainer);

            if (!resolved) {
                result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
            } else if (transfer.targetKind ==
                       static_cast<std::uint8_t>(msg::InventoryKind::CorpseContainer)) {
                // D3: corpse containers are take-only; putting items in is forbidden.
                result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
            } else if (sourceAccess != ContainerAccess::Ok ||
                       targetAccess != ContainerAccess::Ok) {
                result.code = MapContainerErrorCode(sourceAccess != ContainerAccess::Ok
                                                        ? sourceAccess
                                                        : targetAccess);
            } else if (IsBackpackUnequipBlocked(pending.playerId, sourceContainer,
                                                transfer.sourceSlot)) {
                result.code = static_cast<std::uint8_t>(ErrorCode::Conflict);
            } else {
                const auto outcome = _ledger.TransferEx(
                    transfer.itemUid, sourceContainer, transfer.sourceSlot, targetContainer,
                    transfer.targetSlot, transfer.expectedItemId, transfer.expectedRevision,
                    transfer.count, MaxStackForItem(transfer.expectedItemId));
                if (outcome.Ok()) {
                    result.code = 0;
                    result.itemUid = transfer.itemUid;
                    result.sourceRevision = outcome.sourceRevision;
                    result.targetRevision = outcome.targetRevision;
                } else {
                    result.code = TransferErrorCode(outcome.error);
                }
            }

            if (_transferCache.size() > 512)
                _transferCache.clear();
            _transferCache[cacheKey] = result;
        }

        wire::Writer writer;
        result.Write(writer);
        if (!replay && (transfer.sourceKind >= 3 || transfer.targetKind >= 3)) {
            Log::Info("container transfer: player=" + std::to_string(pending.playerId) +
                      " src=" + std::to_string(transfer.sourceKind) + ":" +
                      std::to_string(transfer.sourceId) + "/" +
                      std::to_string(transfer.sourceSlot) + " dst=" +
                      std::to_string(transfer.targetKind) + ":" +
                      std::to_string(transfer.targetId) + "/" +
                      std::to_string(transfer.targetSlot) + " uid=" +
                      std::to_string(transfer.itemUid) + " code=" +
                      std::to_string(result.code));
        }
        Emit(Op::SItemTransfer, pending.playerId, writer.Bytes());

        if (!replay && result.code == 0) {
            // Minimal v9 refresh: player inventories go to the acting player; a changed
            // shared container (map or corpse) is broadcast so every client viewing it can
            // refresh (T3B/T5).
            auto emitChanged = [&](std::uint8_t kind, std::uint32_t ownerId,
                                   std::uint32_t containerId) {
                const bool shared = IsMapContainerId(containerId) ||
                    kind == static_cast<std::uint8_t>(msg::InventoryKind::CorpseContainer);
                if (shared)
                    EmitInventorySnapshot(0, kind, ownerId, containerId, 0);
                else
                    EmitInventorySnapshot(pending.playerId, kind, ownerId, containerId, 0);
            };
            emitChanged(transfer.sourceKind, transfer.sourceId, sourceContainer);
            if (targetContainer != sourceContainer)
                emitChanged(transfer.targetKind, transfer.targetId, targetContainer);
            // ItemDetail carries durability/loaded ammo. A gun moved back from a container
            // or the ground must resend it or the rebuilt native item starts with an empty
            // magazine and 0 durability while the server still has the old detail.
            if (!IsMapContainerId(sourceContainer))
                SendItemDetails(pending.playerId, sourceContainer);
            if (targetContainer != sourceContainer && !IsMapContainerId(targetContainer))
                SendItemDetails(pending.playerId, targetContainer);
        }
    }
}

void World::ApplyItemModDetaches()
{
    auto requests = std::move(_pendingModDetaches);
    _pendingModDetaches.clear();

    for (const auto& pending : requests) {
        const auto& request = pending.request;
        msg::ItemModDetachResult result;
        result.correlationId = request.correlationId;
        result.parentUid = request.parentUid;

        const std::uint32_t backpackId = InventoryId(pending.playerId);
        const std::uint32_t equipmentId = EquipmentId(pending.playerId);
        const ItemInstance* parent = _ledger.GetItem(request.parentUid);

        const auto containerHas = [&](std::uint32_t containerId, ItemUid uid) {
            const auto* slots = _ledger.Slots(containerId);
            if (slots == nullptr)
                return false;
            return std::find(slots->begin(), slots->end(), uid) != slots->end();
        };

        const bool owned = parent != nullptr &&
                           (containerHas(equipmentId, request.parentUid) ||
                            containerHas(backpackId, request.parentUid));

        bool found = false;
        std::uint32_t modItemId = 0;
        if (owned && !request.slotKey.empty()) {
            for (const auto& mod : parent->mods) {
                if (mod.slotKey == request.slotKey) {
                    found = true;
                    modItemId = mod.itemId;
                    break;
                }
            }
        }

        std::uint16_t freeSlot = 0;
        bool hasFreeSlot = false;
        if (found) {
            const auto* slots = _ledger.Slots(backpackId);
            const auto capacity = _ledger.Capacity(backpackId);
            for (std::uint16_t slot = 0; slots != nullptr && slot < capacity; ++slot) {
                if ((*slots)[slot] == ItemLedger::kNoItem) {
                    freeSlot = slot;
                    hasFreeSlot = true;
                    break;
                }
            }
        }

        // Contract 2.16.0: unplugging the magazine of a gun changes its capacity, and the
        // native game empties the magazine into the character's inventory. Mirror that on the
        // server: return the loaded rounds to the backpack and clear the gun's loaded detail.
        // Without this the server keeps the old (larger) magazine loaded, so every later
        // reload is rejected with InvalidArgument and the backpack reserve drifts.
        const std::uint32_t unloadAmmoId =
            found && IsMagazineSlotKey(request.slotKey) ? parent->loadedAmmoItemId : 0;
        const std::uint32_t unloadCount =
            unloadAmmoId != 0 ? parent->loadedAmmoCount : 0;
        bool hasRoomForUnload = true;
        if (unloadCount > 0) {
            // Room = free space in existing stacks of this ammo + free slots left over after
            // the detached attachment takes one.
            const std::uint32_t stack = std::max<std::uint32_t>(1, MaxStackForItem(unloadAmmoId));
            std::uint64_t room = 0;
            std::uint32_t freeSlots = 0;
            const auto* slots = _ledger.Slots(backpackId);
            const auto capacity = _ledger.Capacity(backpackId);
            for (std::uint16_t slot = 0; slots != nullptr && slot < capacity; ++slot) {
                const ItemUid uid = (*slots)[slot];
                if (uid == ItemLedger::kNoItem) {
                    ++freeSlots;
                    continue;
                }
                const auto* item = _ledger.GetItem(uid);
                if (item != nullptr && item->itemId == unloadAmmoId && item->count < stack)
                    room += stack - item->count;
            }
            if (freeSlots > 1)
                room += static_cast<std::uint64_t>(freeSlots - 1) * stack;
            hasRoomForUnload = room >= unloadCount;
        }

        bool unloadedMagazine = false;
        if (!owned || !found) {
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else if (!hasFreeSlot || !hasRoomForUnload) {
            // The attachment stays on the item; the client keeps its native copy.
            result.code = static_cast<std::uint8_t>(ErrorCode::Conflict);
        } else {
            if (unloadCount > 0) {
                // hasRoomForUnload was computed with the same rules AddCount uses (partial
                // stacks first, then free slots minus the one reserved for the attachment), so
                // this add either takes everything or nothing.
                const auto added = _ledger.AddCount(backpackId, unloadAmmoId, unloadCount,
                                                    MaxStackForItem(unloadAmmoId));
                if (added != unloadCount) {
                    Log::Warn("mod detach: magazine unload failed player=" +
                              std::to_string(pending.playerId) + " gun=" +
                              std::to_string(request.parentUid) + " ammo=" +
                              std::to_string(unloadAmmoId) + " count=" +
                              std::to_string(unloadCount) + " added=" + std::to_string(added));
                    result.code = static_cast<std::uint8_t>(ErrorCode::Conflict);
                } else {
                    _ledger.SetItemDetail(request.parentUid, parent->durability,
                                          parent->maxDurability, 0, 0);
                    unloadedMagazine = true;
                }
            }
        }

        if (result.code == 0) {
            const ItemUid newUid = _ledger.CreateItem(modItemId, 1);
            std::uint16_t placed = 0;
            if (newUid == 0 || !_ledger.TryPut(backpackId, newUid, freeSlot, placed)) {
                if (newUid != 0)
                    _ledger.DestroyItem(newUid);
                result.code = static_cast<std::uint8_t>(ErrorCode::Conflict);
            } else {
                std::uint32_t removedItemId = 0;
                _ledger.RemoveItemMod(request.parentUid, request.slotKey, removedItemId);
                result.code = 0;
                result.itemUid = newUid;
            }
        }

        wire::Writer writer;
        result.Write(writer);
        Emit(Op::SItemModDetachResult, pending.playerId, writer.Bytes());

        if (result.code == 0) {
            Log::Info("mod detach: player=" + std::to_string(pending.playerId) +
                      " parent=" + std::to_string(request.parentUid) +
                      " slot=" + request.slotKey +
                      " newUid=" + std::to_string(result.itemUid) +
                      (unloadedMagazine ? " unloadedAmmo=" + std::to_string(unloadAmmoId) +
                                              " rounds=" + std::to_string(unloadCount)
                                        : ""));
            EmitInventorySnapshot(pending.playerId,
                                  static_cast<std::uint8_t>(msg::InventoryKind::PlayerBackpack),
                                  pending.playerId, backpackId, 0);
            SendItemDetails(pending.playerId, equipmentId);
            if (backpackId != equipmentId)
                SendItemDetails(pending.playerId, backpackId);
            if (unloadedMagazine) {
                // SendItemDetails only emits a detail when the item still has a non-default
                // one; an emptied gun has none, so broadcast the cleared record explicitly or
                // every client keeps the old magazine count and can roll it back.
                msg::ItemDetail cleared;
                cleared.itemUid = request.parentUid;
                if (const auto* gun = _ledger.GetItem(request.parentUid)) {
                    cleared.durability = gun->durability;
                    cleared.maxDurability = gun->maxDurability;
                }
                cleared.loadedAmmoItemId = 0;
                cleared.loadedAmmoCount = 0;
                wire::Writer detailWriter;
                cleared.Write(detailWriter);
                Emit(Op::SItemDetail, 0, detailWriter.Bytes());
            }
        }
    }
}

void World::ApplyItemModAttaches()
{
    auto requests = std::move(_pendingModAttaches);
    _pendingModAttaches.clear();

    for (const auto& pending : requests) {
        const auto& request = pending.request;
        msg::ItemModAttachResult result;
        result.correlationId = request.correlationId;
        result.parentUid = request.parentUid;
        result.itemUid = request.itemUid;

        const std::uint32_t backpackId = InventoryId(pending.playerId);
        const std::uint32_t equipmentId = EquipmentId(pending.playerId);

        const auto containerHas = [&](std::uint32_t containerId, ItemUid uid) {
            const auto* slots = _ledger.Slots(containerId);
            if (slots == nullptr)
                return false;
            return std::find(slots->begin(), slots->end(), uid) != slots->end();
        };

        const ItemInstance* parent = _ledger.GetItem(request.parentUid);
        const bool parentOwned = parent != nullptr &&
                                 (containerHas(equipmentId, request.parentUid) ||
                                  containerHas(backpackId, request.parentUid));

        // The item to attach has to be a one-unit backpack stack.
        const ItemInstance* attachItem = _ledger.GetItem(request.itemUid);
        std::uint16_t attachSlot = 0;
        bool attachInBackpack = false;
        if (attachItem != nullptr) {
            const auto* slots = _ledger.Slots(backpackId);
            const auto capacity = _ledger.Capacity(backpackId);
            for (std::uint16_t slot = 0; slots != nullptr && slot < capacity; ++slot) {
                if ((*slots)[slot] == request.itemUid) {
                    attachSlot = slot;
                    attachInBackpack = true;
                    break;
                }
            }
        }

        bool slotOccupied = false;
        if (parent != nullptr && !request.slotKey.empty()) {
            for (const auto& mod : parent->mods) {
                if (mod.slotKey == request.slotKey) {
                    slotOccupied = true;
                    break;
                }
            }
        }

        if (!parentOwned || attachItem == nullptr || !attachInBackpack ||
            request.slotKey.empty() || request.itemUid == request.parentUid) {
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else if (slotOccupied) {
            // The slot already holds an attachment; detach it first.
            result.code = static_cast<std::uint8_t>(ErrorCode::Conflict);
        } else if (attachItem->count != 1) {
            // Attachments are single items; a stack has to be split first.
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else {
            const std::uint32_t itemId = attachItem->itemId;
            ItemUid taken = 0;
            if (!_ledger.TryTake(backpackId, attachSlot, taken) || taken != request.itemUid) {
                if (taken != 0) {
                    std::uint16_t restored = 0;
                    _ledger.TryPut(backpackId, taken, attachSlot, restored);
                }
                result.code = static_cast<std::uint8_t>(ErrorCode::Conflict);
            } else {
                _ledger.DestroyItem(taken);
                const ItemInstance* current = _ledger.GetItem(request.parentUid);
                std::vector<ItemAttachment> mods =
                    current != nullptr ? current->mods : std::vector<ItemAttachment>{};
                mods.push_back(ItemAttachment{request.slotKey, itemId});
                if (!_ledger.SetItemMods(request.parentUid, std::move(mods))) {
                    result.code = static_cast<std::uint8_t>(ErrorCode::Conflict);
                } else {
                    result.code = 0;
                    result.itemId = itemId;
                }
            }
        }

        wire::Writer writer;
        result.Write(writer);
        Emit(Op::SItemModAttachResult, pending.playerId, writer.Bytes());

        if (result.code == 0) {
            Log::Info("mod attach: player=" + std::to_string(pending.playerId) +
                      " parent=" + std::to_string(request.parentUid) +
                      " slot=" + request.slotKey +
                      " item=" + std::to_string(result.itemId) +
                      " fromUid=" + std::to_string(request.itemUid));
            EmitInventorySnapshot(pending.playerId,
                                  static_cast<std::uint8_t>(msg::InventoryKind::PlayerBackpack),
                                  pending.playerId, backpackId, 0);
            SendItemDetails(0, equipmentId);
            if (backpackId != equipmentId)
                SendItemDetails(0, backpackId);
        }
    }
}

std::uint32_t World::MaxStackForItem(std::uint32_t itemId) const
{
    const ItemDef* def = _registry != nullptr ? _registry->Find(itemId) : nullptr;
    return def != nullptr && def->stack > 0 ? def->stack : 1;
}

void World::ApplyItemArranges()
{
    auto requests = std::move(_pendingArranges);
    _pendingArranges.clear();

    for (const auto& pending : requests) {
        const auto& arrange = pending.request;
        msg::ItemArrangeResult result;
        result.correlationId = arrange.correlationId;

        std::uint32_t containerId = 0;
        const bool resolved =
            ResolveInventory(pending.playerId, arrange.kind, arrange.ownerId, containerId);
        ContainerAccess access = ContainerAccess::Ok;
        if (resolved &&
            arrange.kind == static_cast<std::uint8_t>(msg::InventoryKind::MapContainer))
            access = CheckMapContainerAccess(pending.playerId, containerId);

        if (!resolved) {
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else if (arrange.kind ==
                   static_cast<std::uint8_t>(msg::InventoryKind::PlayerEquipment)) {
            // Equipment slots are fixed by slot.Key; sorting/reordering them is meaningless.
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else if (arrange.kind ==
                   static_cast<std::uint8_t>(msg::InventoryKind::CorpseContainer)) {
            // D3: corpse containers are take-only and have no sort/put UI.
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else if (access != ContainerAccess::Ok) {
            result.code = MapContainerErrorCode(access);
        } else {
            std::vector<ItemLedger::ArrangeEntry> layout;
            layout.reserve(arrange.entries.size());
            for (const auto& entry : arrange.entries)
                layout.push_back(
                    ItemLedger::ArrangeEntry{entry.slot, entry.itemUid, entry.itemId, entry.count});

            const auto maxStack = [this](std::uint32_t itemId) {
                return MaxStackForItem(itemId);
            };
            const auto error =
                _ledger.Arrange(containerId, arrange.expectedRevision, layout, maxStack);
            if (error == ItemLedger::ArrangeError::Ok) {
                result.code = 0;
                result.revision = _ledger.Revision(containerId);
            } else if (error == ItemLedger::ArrangeError::RevisionMismatch) {
                result.code = static_cast<std::uint8_t>(ErrorCode::Conflict);
            } else {
                result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
            }
        }

        wire::Writer writer;
        result.Write(writer);
        Emit(Op::SItemArrange, pending.playerId, writer.Bytes());

        if (result.code == 0) {
            if (arrange.kind == static_cast<std::uint8_t>(msg::InventoryKind::MapContainer))
                EmitInventorySnapshot(0, arrange.kind, arrange.ownerId, containerId, 0);
            else
                EmitInventorySnapshot(pending.playerId, arrange.kind, arrange.ownerId, containerId,
                                      0);
        }
    }
}

void World::ApplyItemConsumes()
{
    auto requests = std::move(_pendingConsumes);
    _pendingConsumes.clear();

    for (const auto& pending : requests) {
        const auto& consume = pending.request;
        msg::ItemConsumeResult result;
        result.correlationId = consume.correlationId;

        std::uint32_t containerId = 0;
        const bool resolved =
            ResolveInventory(pending.playerId, consume.kind, consume.ownerId, containerId);
        if (!resolved ||
            consume.kind == static_cast<std::uint8_t>(msg::InventoryKind::MapContainer) ||
            consume.kind == static_cast<std::uint8_t>(msg::InventoryKind::CorpseContainer)) {
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else {
            const auto outcome = _ledger.Consume(
                consume.itemUid, containerId, consume.slot, consume.itemId,
                consume.expectedRevision, consume.count);
            if (outcome.Ok()) {
                result.code = 0;
                result.itemUid = consume.itemUid;
                result.remaining = outcome.remaining;
                result.revision = outcome.sourceRevision;
            } else {
                result.code = TransferErrorCode(outcome.error);
            }
        }

        // Contract 2.16.0: the content table owns the heal per item; the client value is only
        // a fallback for a healing item the export does not cover yet (136 Injection has no
        // usage entry at all, so it never reaches this path).
        std::uint32_t healAmount = consume.healAmount;
        if (_buffModel != nullptr && _buffModel->Loaded()) {
            if (const auto* usage = _buffModel->FindItem(consume.itemId);
                usage != nullptr && usage->drugHeal > 0.0) {
                const auto contentHeal =
                    static_cast<std::uint32_t>(std::lround(usage->drugHeal));
                if (healAmount != 0 && healAmount != contentHeal) {
                    Log::Warn("consume heal mismatch: player=" + std::to_string(pending.playerId) +
                              " item=" + std::to_string(consume.itemId) + " client=" +
                              std::to_string(healAmount) + " content=" +
                              std::to_string(contentHeal) + " (content wins)");
                }
                healAmount = contentHeal;
            }
        }

        wire::Writer writer;
        result.Write(writer);
        Emit(Op::SItemConsume, pending.playerId, writer.Bytes());

        if (result.code == 0) {
            EmitInventorySnapshot(pending.playerId, consume.kind, consume.ownerId, containerId, 0);
            // Contract 2.16.0: the native heal was never applied on the server. Apply it after
            // the stack settles so clients see the item count change before the HP event.
            ApplyConsumeHeal(pending.playerId, healAmount);
        }
    }
}

void World::ApplyConsumeHeal(std::uint16_t playerId, std::uint32_t healAmount)
{
    if (healAmount == 0 || healAmount > kMaxHealPerUse)
        return; // not a healing item, or a bogus/forged amount

    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity)
        return;

    auto* health = _ecs.Get<Health>(entity);
    if (health == nullptr || !health->alive)
        return;

    const std::int32_t before = health->hp;
    const std::int32_t healed =
        std::min(health->maxHp, health->hp + static_cast<std::int32_t>(healAmount));
    if (healed == before)
        return; // already at max HP: nothing to broadcast

    health->hp = healed;

    msg::CombatEventV4 event;
    event.eventId = _nextEventId++;
    event.type = 2; // healed
    event.sourceKind = 0;
    event.sourceId = playerId;
    event.reporterPlayerId = playerId;
    event.targetKind = 0;
    event.targetId = playerId;
    event.damage = 0;
    event.hpAfter = health->hp;
    event.maxHp = health->maxHp;

    wire::Writer writer;
    event.Write(writer);
    Emit(Op::SCombatEvent, 0, writer.Bytes());

    Log::Info("consume heal: player=" + std::to_string(playerId) + " amount=" +
              std::to_string(healAmount) + " hp=" + std::to_string(before) + "->" +
              std::to_string(health->hp) + "/" + std::to_string(health->maxHp));
}

void World::ApplyItemReloads()
{
    auto requests = std::move(_pendingItemReloads);
    _pendingItemReloads.clear();

    for (const auto& pending : requests) {
        const auto& reload = pending.request;
        msg::ItemReloadResult result;
        result.correlationId = reload.correlationId;
        result.gunUid = reload.gunUid;
        result.ammoItemId = reload.ammoItemId;

        std::uint32_t gunSlot = 0;
        bool gunEquipped = false;
        std::uint32_t previousAmmoId = 0;
        std::uint32_t previousLoaded = 0;
        const std::uint32_t equipment = EquipmentId(pending.playerId);
        if (const auto* slots = _ledger.Slots(equipment)) {
            for (std::uint16_t slot = 0; slot < slots->size(); ++slot) {
                if ((*slots)[slot] == reload.gunUid) {
                    gunEquipped = true;
                    gunSlot = slot;
                    break;
                }
            }
        }
        if (const auto* gun = _ledger.GetItem(reload.gunUid)) {
            previousAmmoId = gun->loadedAmmoItemId;
            previousLoaded = gun->loadedAmmoCount;
        }
        const bool replacedMagazine = previousLoaded > 0 && previousAmmoId != 0 &&
                                      previousAmmoId != reload.ammoItemId;

        if (!IsInRaid(pending.playerId) || !gunEquipped ||
            reload.count == 0 || reload.loadedAfter < reload.count) {
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else {
            const std::uint32_t backpack = InventoryId(pending.playerId);
            // Contract 2.16.0: a reload of another ammo type replaces the magazine; the
            // rounds that come out of it need a stack size for the backpack merge.
            std::uint32_t loadedStack = 1;
            if (const auto* gun = _ledger.GetItem(reload.gunUid);
                gun != nullptr && gun->loadedAmmoItemId != 0)
                loadedStack = MaxStackForItem(gun->loadedAmmoItemId);
            const auto outcome = _ledger.ReloadAmmo(
                backpack, reload.gunUid, reload.ammoItemId, reload.count, reload.loadedAfter,
                loadedStack);
            if (outcome.Ok()) {
                result.code = 0;
                result.loadedCount = outcome.loadedCount;
                result.backpackRevision = outcome.backpackRevision;
            } else {
                result.code = TransferErrorCode(outcome.error);
            }
        }

        wire::Writer writer;
        result.Write(writer);
        Log::Info("item reload: player=" + std::to_string(pending.playerId) +
                  " gun=" + std::to_string(reload.gunUid) +
                  " ammo=" + std::to_string(reload.ammoItemId) +
                  " count=" + std::to_string(reload.count) + " code=" +
                  std::to_string(result.code) + " loaded=" +
                  std::to_string(result.loadedCount) +
                  (replacedMagazine ? " switchedFrom=" + std::to_string(previousAmmoId) +
                                          " returned=" + std::to_string(previousLoaded)
                                    : ""));
        Emit(Op::SItemReloadResult, pending.playerId, writer.Bytes());

        if (result.code == 0) {
            const std::uint32_t backpack = InventoryId(pending.playerId);
            EmitInventorySnapshot(pending.playerId,
                                  static_cast<std::uint8_t>(msg::InventoryKind::PlayerBackpack),
                                  pending.playerId, backpack, 0);
            SendItemDetails(0, equipment);
        }
        (void)gunSlot;
    }
}

void World::ApplyItemWeaponFires()
{
    auto requests = std::move(_pendingItemWeaponFires);
    _pendingItemWeaponFires.clear();

    for (const auto& pending : requests) {
        const auto& fire = pending.request;
        if (!IsInRaid(pending.playerId) || fire.gunUid == 0 || fire.ammoItemId == 0)
            continue;

        const std::uint32_t equipment = EquipmentId(pending.playerId);
        const auto* slots = _ledger.Slots(equipment);
        if (slots == nullptr)
            continue;

        bool gunEquipped = false;
        for (const auto uid : *slots) {
            if (uid == fire.gunUid) {
                gunEquipped = true;
                break;
            }
        }
        if (!gunEquipped)
            continue;

        const auto* gun = _ledger.GetItem(fire.gunUid);
        if (gun == nullptr || gun->loadedAmmoItemId != fire.ammoItemId ||
            fire.loadedCount > gun->loadedAmmoCount)
            continue; // stale/forged shot: never increase the magazine

        if (!_ledger.SetItemDetail(fire.gunUid, gun->durability, gun->maxDurability,
                                   fire.ammoItemId, fire.loadedCount))
            continue;
        SendItemDetails(0, equipment);
    }
}

void World::ApplyItemDurabilities()
{
    auto requests = std::move(_pendingItemDurabilities);
    _pendingItemDurabilities.clear();

    for (const auto& pending : requests) {
        const auto& request = pending.request;
        if (!IsInRaid(pending.playerId) || request.itemUid == 0 ||
            request.maxDurability == 0 || request.durability > request.maxDurability)
            continue;

        std::uint32_t containerId = 0;
        std::uint16_t slot = 0;
        if (!_ledger.Locate(request.itemUid, containerId, slot))
            continue;
        const std::uint32_t equipment = EquipmentId(pending.playerId);
        const std::uint32_t backpack = InventoryId(pending.playerId);
        const std::uint32_t petBag = PetBagId(pending.playerId);
        if (containerId != equipment && containerId != backpack && containerId != petBag)
            continue;

        const auto* item = _ledger.GetItem(request.itemUid);
        if (item == nullptr)
            continue;
        if (!_ledger.SetItemDetail(request.itemUid, request.durability,
                                   request.maxDurability, item->loadedAmmoItemId,
                                   item->loadedAmmoCount))
            continue;
        SendItemDetails(0, containerId);
        (void)slot;
    }
}

void World::ApplyBackpackCapacities()
{
    auto requests = std::move(_pendingBackpackCapacities);
    _pendingBackpackCapacities.clear();

    for (const auto& pending : requests) {
        if (!IsInRaid(pending.playerId))
            continue;
        const std::uint32_t capacity = pending.request.capacity;
        if (capacity == 0 || capacity > 64)
            continue;
        const std::uint32_t backpack = InventoryId(pending.playerId);
        if (!_ledger.ResizeContainer(backpack, static_cast<std::uint16_t>(capacity))) {
            Log::Warn("backpack capacity rejected: player=" + std::to_string(pending.playerId) +
                      " requested=" + std::to_string(capacity));
            continue;
        }
        Log::Info("backpack capacity: player=" + std::to_string(pending.playerId) +
                  " capacity=" + std::to_string(capacity));
        EmitInventorySnapshot(pending.playerId,
                              static_cast<std::uint8_t>(msg::InventoryKind::PlayerBackpack),
                              pending.playerId, backpack, 0);
    }
}

void World::ApplyInventorySyncs()
{
    auto requests = std::move(_pendingInventorySyncs);
    _pendingInventorySyncs.clear();

    for (const auto& pending : requests) {
        std::uint32_t containerId = 0;
        const bool isMapContainer =
            pending.request.kind == static_cast<std::uint8_t>(msg::InventoryKind::MapContainer);
        const bool isCorpseContainer =
            pending.request.kind == static_cast<std::uint8_t>(msg::InventoryKind::CorpseContainer);
        const bool accessible =
            (!isMapContainer || CanUseMapContainer(pending.playerId, pending.request.ownerId)) &&
            (!isCorpseContainer ||
             CanUseCorpseContainer(pending.playerId, pending.request.ownerId));
        if (!ResolveInventory(pending.playerId, pending.request.kind, pending.request.ownerId,
                              containerId) ||
            !accessible) {
            // v9 SInventorySnapshot has no error code: answer with an empty zero snapshot.
            msg::InventoryState state;
            state.correlationId = pending.request.correlationId;
            state.snapshot.kind = pending.request.kind;
            state.snapshot.ownerId = pending.request.ownerId;
            wire::Writer writer;
            state.Write(writer);
            Emit(Op::SInventorySnapshot, pending.playerId, writer.Bytes());
            continue;
        }
        EmitInventorySnapshot(pending.playerId, pending.request.kind, pending.request.ownerId,
                              containerId, pending.request.correlationId);
    }
}

// ---------------------------------------------------------------------------
// v9 item contract: map containers + world items (T3B)
// ---------------------------------------------------------------------------

bool World::IsMapContainerId(std::uint32_t containerId) const
{
    return containerId == kWorldLootContainer || containerId >= kMapContainerBase;
}

bool World::SameSceneGroup(const std::string& playerScene,
                           const std::string& containerScene) const
{
    if (playerScene.empty() || containerScene.empty())
        return false;
    if (playerScene == containerScene)
        return true;
    if (_content == nullptr)
        return false;

    // T3.5b: scenes.xml aliases several Level_* scenes to one mapId (GroundZero Main/_1).
    // Containers live in the sub-scene while the player reports the primary scene, so the
    // guard compares the map group instead of the raw scene string.
    const SceneDef* playerDef = _content->FindScene(playerScene);
    const SceneDef* containerDef = _content->FindScene(containerScene);
    if (playerDef == nullptr || containerDef == nullptr)
        return false;
    return playerDef->mapId == containerDef->mapId;
}

World::ContainerAccess World::CheckMapContainerAccess(std::uint16_t playerId,
                                                      std::uint32_t containerId) const
{
    // 1. Only an active raid participant may touch map containers.
    if (!IsInRaid(playerId))
        return ContainerAccess::NotInRaid;

    // 2. The legacy fallback box has no authored spawn: raid-only, no scene/range checks.
    if (containerId == kWorldLootContainer)
        return ContainerAccess::Ok;

    // 3. The id must be an authored map container of the current raid.
    const auto it = _mapContainers.find(containerId);
    if (it == _mapContainers.end())
        return ContainerAccess::Unknown;

    const Entity entity = FindPlayer(playerId);
    const auto* position = entity != kInvalidEntity ? _ecs.Get<Position>(entity) : nullptr;
    if (position == nullptr)
        return ContainerAccess::NotInRaid;

    // 4. Same map/scene group + within 5 m (2D, centimeters).
    if (!SameSceneGroup(PlayerScene(playerId), it->second.spawn.scene))
        return ContainerAccess::OutOfRange;
    const std::int64_t dx = static_cast<std::int64_t>(position->x) - it->second.spawn.x;
    const std::int64_t dz = static_cast<std::int64_t>(position->z) - it->second.spawn.z;
    if (dx * dx + dz * dz > 500LL * 500LL)
        return ContainerAccess::OutOfRange;
    return ContainerAccess::Ok;
}

std::uint8_t World::MapContainerErrorCode(ContainerAccess access) const
{
    switch (access) {
    case ContainerAccess::Ok:
        return 0;
    case ContainerAccess::NotInRaid:
        return static_cast<std::uint8_t>(ErrorCode::NotInRaid);
    case ContainerAccess::OutOfRange:
        return static_cast<std::uint8_t>(ErrorCode::Conflict);
    case ContainerAccess::Unknown:
        return static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
    }
    return static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
}

bool World::CanUseMapContainer(std::uint16_t playerId, std::uint32_t containerId) const
{
    return CheckMapContainerAccess(playerId, containerId) == ContainerAccess::Ok;
}

World::ContainerAccess World::CheckCorpseAccess(std::uint16_t playerId,
                                                std::uint32_t containerId) const
{
    if (!IsInRaid(playerId))
        return ContainerAccess::NotInRaid;

    const auto it = _corpses.find(containerId);
    if (it == _corpses.end())
        return ContainerAccess::Unknown;

    const Entity entity = FindPlayer(playerId);
    const auto* position = entity != kInvalidEntity ? _ecs.Get<Position>(entity) : nullptr;
    if (position == nullptr)
        return ContainerAccess::NotInRaid;
    if (!SameSceneGroup(PlayerScene(playerId), it->second.scene))
        return ContainerAccess::OutOfRange;

    const std::int64_t dx = static_cast<std::int64_t>(position->x) - it->second.x;
    const std::int64_t dz = static_cast<std::int64_t>(position->z) - it->second.z;
    if (dx * dx + dz * dz > 500LL * 500LL)
        return ContainerAccess::OutOfRange;
    return ContainerAccess::Ok;
}

bool World::CanUseCorpseContainer(std::uint16_t playerId, std::uint32_t containerId) const
{
    return CheckCorpseAccess(playerId, containerId) == ContainerAccess::Ok;
}

std::vector<std::uint32_t> World::CorpseContainerIds() const
{
    std::vector<std::uint32_t> ids;
    ids.reserve(_corpses.size());
    for (const auto& [containerId, record] : _corpses) {
        (void)record;
        ids.push_back(containerId);
    }
    std::sort(ids.begin(), ids.end());
    return ids;
}

std::uint32_t World::CreatePlayerCorpse(std::uint16_t playerId, const std::string& scene,
                                        std::int32_t x, std::int32_t y, std::int32_t z,
                                        std::uint16_t yaw)
{
    const std::uint16_t corpseSequence = _corpseSequence[playerId]++;
    const std::uint32_t corpseId = CorpseContainerId(playerId, corpseSequence);
    const std::size_t itemCount = OccupiedCount(EquipmentId(playerId)) +
                                  OccupiedCount(InventoryId(playerId));
    const std::uint16_t capacity = std::max<std::uint16_t>(
        kCorpseMinCapacity,
        static_cast<std::uint16_t>(std::min<std::size_t>(itemCount, 0xFFFF)));

    if (_ledger.Capacity(corpseId) != 0) {
        _ledger.ClearContainer(corpseId);
    } else if (!_ledger.CreateContainer(corpseId, capacity)) {
        Log::Warn("corpse: failed to create container for player " + std::to_string(playerId));
        return 0;
    }

    std::uint16_t prefer = 0;
    auto moveContainer = [&](std::uint32_t sourceContainer, std::uint16_t sourceCapacity) {
        const auto* slots = _ledger.Slots(sourceContainer);
        if (slots == nullptr)
            return;
        const std::uint16_t limit = static_cast<std::uint16_t>(
            std::min<std::size_t>(sourceCapacity, slots->size()));
        for (std::uint16_t slot = 0; slot < limit; ++slot) {
            if ((*slots)[slot] == ItemLedger::kNoItem)
                continue;
            ItemUid uid = ItemLedger::kNoItem;
            if (!_ledger.TryTake(sourceContainer, slot, uid))
                continue;
            std::uint16_t placed = 0;
            if (!_ledger.TryPut(corpseId, uid, prefer, placed)) {
                std::uint16_t restored = 0;
                _ledger.TryPut(sourceContainer, uid, slot, restored);
                Log::Warn("corpse: item move failed player=" + std::to_string(playerId) +
                          " uid=" + std::to_string(uid));
                continue;
            }
            prefer = static_cast<std::uint16_t>(placed + 1);
        }
    };

    moveContainer(EquipmentId(playerId), kEquipmentCapacity);
    moveContainer(InventoryId(playerId), kBackpackCapacity);

    CorpseRecord record;
    record.containerId = corpseId;
    record.playerId = playerId;
    record.scene = scene;
    record.x = x;
    record.y = y;
    record.z = z;
    record.yaw = yaw;
    _corpses[corpseId] = std::move(record);
    BroadcastCorpseSpawn(_corpses[corpseId]);

    Log::Info("player corpse created: player=" + std::to_string(playerId) +
              " id=" + std::to_string(corpseId) + " seq=" + std::to_string(corpseSequence) +
              " items=" + std::to_string(itemCount) +
              " capacity=" + std::to_string(capacity) + " scene=" + scene);
    return corpseId;
}

std::uint32_t World::CreateAiCorpse(Entity ai, std::uint16_t killerPlayerId)
{
    const auto* tag = _ecs.Get<AiTag>(ai);
    const auto* position = _ecs.Get<Position>(ai);
    if (tag == nullptr || position == nullptr)
        return 0;

    const LootTable* table = nullptr;
    std::string tableId = "none";
    if (_aiLootTables != nullptr && _lootTables != nullptr) {
        const auto mapping = _aiLootTables->find(tag->monsterId);
        const auto fallback = _aiLootTables->find(0);
        const std::string requested =
            mapping != _aiLootTables->end()
                ? mapping->second
                : (fallback != _aiLootTables->end() ? fallback->second : std::string());
        if (!requested.empty()) {
            const auto configured = _lootTables->find(requested);
            if (configured != _lootTables->end()) {
                table = &configured->second;
                tableId = configured->first;
            }
        }
    }

    std::vector<RolledItem> rolled;
    if (table != nullptr)
        rolled = RollLoot(*table, _rng, _registry);
    // v2: the loot table owns the corpse slot count (ai_default 6 / elite 8 / boss 10).
    const std::uint16_t baseCapacity =
        table != nullptr ? std::max<std::uint16_t>(1, table->capacity) : kCorpseMinCapacity;
    const std::uint16_t capacity = std::max<std::uint16_t>(
        baseCapacity, static_cast<std::uint16_t>(std::min<std::size_t>(rolled.size(), 0xFFFF)));

    const std::uint32_t corpseId = _nextAiCorpseId++;
    if (!_ledger.CreateContainer(corpseId, capacity)) {
        Log::Warn("ai corpse: failed to create container " + std::to_string(corpseId));
        return 0;
    }

    std::uint16_t slot = 0;
    std::size_t placedItems = 0;
    for (const auto& item : rolled) {
        const auto uid = _ledger.CreateItem(item.itemId, item.count);
        std::uint16_t placed = 0;
        if (!_ledger.TryPut(corpseId, uid, slot, placed)) {
            _ledger.DestroyItem(uid);
            Log::Warn("ai corpse: item did not fit id=" + std::to_string(corpseId) +
                      " item=" + std::to_string(item.itemId));
            continue;
        }
        slot = static_cast<std::uint16_t>(placed + 1);
        ++placedItems;
    }

    std::string scene = PlayerScene(killerPlayerId);
    if (scene.empty() && tag->ownerPlayerId != 0)
        scene = PlayerScene(tag->ownerPlayerId);

    CorpseRecord record;
    record.containerId = corpseId;
    record.playerId = 0;
    record.aiId = tag->aiId;
    record.monsterId = tag->monsterId;
    record.isAi = true;
    record.scene = scene;
    record.x = position->x;
    record.y = position->y;
    record.z = position->z;
    record.yaw = position->yaw;
    _corpses[corpseId] = record;
    BroadcastCorpseSpawn(_corpses[corpseId]);

    Log::Info("ai corpse created: ai=" + std::to_string(tag->aiId) + " monster=" +
              std::to_string(tag->monsterId) + " id=" + std::to_string(corpseId) +
              " capacity=" + std::to_string(capacity) + " table=" + tableId +
              " items=" + std::to_string(placedItems));
    return corpseId;
}

bool World::MapContainerSpawn(std::uint32_t containerId, std::int32_t& x, std::int32_t& y,
                              std::int32_t& z, std::string& scene) const
{
    const auto it = _mapContainers.find(containerId);
    if (it == _mapContainers.end())
        return false;
    x = it->second.spawn.x;
    y = it->second.spawn.y;
    z = it->second.spawn.z;
    scene = it->second.spawn.scene;
    return true;
}

std::size_t World::OccupiedCount(std::uint32_t containerId) const
{
    const auto* slots = _ledger.Slots(containerId);
    if (slots == nullptr)
        return 0;
    std::size_t count = 0;
    for (const ItemUid uid : *slots) {
        if (uid != ItemLedger::kNoItem)
            ++count;
    }
    return count;
}

msg::WorldItemInfo World::ToWorldItemInfo(const WorldItemRecord& record) const
{
    msg::WorldItemInfo info;
    info.worldItemId = record.worldItemId;
    info.itemUid = record.itemUid;
    info.itemId = record.itemId;
    info.count = record.count;
    info.scene = record.scene;
    info.x = record.x;
    info.y = record.y;
    info.z = record.z;
    info.yaw = record.yaw;
    info.source = record.source;
    return info;
}

void World::BroadcastWorldItemSpawn(const WorldItemRecord& record)
{
    msg::WorldItemSpawn spawn;
    spawn.info = ToWorldItemInfo(record);

    wire::Writer writer;
    spawn.Write(writer);
    Emit(Op::SWorldItemSpawn, 0, writer.Bytes());
}

void World::BroadcastWorldItemDespawn(std::uint32_t worldItemId)
{
    msg::WorldItemDespawn despawn;
    despawn.worldItemId = worldItemId;

    wire::Writer writer;
    despawn.Write(writer);
    Emit(Op::SWorldItemDespawn, 0, writer.Bytes());
}

void World::SendCorpseSpawn(std::uint16_t playerId, const CorpseRecord& record)
{
    msg::CorpseSpawn spawn;
    spawn.containerId = record.containerId;
    spawn.playerId = record.playerId;
    spawn.scene = record.scene;
    spawn.x = record.x;
    spawn.y = record.y;
    spawn.z = record.z;
    spawn.yaw = record.yaw;

    wire::Writer writer;
    spawn.Write(writer);
    Emit(Op::SCorpseSpawn, playerId, writer.Bytes());
}

void World::BroadcastCorpseSpawn(const CorpseRecord& record)
{
    SendCorpseSpawn(0, record);
}

void World::BroadcastCorpseDespawn(std::uint32_t containerId)
{
    msg::CorpseDespawn despawn;
    despawn.containerId = containerId;

    wire::Writer writer;
    despawn.Write(writer);
    Emit(Op::SCorpseDespawn, 0, writer.Bytes());
}

void World::SendCorpseSnapshot(std::uint16_t playerId)
{
    for (const auto& [containerId, record] : _corpses) {
        (void)containerId;
        SendCorpseSpawn(playerId, record);
    }
}

std::uint32_t World::SpawnWorldItem(std::uint32_t itemId, std::uint32_t count,
                                    const std::string& scene, std::int32_t x, std::int32_t y,
                                    std::int32_t z, std::uint16_t yaw, std::uint8_t source)
{
    if (itemId == 0 || count == 0)
        return 0;

    WorldItemRecord record;
    record.worldItemId = _nextWorldItemId++;
    record.itemUid = _ledger.CreateItem(itemId, count);
    record.itemId = itemId;
    record.count = count;
    record.scene = scene;
    record.x = x;
    record.y = y;
    record.z = z;
    record.yaw = yaw;
    record.source = source;

    _worldItems.emplace(record.worldItemId, record);
    BroadcastWorldItemSpawn(record);
    return record.worldItemId;
}

bool World::FindWorldItem(std::uint32_t worldItemId, msg::WorldItemInfo& out) const
{
    const auto it = _worldItems.find(worldItemId);
    if (it == _worldItems.end())
        return false;
    out = ToWorldItemInfo(it->second);
    return true;
}

std::vector<std::uint32_t> World::MapContainerIds() const
{
    std::vector<std::uint32_t> ids;
    ids.reserve(_mapContainers.size());
    for (const auto& [containerId, instance] : _mapContainers) {
        (void)instance;
        ids.push_back(containerId);
    }
    std::sort(ids.begin(), ids.end());
    return ids;
}

void World::SendWorldItemSnapshot(std::uint16_t playerId)
{
    std::vector<msg::WorldItemInfo> infos;
    infos.reserve(_worldItems.size());
    for (const auto& [worldItemId, record] : _worldItems)
        infos.push_back(ToWorldItemInfo(record));

    const std::uint32_t snapshotId = _nextWorldSnapshotId++;
    const std::size_t chunkSize = static_cast<std::size_t>(msg::kMaxWorldItemChunkEntries);
    const std::size_t chunkCount =
        infos.empty() ? 0 : (infos.size() + chunkSize - 1) / chunkSize;

    if (infos.empty()) {
        // Empty world: one explicit empty chunk (chunkCount = 0) so the client can settle
        // its pending snapshot instead of waiting for data that will never arrive.
        msg::WorldItemSnapshotChunk chunk;
        chunk.snapshotId = snapshotId;
        wire::Writer writer;
        chunk.Write(writer);
        Emit(Op::SWorldItemSnapshot, playerId, writer.Bytes());
    } else {
        for (std::size_t index = 0; index < chunkCount; ++index) {
            msg::WorldItemSnapshotChunk chunk;
            chunk.snapshotId = snapshotId;
            chunk.chunkIndex = static_cast<std::uint16_t>(index);
            chunk.chunkCount = static_cast<std::uint16_t>(chunkCount);
            const std::size_t first = index * chunkSize;
            const std::size_t last = std::min(first + chunkSize, infos.size());
            for (std::size_t i = first; i < last; ++i)
                chunk.items.push_back(infos[i]);

            wire::Writer writer;
            chunk.Write(writer);
            Emit(Op::SWorldItemSnapshot, playerId, writer.Bytes());
        }
    }

    Log::Info("world item snapshot: player " + std::to_string(playerId) + " items=" +
              std::to_string(infos.size()) + " snapshot=" + std::to_string(snapshotId) +
              " chunks=" + std::to_string(chunkCount));
}

void World::ApplyContainerOpens()
{
    auto requests = std::move(_pendingContainerOpens);
    _pendingContainerOpens.clear();

    for (const auto& pending : requests) {
        msg::ContainerContents contents;
        contents.correlationId = pending.request.correlationId;
        contents.containerId = pending.request.containerId;

        const std::uint32_t containerId = pending.request.containerId;
        const bool mapContainer =
            IsMapContainerId(containerId) && CanUseMapContainer(pending.playerId, containerId) &&
            _ledger.Capacity(containerId) != 0;
        const bool corpseContainer =
            _corpses.find(containerId) != _corpses.end() &&
            CanUseCorpseContainer(pending.playerId, containerId) &&
            _ledger.Capacity(containerId) != 0;
        if (mapContainer || corpseContainer) {
            if (mapContainer)
                EnsureMapContainerRolled(containerId); // roll once, cached per container
            contents.revision = _ledger.Revision(containerId);
            contents.capacity = _ledger.Capacity(containerId);
            if (const auto* slots = _ledger.Slots(containerId)) {
                for (std::uint16_t slot = 0; slot < slots->size(); ++slot) {
                    const ItemUid uid = (*slots)[slot];
                    if (uid == ItemLedger::kNoItem)
                        continue;
                    const ItemInstance* item = _ledger.GetItem(uid);
                    if (item == nullptr)
                        continue;
                    contents.entries.push_back(
                        msg::InventoryEntry{slot, uid, item->itemId, item->count});
                }
            }
        }

        Log::Info("container open: player=" + std::to_string(pending.playerId) +
                  " id=" + std::to_string(containerId) + " entries=" +
                  std::to_string(contents.entries.size()) + " capacity=" +
                  std::to_string(contents.capacity) + " revision=" +
                  std::to_string(contents.revision) + " accepted=" +
                  (mapContainer || corpseContainer ? "true" : "false"));

        wire::Writer writer;
        contents.Write(writer);
        Emit(Op::SContainerContents, pending.playerId, writer.Bytes());
        SendItemDetails(pending.playerId, containerId);
    }
}

void World::ApplyWorldItemPickups()
{
    auto requests = std::move(_pendingWorldPickups);
    _pendingWorldPickups.clear();

    for (const auto& pending : requests) {
        const auto& request = pending.request;
        msg::WorldItemPickupResult result;
        result.correlationId = request.correlationId;

        const auto itemIt = _worldItems.find(request.worldItemId);
        const Entity entity = FindPlayer(pending.playerId);
        const auto* position = entity != kInvalidEntity ? _ecs.Get<Position>(entity) : nullptr;
        const std::uint32_t backpack = InventoryId(pending.playerId);

        if (itemIt == _worldItems.end()) {
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else if (itemIt->second.itemUid != request.expectedItemUid ||
                   itemIt->second.itemId != request.expectedItemId ||
                   itemIt->second.count != request.count) {
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else if (!IsInRaid(pending.playerId)) {
            result.code = static_cast<std::uint8_t>(ErrorCode::NotInRaid);
        } else if (position == nullptr) {
            result.code = static_cast<std::uint8_t>(ErrorCode::NotInRaid);
        } else if (!itemIt->second.scene.empty() &&
                   !SameSceneGroup(PlayerScene(pending.playerId), itemIt->second.scene)) {
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else if (std::abs(position->x - itemIt->second.x) > kWorldItemPickupRangeCm ||
                   std::abs(position->z - itemIt->second.z) > kWorldItemPickupRangeCm) {
            result.code = static_cast<std::uint8_t>(ErrorCode::Conflict);
        } else if (_ledger.Revision(backpack) != request.expectedBackpackRevision) {
            result.code = static_cast<std::uint8_t>(ErrorCode::Conflict);
        } else {
            std::uint16_t placed = 0;
            if (!_ledger.TryPut(backpack, itemIt->second.itemUid, 0, placed)) {
                result.code = static_cast<std::uint8_t>(ErrorCode::Conflict); // backpack full
            } else {
                result.code = 0;
                result.itemUid = itemIt->second.itemUid;
                result.backpackRevision = _ledger.Revision(backpack);

                const std::uint32_t worldItemId = request.worldItemId;
                _worldItems.erase(itemIt);
                BroadcastWorldItemDespawn(worldItemId);
                EmitInventorySnapshot(pending.playerId,
                                      static_cast<std::uint8_t>(msg::InventoryKind::PlayerBackpack),
                                      pending.playerId, backpack, 0);
                SendItemDetails(pending.playerId, backpack);
            }
        }

        wire::Writer writer;
        result.Write(writer);
        Log::Info("world item pickup: player=" + std::to_string(pending.playerId) +
                  " id=" + std::to_string(request.worldItemId) + " uid=" +
                  std::to_string(request.expectedItemUid) + " code=" +
                  std::to_string(result.code) + " expectedRev=" +
                  std::to_string(request.expectedBackpackRevision) + " actualRev=" +
                  std::to_string(_ledger.Revision(backpack)) + " pos=" +
                  std::to_string(position != nullptr ? position->x : 0) + "," +
                  std::to_string(position != nullptr ? position->z : 0));
        Emit(Op::SWorldItemPickup, pending.playerId, writer.Bytes());
    }
}

void World::ApplyWorldItemDrops()
{
    auto requests = std::move(_pendingWorldDrops);
    _pendingWorldDrops.clear();

    for (const auto& pending : requests) {
        const auto& request = pending.request;
        msg::WorldItemDropResult result;
        result.correlationId = request.correlationId;

        std::uint32_t sourceContainer = 0;
        const bool resolved = ResolveInventory(pending.playerId, request.sourceKind,
                                               request.sourceId, sourceContainer);
        if (!resolved) {
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else if (request.sourceKind >
                   static_cast<std::uint8_t>(msg::InventoryKind::PlayerPetBag)) {
            // D4: containers/corpses may not be dropped directly to the ground; only the
            // player's three inventories are valid drop sources.
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else if (!IsInRaid(pending.playerId)) {
            result.code = static_cast<std::uint8_t>(ErrorCode::NotInRaid);
        } else if (request.sourceKind ==
                       static_cast<std::uint8_t>(msg::InventoryKind::MapContainer) &&
                   !CanUseMapContainer(pending.playerId, sourceContainer)) {
            result.code = MapContainerErrorCode(
                CheckMapContainerAccess(pending.playerId, sourceContainer));
        } else if (IsBackpackUnequipBlocked(pending.playerId, sourceContainer,
                                            request.sourceSlot)) {
            result.code = static_cast<std::uint8_t>(ErrorCode::Conflict);
        } else {
            const ItemInstance* item = _ledger.GetItem(request.itemUid);
            std::uint32_t locatedContainer = 0;
            std::uint16_t locatedSlot = 0;
            const bool located = _ledger.Locate(request.itemUid, locatedContainer, locatedSlot);

            if (item == nullptr || !located || locatedContainer != sourceContainer ||
                locatedSlot != request.sourceSlot) {
                result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
            } else if (item->count != request.count) {
                result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
            } else if (_ledger.Revision(sourceContainer) != request.expectedRevision) {
                result.code = static_cast<std::uint8_t>(ErrorCode::Conflict);
            } else {
                ItemUid removed = ItemLedger::kNoItem;
                if (!_ledger.TryTake(sourceContainer, locatedSlot, removed)) {
                    result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
                } else {
                    WorldItemRecord record;
                    record.worldItemId = _nextWorldItemId++;
                    record.itemUid = removed;
                    record.itemId = item->itemId;
                    record.count = item->count;
                    record.scene = request.scene;
                    record.x = request.x;
                    record.y = request.y;
                    record.z = request.z;
                    record.yaw = request.yaw;
                    record.source = static_cast<std::uint8_t>(msg::WorldItemSource::Dropped);
                    _worldItems.emplace(record.worldItemId, record);

                    result.code = 0;
                    result.worldItemId = record.worldItemId;
                    result.sourceRevision = _ledger.Revision(sourceContainer);

                    BroadcastWorldItemSpawn(record);
                    if (IsMapContainerId(sourceContainer)) {
                        EmitInventorySnapshot(0, request.sourceKind, request.sourceId,
                                              sourceContainer, 0);
                    } else {
                        EmitInventorySnapshot(pending.playerId, request.sourceKind,
                                              request.sourceId, sourceContainer, 0);
                    }
                }
            }
        }

        wire::Writer writer;
        result.Write(writer);
        Emit(Op::SWorldItemDrop, pending.playerId, writer.Bytes());
    }
}

void World::Emit(Op op, std::uint16_t playerId, const std::vector<std::uint8_t>& payload,
                 std::uint8_t channel)
{
    _outbound.push_back(Outbound{op, playerId, payload, channel});
}

void World::BroadcastPlayerEvent(std::uint16_t playerId, std::uint8_t type)
{
    msg::PlayerEvent event;
    event.eventId = _nextEventId++;
    event.playerId = playerId;
    event.type = type;

    wire::Writer writer;
    event.Write(writer);
    Emit(Op::SPlayerEvent, 0, writer.Bytes());
}

void World::Tick(std::int32_t tick)
{
    _currentTick = tick;

    // 1 + 2: drain commands and apply inputs
    ApplyInputs();
    // 2b: M2-v11 spawn AOI 鈥?activate/sleep AI by player distance (uses fresh positions)
    ApplyAiStreaming();
    // 3: AI sync (simulator reports transform only)
    ApplyAiSync();
    ApplyAiDirector();
    // 4: combat (D5 hit facts use the native formula; legacy CHitReport stays for old data)
    ApplyHitEvents();
    ApplyHits();
    ApplyCombat();
    TickBuffs();
    ApplyRespawns();
    // 5: interactions (loot transactions)
    ApplyInteractions();
    ApplyItemTransfers();
    ApplyItemArranges();
    ApplyItemConsumes();
    ApplyItemReloads();
    ApplyItemWeaponFires();
    ApplyItemDurabilities();
    ApplyItemModDetaches();
    ApplyItemModAttaches();
    ApplyBackpackCapacities();
    ApplyWorldItemDrops();
    ApplyWorldItemPickups();
    ApplyContainerOpens();
    ApplyInventorySyncs();
    ApplyContainerViews();
    // 6: raid phase transitions
    ApplyExtract();
    ApplyRaidPhase();
    // 7: events are already queued in _events/_outbound
    // 8: snapshot
    BuildSnapshot(tick);
}

std::vector<WorldEvent> World::TakeEvents()
{
    auto events = std::move(_events);
    _events.clear();
    return events;
}

std::vector<Outbound> World::TakeOutbound()
{
    auto outbound = std::move(_outbound);
    _outbound.clear();
    return outbound;
}

void World::ApplyInteractions()
{
    auto requests = std::move(_pendingLoot);
    _pendingLoot.clear();

    for (const auto& pending : requests) {
        msg::LootResult result;
        result.correlationId = pending.request.correlationId;

        const std::uint64_t cacheKey =
            (static_cast<std::uint64_t>(pending.playerId) << 32) |
            (pending.request.correlationId & 0xFFFFFFFFu);

        const auto cached = _lootCache.find(cacheKey);
        const std::uint32_t targetContainer = pending.request.containerId;
        const ContainerAccess access = IsMapContainerId(targetContainer)
                                           ? CheckMapContainerAccess(pending.playerId, targetContainer)
                                           : ContainerAccess::Ok;
        if (cached != _lootCache.end()) {
            result = cached->second;
            result.code = static_cast<std::uint8_t>(ErrorCode::IdempotentReplay);
        } else if (access != ContainerAccess::Ok) {
            // T3.5: the legacy v8 CLoot path cannot bypass the map-container guard.
            result.code = MapContainerErrorCode(access);
        } else if (pending.request.action == 0) {
            // take from container -> player inventory
            ItemUid uid = ItemLedger::kNoItem;
            if (!_ledger.TryTake(pending.request.containerId, pending.request.slot, uid)) {
                result.code = 4; // not found
            } else {
                const ItemInstance* item = _ledger.GetItem(uid);
                const std::uint32_t inventoryId = InventoryId(pending.playerId);
                std::uint16_t placed = 0;

                if (!_ledger.TryPut(inventoryId, uid, 0, placed)) {
                    std::uint16_t restored = 0;
                    _ledger.TryPut(pending.request.containerId, uid, pending.request.slot, restored);
                    result.code = 5; // no space
                } else {
                    result.code = 0;
                    result.itemUid = uid;
                    result.itemId = item != nullptr ? item->itemId : 0;
                    result.count = item != nullptr ? item->count : 0;
                    result.slot = placed;
                }
            }
        } else {
            // put from player inventory -> container
            const std::uint32_t inventoryId = InventoryId(pending.playerId);
            std::uint32_t containerId = 0;
            std::uint16_t slot = 0;

            if (!_ledger.Locate(pending.request.itemUid, containerId, slot) || containerId != inventoryId) {
                result.code = 4;
            } else {
                ItemUid removed = ItemLedger::kNoItem;
                _ledger.TryTake(inventoryId, slot, removed);

                std::uint16_t placed = 0;
                if (!_ledger.TryPut(pending.request.containerId, pending.request.itemUid, pending.request.slot, placed)) {
                    std::uint16_t back = 0;
                    _ledger.TryPut(inventoryId, pending.request.itemUid, slot, back);
                    result.code = 5;
                } else {
                    const ItemInstance* item = _ledger.GetItem(pending.request.itemUid);
                    result.code = 0;
                    result.itemUid = pending.request.itemUid;
                    result.itemId = item != nullptr ? item->itemId : 0;
                    result.count = item != nullptr ? item->count : 0;
                    result.slot = placed;
                }
            }
        }

        if (_lootCache.size() > 512)
            _lootCache.clear();
        _lootCache[cacheKey] = result;

        wire::Writer writer;
        result.Write(writer);
        Emit(Op::SLoot, pending.playerId, writer.Bytes());
    }
}

std::uint64_t World::IdentityOf(std::uint16_t playerId) const
{
    const auto& players = _ecs.All<PlayerComponent>();
    for (const auto& [entity, player] : players) {
        (void)entity;
        if (player.playerId == playerId)
            return player.identity;
    }
    return playerId;
}

void World::PersistStash(std::uint16_t playerId)
{
    SettleStash(playerId, true);
}

void World::SettleStash(std::uint16_t playerId, bool extracted)
{
    if (_storage == nullptr)
        return;

    const std::vector<StoredItem> items = extracted ? CollectInventory(playerId) : std::vector<StoredItem>{};
    if (!_storage->SettleRaid(IdentityOf(playerId), _raidEpoch, items, extracted)) {
        Log::Warn("storage: failed to settle raid for player " + std::to_string(playerId));
        return;
    }

    ++_stashVersion;
    if (extracted) {
        // T3B: the summary covers the three raid inventories only (equipment/backpack/petBag);
        // map containers and ground items never settle. Kept as a log line for the D6 report.
        const std::size_t equipmentItems = OccupiedCount(EquipmentId(playerId));
        const std::size_t backpackItems = OccupiedCount(InventoryId(playerId));
        const std::size_t petBagItems = OccupiedCount(PetBagId(playerId));
        Log::Info("extract summary: player " + std::to_string(playerId) + " equipment=" +
                  std::to_string(equipmentItems) + " backpack=" +
                  std::to_string(backpackItems) + " petBag=" + std::to_string(petBagItems) +
                  " total=" + std::to_string(items.size()) + " epoch=" +
                  std::to_string(_raidEpoch));
        Log::Info("extract saved: player " + std::to_string(playerId) + " items=" +
                  std::to_string(items.size()) + " epoch=" + std::to_string(_raidEpoch));
    } else {
        Log::Info("death settled: player " + std::to_string(playerId) + " epoch=" +
                  std::to_string(_raidEpoch));
    }
}

std::vector<StoredItem> World::CollectInventory(std::uint16_t playerId) const
{
    std::vector<StoredItem> items;
    const std::uint32_t containers[] = {EquipmentId(playerId), InventoryId(playerId),
                                        PetBagId(playerId)};
    for (const std::uint32_t containerId : containers) {
        const auto* slots = _ledger.Slots(containerId);
        if (slots == nullptr)
            continue;

        for (const auto uid : *slots) {
            if (uid == ItemLedger::kNoItem)
                continue;
            const auto* item = _ledger.GetItem(uid);
            if (item == nullptr)
                continue;
            items.push_back(StoredItem{item->itemId, item->count, item->durability,
                                       item->maxDurability, item->loadedAmmoItemId,
                                       item->loadedAmmoCount});
        }
    }

    return items;
}

void World::ApplyExtract()
{
    auto requests = std::move(_pendingExtract);
    _pendingExtract.clear();

    bool participantExtracted = false;

    for (const auto& pending : requests) {
        msg::ExtractResult result;
        result.correlationId = pending.request.correlationId;

        auto* progress = _ecs.Get<PlayerProgress>(pending.entity);
        auto* health = _ecs.Get<Health>(pending.entity);
        auto* player = _ecs.Get<PlayerComponent>(pending.entity);

        const bool inRaid = player != nullptr && !player->leftRaid && !player->scene.empty() &&
                            _raidParticipants.find(pending.playerId) != _raidParticipants.end();

        if (progress == nullptr || health == nullptr || player == nullptr) {
            result.code = 4;
        } else if (progress->extracted) {
            result.code = static_cast<std::uint8_t>(ErrorCode::IdempotentReplay);
        } else if (!health->alive) {
            result.code = 6; // dead players cannot extract
        } else if (!inRaid) {
            result.code = static_cast<std::uint8_t>(ErrorCode::NotInRaid);
        } else if (_phase != 1 && _phase != 2) {
            result.code = static_cast<std::uint8_t>(ErrorCode::NotInRaid);
        } else if (!IsPlayerAtExit(pending.playerId)) {
            result.code = static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        } else {
            progress->extracted = true;
            result.code = 0;
            participantExtracted = true;

            // extracted players leave the raid immediately: no scene, no inputs, no
            // further spawn reports (the next raid resets the flag)
            MarkPlayerLeft(pending.entity, false);
            ReassignAiOwnership(pending.playerId);
            BroadcastPlayerEvent(pending.playerId, 3);
            PersistStash(pending.playerId);
        }

        wire::Writer writer;
        result.Write(writer);
        Emit(Op::SExtract, pending.playerId, writer.Bytes());
    }

    if (!participantExtracted || _phase != 1)
        return;

    MaybeFinishRaid();
}

void World::ApplyRaidPhase()
{
    // BUG-008: only a raid that actually began (spawn anchor -> BeginRaid) can time out.
    // Before the anchor the server waits in phase 1 regardless of the tick counter, so a
    // fresh CStartRaid cannot inherit the previous raid's clock.
    if (_phase == 1 && _raidStarted && _currentTick - _raidStartTick >= kRaidDurationTicks) {
        _phase = 2;
        _settleTicks = 20;

        Log::Info("raid timeout, entering settling");

        msg::RaidPhaseMessage message;
        message.phase = _phase;
        message.tick = _lastSnapshot.tick;

        wire::Writer writer;
        message.Write(writer);
        Emit(Op::SRaidPhase, 0, writer.Bytes());
        return;
    }

    if (_phase != 2)
        return;

    if (--_settleTicks > 0)
        return;

    _phase = 3;
    msg::RaidPhaseMessage message;
    message.phase = _phase;
    message.tick = _lastSnapshot.tick;

    wire::Writer writer;
    message.Write(writer);
    Emit(Op::SRaidPhase, 0, writer.Bytes());
}

void World::DropAiLoot(Entity ai, std::uint16_t killerPlayerId)
{
    // T7: AI loot is a persistent corpse container, not a direct gift to the killer.
    CreateAiCorpse(ai, killerPlayerId);
}

bool World::GetPlayerPosition(std::uint16_t playerId, std::int32_t& x, std::int32_t& y, std::int32_t& z) const
{
    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity)
        return false;

    const auto* position = _ecs.Get<Position>(entity);
    if (position == nullptr)
        return false;

    x = position->x;
    y = position->y;
    z = position->z;
    return true;
}

void World::DespawnAiOwnedBy(std::uint16_t ownerPlayerId)
{
    std::vector<Entity> owned;
    for (const auto& [entity, tag] : _ecs.All<AiTag>()) {
        if (tag.ownerPlayerId == ownerPlayerId)
            owned.push_back(entity);
    }

    for (const Entity entity : owned) {
        const auto* tag = _ecs.Get<AiTag>(entity);
        const std::uint32_t aiId = tag != nullptr ? tag->aiId : 0;

        SendAiDespawn(aiId, static_cast<std::uint8_t>(msg::AiDespawnReason::OwnerLeft));
        _events.push_back(WorldEvent{EventType::AiDied, entity, ownerPlayerId, 0});
        _ecs.Destroy(entity);
        _aiByExternalId.erase(aiId);
        _aiLoadouts.erase(aiId);
        _aiLoadoutSnapshots.erase(aiId);
        _aiAnimSeq.erase(aiId);
        _aiAimSeq.erase(aiId);
        _aiStateSampleSeq.erase(aiId);
        _aiShotSeq.erase(aiId);
        _aiTargets.erase(aiId);
        _lastAiHit.erase(aiId);
        EmitCombat(1, 1, aiId, ownerPlayerId, 0);

        Log::Info("ai despawned with owner " + std::to_string(ownerPlayerId) +
                  " id=" + std::to_string(aiId));
    }
}

void World::ReassignAiOwnership(std::uint16_t oldOwnerPlayerId)
{
    if (oldOwnerPlayerId == 0)
        return;

    bool hasOwnedAi = false;
    for (const auto& [entity, tag] : _ecs.All<AiTag>()) {
        (void)entity;
        if (tag.ownerPlayerId == oldOwnerPlayerId) {
            hasOwnedAi = true;
            break;
        }
    }
    if (!hasOwnedAi)
        return;

    // The new simulator must still be an active participant with a loaded raid scene.
    // Dead-but-respawning players are skipped: assigning AI to them would freeze every
    // replica for the respawn window.
    std::uint16_t newOwnerPlayerId = 0;
    for (const auto& [entity, player] : _ecs.All<PlayerComponent>()) {
        if (player.playerId == oldOwnerPlayerId || player.leftRaid || player.scene.empty())
            continue;
        if (_raidParticipants.find(player.playerId) == _raidParticipants.end())
            continue;
        const auto* health = _ecs.Get<Health>(entity);
        if (health == nullptr || !health->alive)
            continue;
        if (newOwnerPlayerId == 0 || player.playerId < newOwnerPlayerId)
            newOwnerPlayerId = player.playerId;
    }

    if (newOwnerPlayerId == 0) {
        DespawnAiOwnedBy(oldOwnerPlayerId);
        Log::Info("ai owner left with no successor: owner=" + std::to_string(oldOwnerPlayerId));
        return;
    }

    struct ActiveMigration {
        std::uint32_t aiId = 0;
        std::uint32_t monsterId = 0;
        Position position;
        std::int32_t hp = 100;
        std::uint16_t activationRadius = 0;
        std::uint16_t deactivationRadius = 0;
    };
    std::vector<ActiveMigration> active;

    for (auto& [entity, tag] : _ecs.All<AiTag>()) {
        if (tag.ownerPlayerId != oldOwnerPlayerId)
            continue;
        tag.ownerPlayerId = newOwnerPlayerId;

        // The new owner restarts its local sequence counters and does not have the old
        // owner's v7 snapshot. Drop both so the first samples are accepted and the rebuilt
        // AI falls back to its monster preset (known trade-off: position/hp survive).
        _aiAnimSeq.erase(tag.aiId);
        _aiAimSeq.erase(tag.aiId);
        _aiStateSampleSeq.erase(tag.aiId);
        _aiShotSeq.erase(tag.aiId);
        _aiLoadouts.erase(tag.aiId);
        _aiLoadoutSnapshots.erase(tag.aiId);
        _aiTargets.erase(tag.aiId);
        _lastAiHit.erase(tag.aiId);

        if (tag.state != 1)
            continue;

        const auto* position = _ecs.Get<Position>(entity);
        const auto* health = _ecs.Get<Health>(entity);
        if (position == nullptr)
            continue;

        ActiveMigration migration;
        migration.aiId = tag.aiId;
        migration.monsterId = tag.monsterId;
        migration.position = *position;
        migration.hp = health != nullptr ? health->hp : 100;
        const auto activation = _aiActivationRadius.find(tag.aiId);
        const auto deactivation = _aiDeactivationRadius.find(tag.aiId);
        migration.activationRadius = activation != _aiActivationRadius.end() ? activation->second : 0;
        migration.deactivationRadius =
            deactivation != _aiDeactivationRadius.end() ? deactivation->second : 0;
        active.push_back(migration);
    }

    for (const auto& migration : active) {
        SendAiDespawn(migration.aiId, static_cast<std::uint8_t>(msg::AiDespawnReason::OwnerLeft));
        SendAiSpawn(migration.aiId, newOwnerPlayerId, migration.monsterId, migration.position,
                    migration.hp, 0, migration.activationRadius, migration.deactivationRadius);
    }

    Log::Info("ai owner migrated: old=" + std::to_string(oldOwnerPlayerId) +
              " new=" + std::to_string(newOwnerPlayerId) +
              " active=" + std::to_string(active.size()));
}

void World::EndRaid()
{
    std::vector<Entity> aiEntities;
    for (const auto& [entity, tag] : _ecs.All<AiTag>())
        aiEntities.push_back(entity);

    for (const Entity entity : aiEntities) {
        if (const auto* tag = _ecs.Get<AiTag>(entity))
            SendAiDespawn(tag->aiId, static_cast<std::uint8_t>(msg::AiDespawnReason::RaidEnd));
        _ecs.Destroy(entity);
    }
    _aiByExternalId.clear();
    _aiLoadouts.clear();
    _aiTargets.clear();
    _lastAiHit.clear();
    _aiAnimSeq.clear();
    _aiAimSeq.clear();
    _aiStateSampleSeq.clear();
    _aiShotSeq.clear();
    _aiLoadoutSnapshots.clear();
    _playerMirror.clear();
    _playerStateSeq.clear();
    _playerShotSeq.clear();
    _playerBuffs.clear();
    _consumableDedup.clear();
    _consumableNextTick.clear();
    _lastPlayerHurtTick.clear();

    // a fresh raid must not inherit anything from the previous one
    ++_raidEpoch;

    for (auto& [entity, player] : _ecs.All<PlayerComponent>()) {
        (void)entity;
        player.leftRaid = false;
        player.scene.clear();
        player.hasInput = false;
        player.teleportPending = false;

        if (auto* progress = _ecs.Get<PlayerProgress>(entity))
            progress->extracted = false;

        if (auto* health = _ecs.Get<Health>(entity)) {
            health->alive = true;
            health->maxHp = kPlayerMaxHp;
            health->hp = kPlayerMaxHp;
        }
    }

    _aiSpawned = false;
    _raidStarted = false;
    _nextAiInstanceId = 1;

    // BUG-008: the raid clock belongs to the raid that just ended. Restart it here so a
    // new CStartRaid never inherits the old tick; BeginRaid() starts the real budget.
    _raidStartTick = _currentTick;

    _raidParticipants.clear();
    _phase = 1;
    _settleTicks = 20;
    _lastHitTick.clear();
    _hitEventDedup.clear();
    _explosionHitTick.clear();
    _hitDropLogTick.clear();
    _lootCache.clear();
    _transferCache.clear();
    _ledger.ClearContainer(kWorldLootContainer);

    // Ground items belong to the raid that just ended; drop their ledger items as well.
    for (const auto& [worldItemId, record] : _worldItems) {
        (void)worldItemId;
        _ledger.DestroyItem(record.itemUid);
    }
    _worldItems.clear();

    // Corpse containers are raid-scoped: their items disappear with the raid.
    for (const auto& [corpseId, record] : _corpses) {
        (void)record;
        BroadcastCorpseDespawn(corpseId);
        _ledger.ClearContainer(corpseId);
    }
    for (const auto& [containerId, record] : _corpses) {
        (void)record;
        _ledger.DestroyContainer(containerId);
    }
    _corpses.clear();
    _corpseSequence.clear();
    _nextAiCorpseId = kAiCorpseContainerBase;
    _respawnAtTick.clear();

    // tell clients still showing the closed raid that a new raid is loading
    msg::RaidPhaseMessage message;
    message.phase = _phase;
    message.tick = _lastSnapshot.tick;

    wire::Writer writer;
    message.Write(writer);
    Emit(Op::SRaidPhase, 0, writer.Bytes());

    Log::Info("raid torn down: " + std::to_string(aiEntities.size()) + " ai removed, epoch=" +
              std::to_string(_raidEpoch));
}

void World::MarkPlayerLeft(Entity entity, bool dead)
{
    std::uint16_t playerId = 0;
    if (auto* player = _ecs.Get<PlayerComponent>(entity)) {
        playerId = player->playerId;
        player->scene.clear();
        player->hasInput = false;
        player->teleportPending = false;
        player->leftRaid = true;
        _respawnAtTick.erase(playerId);
    }

    if (auto* health = _ecs.Get<Health>(entity)) {
        if (dead) {
            health->alive = false;
            health->hp = 0;
        }
    }

    if (playerId != 0)
        _raidParticipants.erase(playerId);
}

void World::LeaveRaid(std::uint16_t playerId)
{
    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity)
        return;

    // already dead / extracted: keep the operation idempotent, but still re-check settle
    if (const auto* player = _ecs.Get<PlayerComponent>(entity); player != nullptr && player->leftRaid) {
        MaybeFinishRaid();
        return;
    }

    MarkPlayerLeft(entity, true);
    ReassignAiOwnership(playerId);
    SettleStash(playerId, false); // died / gave up: stash is lost
    BroadcastPlayerEvent(playerId, 2); // died / gave up
    EmitCombat(1, 0, playerId, playerId, 0);
    MaybeFinishRaid();
}

void World::HandlePlayerDeath(Entity entity, std::uint16_t playerId)
{
    auto* player = _ecs.Get<PlayerComponent>(entity);
    auto* health = _ecs.Get<Health>(entity);
    auto* position = _ecs.Get<Position>(entity);
    if (player == nullptr || health == nullptr || position == nullptr || playerId == 0)
        return;
    if (_respawnAtTick.find(playerId) != _respawnAtTick.end())
        return;

    health->alive = false;
    health->hp = 0;
    CreatePlayerCorpse(playerId, player->scene, position->x, position->y, position->z,
                       position->yaw);
    // D9/B: keep the player in the raid but dead until the respawn tick. Equipment/backpack
    // were moved into the corpse; the pet bag stays on the player.
    player->leftRaid = false;
    player->hasInput = false;
    player->teleportPending = false;
    _respawnAtTick[playerId] = _currentTick + kRespawnDelayTicks;

    BroadcastPlayerEvent(playerId, 2);
    MaybeFinishRaid();
    Log::Info("player death pending respawn: player=" + std::to_string(playerId) +
              " tick=" + std::to_string(_currentTick + kRespawnDelayTicks));
}

void World::ApplyRespawns()
{
    std::vector<std::uint16_t> ready;
    for (const auto& [playerId, atTick] : _respawnAtTick) {
        if (_currentTick >= atTick)
            ready.push_back(playerId);
    }

    for (const std::uint16_t playerId : ready) {
        _respawnAtTick.erase(playerId);
        const Entity entity = FindPlayer(playerId);
        if (entity == kInvalidEntity)
            continue;
        auto* player = _ecs.Get<PlayerComponent>(entity);
        auto* health = _ecs.Get<Health>(entity);
        auto* position = _ecs.Get<Position>(entity);
        if (player == nullptr || health == nullptr || position == nullptr)
            continue;

        health->alive = true;
        health->maxHp = kPlayerMaxHp;
        health->hp = kPlayerMaxHp;
        player->leftRaid = false;
        player->hasInput = false;
        player->teleportPending = false;

        // D9/B: respawn at the authored player-spawn point for the same scene, not at the
        // death location. Fall back to the death position when content has no spawn point.
        std::int32_t spawnX = position->x;
        std::int32_t spawnY = position->y;
        std::int32_t spawnZ = position->z;
        std::uint16_t spawnYaw = position->yaw;
        if (PickPlayerSpawn(player->scene, static_cast<std::uint16_t>(playerId % 4),
                            spawnX, spawnY, spawnZ, spawnYaw)) {
            position->x = spawnX;
            position->y = spawnY;
            position->z = spawnZ;
            position->yaw = spawnYaw;
        }
        ProtectPlayer(playerId, 40);

        msg::Respawn respawn;
        respawn.playerId = playerId;
        respawn.x = position->x;
        respawn.y = position->y;
        respawn.z = position->z;
        respawn.yaw = position->yaw;
        respawn.hp = static_cast<std::uint16_t>(std::clamp(health->hp, 0, 65535));

        wire::Writer writer;
        respawn.Write(writer);
        Emit(Op::SRespawn, playerId, writer.Bytes());
        SendInitialInventory(playerId);
        Log::Info("player respawned: player=" + std::to_string(playerId) +
                  " hp=" + std::to_string(health->hp));
    }
}

void World::MaybeFinishRaid()
{
    // D9/B: a dead player with a pending respawn keeps the raid alive.
    if (!_respawnAtTick.empty())
        return;

    if (_phase != 1 || !_raidStarted)
        return;

    // an empty participant set means everyone left: the raid is over
    for (const std::uint16_t playerId : _raidParticipants) {
        const Entity entity = FindPlayer(playerId);
        if (entity == kInvalidEntity)
            continue;

        const auto* health = _ecs.Get<Health>(entity);
        if (health != nullptr && health->alive)
            return; // someone is still fighting
    }

    _phase = 2;
    _settleTicks = 20;

    Log::Info("raid finished: no alive players");

    msg::RaidPhaseMessage message;
    message.phase = _phase;
    message.tick = _lastSnapshot.tick;

    wire::Writer writer;
    message.Write(writer);
    Emit(Op::SRaidPhase, 0, writer.Bytes());
}

void World::OnPlayerDisconnected(std::uint16_t playerId)
{
    const Entity entity = FindPlayer(playerId);
    if (entity == kInvalidEntity)
        return;

    if (const auto* player = _ecs.Get<PlayerComponent>(entity); player != nullptr && player->leftRaid) {
        // extracted players keep the stash that was persisted on extract; dead/left
        // players already had theirs cleared when they left
        const auto* progress = _ecs.Get<PlayerProgress>(entity);
        const bool extracted = progress != nullptr && progress->extracted;
        if (!extracted)
            SettleStash(playerId, false);
        MaybeFinishRaid();
        Log::Info("player lost: " + std::to_string(playerId));
        return;
    }

    MarkPlayerLeft(entity, true);
    SettleStash(playerId, false);

    ReassignAiOwnership(playerId);
    _raidParticipants.erase(playerId);
    BroadcastPlayerEvent(playerId, 1); // left
    EmitCombat(1, 0, playerId, playerId, 0);
    MaybeFinishRaid();
    Log::Info("player lost: " + std::to_string(playerId));
}

void World::ApplyInputs()
{
    auto inputs = std::move(_pendingInputs);
    _pendingInputs.clear();

    for (const auto& [entity, input] : inputs) {
        auto* player = _ecs.Get<PlayerComponent>(entity);
        auto* position = _ecs.Get<Position>(entity);
        auto* health = _ecs.Get<Health>(entity);

        if (player == nullptr || position == nullptr || health == nullptr || !health->alive) {
            const std::uint16_t playerId = player != nullptr ? player->playerId : 0;
            _events.push_back(WorldEvent{EventType::InputRejected, entity, playerId, 1});
            continue;
        }

        if (player->teleportPending) {
            // SSpawnAssign moved the authoritative position to teleport*. An input that
            // was already in flight from the old position must not rewind it; only the
            // first input near the assigned point completes the teleport.
            const std::int64_t tx = static_cast<std::int64_t>(input.x) - player->teleportX;
            const std::int64_t ty = static_cast<std::int64_t>(input.y) - player->teleportY;
            const std::int64_t tz = static_cast<std::int64_t>(input.z) - player->teleportZ;
            if (tx * tx + ty * ty + tz * tz > kMaxStepCm * kMaxStepCm) {
                _events.push_back(WorldEvent{EventType::InputRejected, entity, player->playerId, 4});
                continue;
            }
            player->teleportPending = false;
            Log::Info("player teleport accepted: player=" + std::to_string(player->playerId) +
                      " seq=" + std::to_string(input.seq) + " pos=" + std::to_string(input.x) +
                      "," + std::to_string(input.y) + "," + std::to_string(input.z));
        }

        if (player->hasInput && input.seq <= player->lastSeq) {
            _events.push_back(WorldEvent{EventType::InputRejected, entity, player->playerId, 2});
            continue;
        }

        if (player->hasInput) {
            const std::int64_t dx = static_cast<std::int64_t>(input.x) - position->x;
            const std::int64_t dy = static_cast<std::int64_t>(input.y) - position->y;
            const std::int64_t dz = static_cast<std::int64_t>(input.z) - position->z;
            if (dx * dx + dy * dy + dz * dz > kMaxStepCm * kMaxStepCm) {
                if (player->inputRejectStreak < 2) {
                    ++player->inputRejectStreak;
                    _events.push_back(
                        WorldEvent{EventType::InputRejected, entity, player->playerId, 3});
                    continue;
                }
                Log::Warn("player input resync: player=" + std::to_string(player->playerId) +
                          " from=" + std::to_string(position->x) + "," +
                          std::to_string(position->y) + "," + std::to_string(position->z) +
                          " to=" + std::to_string(input.x) + "," + std::to_string(input.y) +
                          "," + std::to_string(input.z));
                player->inputRejectStreak = 0;
            } else {
                player->inputRejectStreak = 0;
            }
        } else {
            player->inputRejectStreak = 0;
        }

        position->x = input.x;
        position->y = input.y;
        position->z = input.z;
        position->yaw = input.yaw;
        player->lastSeq = input.seq;
        player->hasInput = true;
    }
}

void World::ApplyAiSync()
{
    auto states = std::move(_pendingAiState);
    _pendingAiState.clear();

    for (const auto& [fromPlayerId, state] : states) {
        const Entity entity = FindAi(state.aiId);
        if (entity == kInvalidEntity) {
            _events.push_back(WorldEvent{EventType::AiSyncRejected, entity, fromPlayerId, 1});
            continue;
        }

        auto* tag = _ecs.Get<AiTag>(entity);
        auto* position = _ecs.Get<Position>(entity);
        if (tag == nullptr || position == nullptr) {
            _events.push_back(WorldEvent{EventType::AiSyncRejected, entity, fromPlayerId, 2});
            continue;
        }

        // only the registered simulator may drive this AI
        if (tag->ownerPlayerId != fromPlayerId) {
            _events.push_back(WorldEvent{EventType::AiSyncRejected, entity, fromPlayerId, 3});
            continue;
        }

        // M2-v11: AI lifecycle is server-authoritative. A dormant AI (AOI sleep) must not be
        // resurrected by a state sample that was already in flight, so reject those instead of
        // copying the owner's legacy `flags` into AiTag::state (the client always sends 1).
        if (tag->state != 1) {
            _events.push_back(WorldEvent{EventType::AiSyncRejected, entity, fromPlayerId, 4});
            continue;
        }

        // transform only: health is server-authoritative and never taken from the client
        position->x = state.x;
        position->y = state.y;
        position->z = state.z;
        position->yaw = state.yaw;

        msg::AiState relay = state;
        relay.ownerPlayerId = tag->ownerPlayerId;

        wire::Writer writer;
        relay.Write(writer);
        Emit(Op::SAiState, 0, writer.Bytes(), 0);
    }
}

void World::ApplyCombat()
{
    auto damage = std::move(_pendingDamage);
    _pendingDamage.clear();

    for (const auto& [target, amount] : damage) {
        auto* health = _ecs.Get<Health>(target);
        if (health == nullptr || !health->alive || amount <= 0)
            continue;
        if (const auto* player = _ecs.Get<PlayerComponent>(target)) {
            const auto protect = _spawnProtectUntil.find(player->playerId);
            if (protect != _spawnProtectUntil.end() && protect->second > _currentTick)
                continue;
        }

        health->hp = std::max(0, health->hp - amount);
        const auto* player = _ecs.Get<PlayerComponent>(target);
        const auto* ai = _ecs.Get<AiTag>(target);
        const std::uint16_t playerId = player ? player->playerId : 0;

        _events.push_back(WorldEvent{EventType::PlayerDamaged, target, playerId, health->hp});

        if (health->hp == 0) {
            health->alive = false;
            _events.push_back(WorldEvent{ai != nullptr ? EventType::AiDied : EventType::PlayerDied,
                                        target, playerId, 0});
            if (ai == nullptr) {
                HandlePlayerDeath(target, playerId);
            }
        }
    }
}

void World::BuildSnapshot(std::int32_t tick)
{
    _lastSnapshot.tick = tick;
    _lastSnapshot.players.clear();

    const auto& players = _ecs.All<PlayerComponent>();
    _lastSnapshot.players.reserve(players.size());

    for (const auto& [entity, player] : players) {
        msg::PlayerSnapshot p;
        p.playerId = player.playerId;
        p.flags = 0;

        if (const auto* health = _ecs.Get<Health>(entity)) {
            p.hp = static_cast<std::uint16_t>(std::max(0, health->hp));
            // Contract 2.16.0: clients render the friendly bar as hp / maxHp. 0 means
            // "unknown" (no Health component) and makes the client keep its local value.
            p.maxHp = static_cast<std::uint16_t>(std::clamp(health->maxHp, 0, 65535));
            if (health->alive)
                p.flags |= 0x01;
        }

        if (const auto* position = _ecs.Get<Position>(entity)) {
            p.x = position->x;
            p.y = position->y;
            p.z = position->z;
            p.yaw = position->yaw;
            if (player.hasInput)
                p.flags |= 0x02;
        }

        p.scene = player.scene;

        const auto* progress = _ecs.Get<PlayerProgress>(entity);
        const bool extracted = progress != nullptr && progress->extracted;
        const bool participant = _raidParticipants.find(player.playerId) != _raidParticipants.end();

        // ADR-009 / BUG-001: 0x04 means "extracted in this raid" and is only set while the
        // player is still one of its participants. A player back in the base must report 0,
        // otherwise late joiners skip them (the client filters 0x04 before hasInput).
        if (extracted && participant)
            p.flags |= 0x04;

        // active raid participant: clients only render these while they are inside the raid
        if (!player.leftRaid && !extracted && !p.scene.empty() && participant)
            p.flags |= 0x08;

        _lastSnapshot.players.push_back(p);
    }
}

} // namespace coop
