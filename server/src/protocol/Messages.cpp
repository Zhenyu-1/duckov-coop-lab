#include "protocol/Messages.h"

#include "protocol/Opcodes.h"

namespace coop::msg {

namespace {

/// v9 bounded-string rule: clamp to the SPEC maximum, never cutting a UTF-8 sequence in
/// half (a truncated prefix ends before the codepoint that crossed the limit).
std::string ClampUtf8(const std::string& value, std::size_t maxBytes)
{
    if (value.size() <= maxBytes)
        return value;
    std::size_t size = maxBytes;
    while (size > 0 && (static_cast<unsigned char>(value[size]) & 0xC0) == 0x80)
        --size;
    return value.substr(0, size);
}

std::string ReadClampedString(wire::Reader& r, std::size_t maxBytes)
{
    return ClampUtf8(r.Str(), maxBytes);
}

/// v9 bounded-collection rule: write at most `maxEntries`; read at most `maxEntries` of the
/// declared count (over-limit values are clamped instead of allocating unbounded vectors).
std::uint16_t ClampCount(std::size_t size, std::size_t maxEntries)
{
    return static_cast<std::uint16_t>(size > maxEntries ? maxEntries : size);
}

std::uint16_t ReadClampedCount(wire::Reader& r, std::size_t maxEntries)
{
    const std::uint16_t declared = r.U16();
    return declared > maxEntries ? static_cast<std::uint16_t>(maxEntries) : declared;
}

} // namespace

void LoginRequest::Write(wire::Writer& w) const
{
    w.Str(account);
    w.Str(passwordHash);
    w.Str(clientVersion);
    w.Raw(contentHash.data(), contentHash.size());
}

LoginRequest LoginRequest::Read(wire::Reader& r)
{
    LoginRequest m;
    m.account = r.Str();
    m.passwordHash = r.Str();
    m.clientVersion = r.Str();
    r.Raw(m.contentHash.data(), m.contentHash.size());
    return m;
}

void LoginResult::Write(wire::Writer& w) const
{
    w.U8(code);
    w.Str(token);
    w.I64(expiresAtMs);
}

LoginResult LoginResult::Read(wire::Reader& r)
{
    LoginResult m;
    m.code = r.U8();
    m.token = r.Str();
    m.expiresAtMs = r.I64();
    return m;
}

void JoinRequest::Write(wire::Writer& w) const { w.Str(token); }

JoinRequest JoinRequest::Read(wire::Reader& r)
{
    JoinRequest m;
    m.token = r.Str();
    return m;
}

void JoinResult::Write(wire::Writer& w) const
{
    w.U8(code);
    w.U16(playerId);
    w.U64(raidId);
    w.Str(sceneId);
    w.I32(startTick);
    w.U8(tickRate);
}

JoinResult JoinResult::Read(wire::Reader& r)
{
    JoinResult m;
    m.code = r.U8();
    m.playerId = r.U16();
    m.raidId = r.U64();
    m.sceneId = r.Str();
    m.startTick = r.I32();
    m.tickRate = r.U8();
    return m;
}

void Input::Write(wire::Writer& w) const
{
    w.U32(seq);
    w.I32(x);
    w.I32(y);
    w.I32(z);
    w.U16(yaw);
    w.I16(pitch);
    w.U8(flags);
}

Input Input::Read(wire::Reader& r)
{
    Input m;
    m.seq = r.U32();
    m.x = r.I32();
    m.y = r.I32();
    m.z = r.I32();
    m.yaw = r.U16();
    m.pitch = r.I16();
    m.flags = r.U8();
    return m;
}

void AiState::Write(wire::Writer& w) const
{
    w.U32(aiId);
    w.U16(ownerPlayerId);
    w.I32(x);
    w.I32(y);
    w.I32(z);
    w.U16(yaw);
    w.U8(flags);
}

AiState AiState::Read(wire::Reader& r)
{
    AiState m;
    m.aiId = r.U32();
    m.ownerPlayerId = r.U16();
    m.x = r.I32();
    m.y = r.I32();
    m.z = r.I32();
    m.yaw = r.U16();
    m.flags = r.U8();
    return m;
}

void LootRequest::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U32(containerId);
    w.U16(slot);
    w.U8(action);
    w.U32(itemUid);
    w.U32(count);
}

LootRequest LootRequest::Read(wire::Reader& r)
{
    LootRequest m;
    m.correlationId = r.U64();
    m.containerId = r.U32();
    m.slot = r.U16();
    m.action = r.U8();
    m.itemUid = r.U32();
    m.count = r.U32();
    return m;
}

void LootResult::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(code);
    w.U32(itemUid);
    w.U32(itemId);
    w.U32(count);
    w.U16(slot);
}

LootResult LootResult::Read(wire::Reader& r)
{
    LootResult m;
    m.correlationId = r.U64();
    m.code = r.U8();
    m.itemUid = r.U32();
    m.itemId = r.U32();
    m.count = r.U32();
    m.slot = r.U16();
    return m;
}

void ExtractRequest::Write(wire::Writer& w) const { w.U64(correlationId); }

ExtractRequest ExtractRequest::Read(wire::Reader& r)
{
    ExtractRequest m;
    m.correlationId = r.U64();
    return m;
}

void ExtractResult::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(code);
}

ExtractResult ExtractResult::Read(wire::Reader& r)
{
    ExtractResult m;
    m.correlationId = r.U64();
    m.code = r.U8();
    return m;
}

void PlayerReady::Write(wire::Writer& w) const { w.U8(ready ? 1 : 0); }

PlayerReady PlayerReady::Read(wire::Reader& r)
{
    PlayerReady m;
    m.ready = r.U8() != 0;
    return m;
}

void PlayerReadyState::Write(wire::Writer& w) const
{
    w.U16(playerId);
    w.U8(ready ? 1 : 0);
}

PlayerReadyState PlayerReadyState::Read(wire::Reader& r)
{
    PlayerReadyState m;
    m.playerId = r.U16();
    m.ready = r.U8() != 0;
    return m;
}

void ForceResetRaid::Write(wire::Writer& w) const { (void)w; }

ForceResetRaid ForceResetRaid::Read(wire::Reader& r)
{
    (void)r;
    return ForceResetRaid{};
}

void ForceResetRaidState::Write(wire::Writer& w) const { w.U16(byPlayerId); }

ForceResetRaidState ForceResetRaidState::Read(wire::Reader& r)
{
    ForceResetRaidState m;
    m.byPlayerId = r.U16();
    return m;
}

void RaidControlResult::Write(wire::Writer& w) const
{
    w.U8(action);
    w.U8(code);
    w.U16(playerId);
}

RaidControlResult RaidControlResult::Read(wire::Reader& r)
{
    RaidControlResult m;
    m.action = r.U8();
    m.code = r.U8();
    m.playerId = r.U16();
    return m;
}

void PlayerEvent::Write(wire::Writer& w) const
{
    w.U64(eventId);
    w.U16(playerId);
    w.U8(type);
}

PlayerEvent PlayerEvent::Read(wire::Reader& r)
{
    PlayerEvent m;
    m.eventId = r.U64();
    m.playerId = r.U16();
    m.type = r.U8();
    return m;
}

void RaidPhaseMessage::Write(wire::Writer& w) const
{
    w.U8(phase);
    w.I32(tick);
}

RaidPhaseMessage RaidPhaseMessage::Read(wire::Reader& r)
{
    RaidPhaseMessage m;
    m.phase = r.U8();
    m.tick = r.I32();
    return m;
}

void ContainerViewRequest::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U32(containerId);
}

ContainerViewRequest ContainerViewRequest::Read(wire::Reader& r)
{
    ContainerViewRequest m;
    m.correlationId = r.U64();
    m.containerId = r.U32();
    return m;
}

void ContainerView::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U32(containerId);
    w.U16(capacity);
    const auto count = static_cast<std::uint8_t>(entries.size() > 255 ? 255 : entries.size());
    w.U8(count);
    for (std::size_t i = 0; i < count; ++i) {
        const auto& entry = entries[i];
        w.U16(entry.slot);
        w.U32(entry.itemUid);
        w.U32(entry.itemId);
        w.U32(entry.count);
    }
}

ContainerView ContainerView::Read(wire::Reader& r)
{
    ContainerView m;
    m.correlationId = r.U64();
    m.containerId = r.U32();
    m.capacity = r.U16();
    const std::uint8_t count = r.U8();
    m.entries.reserve(count);
    for (std::uint8_t i = 0; i < count; ++i) {
        ContainerViewEntry entry;
        entry.slot = r.U16();
        entry.itemUid = r.U32();
        entry.itemId = r.U32();
        entry.count = r.U32();
        m.entries.push_back(entry);
    }
    return m;
}

void RaidStart::Write(wire::Writer& w) const
{
    w.U32(sceneIndex);
    w.Str(sceneName);
    w.U32(raidEpoch);
}

RaidStart RaidStart::Read(wire::Reader& r)
{
    RaidStart m;
    m.sceneIndex = r.U32();
    m.sceneName = r.Str();
    m.raidEpoch = r.U32();
    return m;
}

void AiSpawn::Write(wire::Writer& w) const
{
    w.U32(aiId);
    w.U16(ownerPlayerId);
    w.U32(monsterId);
    w.I32(x);
    w.I32(y);
    w.I32(z);
    w.U16(yaw);
    w.U16(hp);
    w.U32(raidEpoch);
    w.U16(activationRadius);
    w.U16(deactivationRadius);
}

AiSpawn AiSpawn::Read(wire::Reader& r)
{
    AiSpawn m;
    m.aiId = r.U32();
    m.ownerPlayerId = r.U16();
    m.monsterId = r.U32();
    m.x = r.I32();
    m.y = r.I32();
    m.z = r.I32();
    m.yaw = r.U16();
    m.hp = r.U16();
    m.raidEpoch = r.U32();
    m.activationRadius = r.U16();
    m.deactivationRadius = r.U16();
    return m;
}

void AiDespawn::Write(wire::Writer& w) const
{
    w.U32(aiId);
    w.U32(raidEpoch);
    w.U8(reason);
}

AiDespawn AiDespawn::Read(wire::Reader& r)
{
    AiDespawn m;
    m.aiId = r.U32();
    m.raidEpoch = r.U32();
    m.reason = r.U8();
    return m;
}

void HitEvent::Write(wire::Writer& w) const
{
    w.U32(hitId);
    w.U8(sourceKind);
    w.U32(sourceId);
    w.U16(reporterPlayerId);
    w.U8(targetKind);
    w.U32(targetId);
    w.U32(weaponId);
    w.U32(ammoId);
    w.U32(attackId);
    w.I32(hitX);
    w.I32(hitY);
    w.I32(hitZ);
    w.U8(flags);
    if ((flags & static_cast<std::uint8_t>(HitFlags::HasImpactCenter)) != 0) {
        w.I32(impactX);
        w.I32(impactY);
        w.I32(impactZ);
    }
    if ((flags & static_cast<std::uint8_t>(HitFlags::HasRadius)) != 0)
        w.U16(radius);
}

HitEvent HitEvent::Read(wire::Reader& r)
{
    HitEvent m;
    m.hitId = r.U32();
    m.sourceKind = r.U8();
    m.sourceId = r.U32();
    m.reporterPlayerId = r.U16();
    m.targetKind = r.U8();
    m.targetId = r.U32();
    m.weaponId = r.U32();
    m.ammoId = r.U32();
    m.attackId = r.U32();
    m.hitX = r.I32();
    m.hitY = r.I32();
    m.hitZ = r.I32();
    m.flags = r.U8();
    if ((m.flags & static_cast<std::uint8_t>(HitFlags::HasImpactCenter)) != 0) {
        m.impactX = r.I32();
        m.impactY = r.I32();
        m.impactZ = r.I32();
    }
    if ((m.flags & static_cast<std::uint8_t>(HitFlags::HasRadius)) != 0)
        m.radius = r.U16();
    return m;
}

void CombatEventV4::Write(wire::Writer& w) const
{
    w.U64(eventId);
    w.U32(causeId);
    w.U8(type);
    w.U8(sourceKind);
    w.U32(sourceId);
    w.U16(reporterPlayerId);
    w.U8(targetKind);
    w.U32(targetId);
    w.U32(damage);
    w.I32(hpAfter);
    w.I32(maxHp);
    w.U8(element);
    w.U8(crit);
    w.I32(hitX);
    w.I32(hitY);
    w.I32(hitZ);
    w.U32(weaponId);
    w.U8(flags);
}

CombatEventV4 CombatEventV4::Read(wire::Reader& r)
{
    CombatEventV4 m;
    m.eventId = r.U64();
    m.causeId = r.U32();
    m.type = r.U8();
    m.sourceKind = r.U8();
    m.sourceId = r.U32();
    m.reporterPlayerId = r.U16();
    m.targetKind = r.U8();
    m.targetId = r.U32();
    m.damage = r.U32();
    m.hpAfter = r.I32();
    m.maxHp = r.I32();
    m.element = r.U8();
    m.crit = r.U8();
    m.hitX = r.I32();
    m.hitY = r.I32();
    m.hitZ = r.I32();
    m.weaponId = r.U32();
    m.flags = r.U8();
    return m;
}

void AiAnim::Write(wire::Writer& w) const
{
    w.U32(aiId);
    w.U32(raidEpoch);
    w.U16(seq);
    w.U16(moveSpeed);
    w.U8(static_cast<std::uint8_t>(moveDirX));
    w.U8(static_cast<std::uint8_t>(moveDirY));
    w.U8(handState);
    w.U8(attackIndex);
    w.U8(flags);
    w.U8(attackSeq);
}

AiAnim AiAnim::Read(wire::Reader& r)
{
    AiAnim m;
    m.aiId = r.U32();
    m.raidEpoch = r.U32();
    m.seq = r.U16();
    m.moveSpeed = r.U16();
    m.moveDirX = static_cast<std::int8_t>(r.U8());
    m.moveDirY = static_cast<std::int8_t>(r.U8());
    m.handState = r.U8();
    m.attackIndex = r.U8();
    m.flags = r.U8();
    m.attackSeq = r.U8();
    return m;
}

void AiHealth::Write(wire::Writer& w) const
{
    w.U32(aiId);
    w.U32(raidEpoch);
    w.I32(hp);
    w.I32(maxHp);
    w.U8(flags);
    w.U16(seq);
}

AiHealth AiHealth::Read(wire::Reader& r)
{
    AiHealth m;
    m.aiId = r.U32();
    m.raidEpoch = r.U32();
    m.hp = r.I32();
    m.maxHp = r.I32();
    m.flags = r.U8();
    m.seq = r.U16();
    return m;
}

void AiLoadout::Write(wire::Writer& w) const
{
    w.U32(aiId);
    w.U32(raidEpoch);
    w.U32(weaponId);
    w.U32(bulletId);
    w.U32(armorId);
    w.U32(helmetId);
}

AiLoadout AiLoadout::Read(wire::Reader& r)
{
    AiLoadout m;
    m.aiId = r.U32();
    m.raidEpoch = r.U32();
    m.weaponId = r.U32();
    m.bulletId = r.U32();
    m.armorId = r.U32();
    m.helmetId = r.U32();
    return m;
}

void AiAim::Write(wire::Writer& w) const
{
    w.U32(aiId);
    w.U32(raidEpoch);
    w.U16(seq);
    w.U16(aimYaw);
    w.I16(aimPitch);
    w.U8(flags);
}

AiAim AiAim::Read(wire::Reader& r)
{
    AiAim m;
    m.aiId = r.U32();
    m.raidEpoch = r.U32();
    m.seq = r.U16();
    m.aimYaw = r.U16();
    m.aimPitch = r.I16();
    m.flags = r.U8();
    return m;
}

void AiStateSample::Write(wire::Writer& w) const
{
    w.U32(aiId);
    w.U32(raidEpoch);
    w.U16(seq);
    w.U32(stateHash);
    w.U16(normTime);
    w.U8(flags);
}

AiStateSample AiStateSample::Read(wire::Reader& r)
{
    AiStateSample m;
    m.aiId = r.U32();
    m.raidEpoch = r.U32();
    m.seq = r.U16();
    m.stateHash = r.U32();
    m.normTime = r.U16();
    m.flags = r.U8();
    return m;
}

void AiLoadoutV7::Write(wire::Writer& w) const
{
    w.U32(aiId);
    w.U32(raidEpoch);
    w.U8(activeSlot);
    w.U8(static_cast<std::uint8_t>(slots.size()));
    for (const AiLoadoutSlot& slot : slots) {
        w.U8(slot.slot);
        w.U16(static_cast<std::uint16_t>(slot.blob.size()));
        w.Raw(slot.blob.data(), slot.blob.size());
    }
}

AiLoadoutV7 AiLoadoutV7::Read(wire::Reader& r)
{
    AiLoadoutV7 m;
    m.aiId = r.U32();
    m.raidEpoch = r.U32();
    m.activeSlot = r.U8();
    const std::uint8_t count = r.U8();
    for (std::uint8_t i = 0; i < count; ++i) {
        AiLoadoutSlot slot;
        slot.slot = r.U8();
        const std::uint16_t length = r.U16();
        if (length > 0) {
            slot.blob.resize(length);
            r.Raw(slot.blob.data(), slot.blob.size());
        }
        m.slots.push_back(std::move(slot));
    }
    return m;
}

void AiShot::Write(wire::Writer& w) const
{
    w.U32(aiId);
    w.U32(raidEpoch);
    w.U32(shotId);
    w.U32(weaponId);
    w.U32(bulletId);
    w.I32(originX);
    w.I32(originY);
    w.I32(originZ);
    w.I16(dirX);
    w.I16(dirY);
    w.I16(dirZ);
    w.U32(seed);
    w.U8(flags);
}

AiShot AiShot::Read(wire::Reader& r)
{
    AiShot m;
    m.aiId = r.U32();
    m.raidEpoch = r.U32();
    m.shotId = r.U32();
    m.weaponId = r.U32();
    m.bulletId = r.U32();
    m.originX = r.I32();
    m.originY = r.I32();
    m.originZ = r.I32();
    m.dirX = r.I16();
    m.dirY = r.I16();
    m.dirZ = r.I16();
    m.seed = r.U32();
    m.flags = r.U8();
    return m;
}

void PlayerStateSample::Write(wire::Writer& w) const
{
    w.U32(raidEpoch);
    w.U16(seq);
    w.U32(stateHash);
    w.U16(normTime);
    w.U8(flags);
    w.U8(attackSeq);
    w.U8(handState);
}

PlayerStateSample PlayerStateSample::Read(wire::Reader& r)
{
    PlayerStateSample m;
    m.raidEpoch = r.U32();
    m.seq = r.U16();
    m.stateHash = r.U32();
    m.normTime = r.U16();
    m.flags = r.U8();
    m.attackSeq = r.U8();
    m.handState = r.U8();
    return m;
}

void PlayerLoadoutV8::Write(wire::Writer& w) const
{
    w.U32(raidEpoch);
    w.U8(activeSlot);
    w.U8(slot);
    w.U16(static_cast<std::uint16_t>(blob.size()));
    w.Raw(blob.data(), blob.size());
}

PlayerLoadoutV8 PlayerLoadoutV8::Read(wire::Reader& r)
{
    PlayerLoadoutV8 m;
    m.raidEpoch = r.U32();
    m.activeSlot = r.U8();
    m.slot = r.U8();
    const std::uint16_t length = r.U16();
    if (length > 0) {
        m.blob.resize(length);
        r.Raw(m.blob.data(), m.blob.size());
    }
    return m;
}

void PlayerShot::Write(wire::Writer& w) const
{
    w.U32(raidEpoch);
    w.U32(shotId);
    w.U32(weaponId);
    w.U32(bulletId);
    w.I32(originX);
    w.I32(originY);
    w.I32(originZ);
    w.I16(dirX);
    w.I16(dirY);
    w.I16(dirZ);
    w.U32(seed);
    w.U8(flags);
}

PlayerShot PlayerShot::Read(wire::Reader& r)
{
    PlayerShot m;
    m.raidEpoch = r.U32();
    m.shotId = r.U32();
    m.weaponId = r.U32();
    m.bulletId = r.U32();
    m.originX = r.I32();
    m.originY = r.I32();
    m.originZ = r.I32();
    m.dirX = r.I16();
    m.dirY = r.I16();
    m.dirZ = r.I16();
    m.seed = r.U32();
    m.flags = r.U8();
    return m;
}

void PlayerFaceV8::Write(wire::Writer& w) const
{
    w.U32(raidEpoch);
    w.U16(static_cast<std::uint16_t>(face.size()));
    w.Raw(face.data(), face.size());
}

PlayerFaceV8 PlayerFaceV8::Read(wire::Reader& r)
{
    PlayerFaceV8 m;
    m.raidEpoch = r.U32();
    const std::uint16_t length = r.U16();
    if (length > 0) {
        m.face.resize(length);
        r.Raw(m.face.data(), m.face.size());
    }
    return m;
}

void PlayerStats::Write(wire::Writer& w) const
{
    w.F32(gunDamageMultiplier);
    w.F32(gunCritRateGain);
    w.F32(gunCritDamageGain);
    w.F32(meleeDamageMultiplier);
    w.F32(meleeCritRateGain);
    w.F32(meleeCritDamageGain);
    for (float value : elementFactors)
        w.F32(value);
    w.F32(bodyArmor);
    w.F32(headArmor);
    w.F32(maxHealth);
}

PlayerStats PlayerStats::Read(wire::Reader& r)
{
    PlayerStats m;
    m.gunDamageMultiplier = r.F32();
    m.gunCritRateGain = r.F32();
    m.gunCritDamageGain = r.F32();
    m.meleeDamageMultiplier = r.F32();
    m.meleeCritRateGain = r.F32();
    m.meleeCritDamageGain = r.F32();
    for (float& value : m.elementFactors)
        value = r.F32();
    m.bodyArmor = r.F32();
    m.headArmor = r.F32();
    m.maxHealth = r.F32();
    return m;
}

void ShootFact::Write(wire::Writer& w) const
{
    w.U8(sourceKind);
    w.U32(sourceId);
    w.U32(weaponId);
    w.U32(ammoId);
    w.U32(attackId);
    w.U8(flags);
}

ShootFact ShootFact::Read(wire::Reader& r)
{
    ShootFact m;
    m.sourceKind = r.U8();
    m.sourceId = r.U32();
    m.weaponId = r.U32();
    m.ammoId = r.U32();
    m.attackId = r.U32();
    m.flags = r.U8();
    return m;
}

void UseConsumable::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U32(itemId);
    w.U8(slot);
    w.U8(flags);
}

UseConsumable UseConsumable::Read(wire::Reader& r)
{
    UseConsumable m;
    m.correlationId = r.U64();
    m.itemId = r.U32();
    m.slot = r.U8();
    m.flags = r.U8();
    return m;
}

void PlayerBuff::Write(wire::Writer& w) const
{
    w.U16(playerId);
    w.U32(buffId);
    w.U8(layers);
    w.U32(remainingMs);
    w.U8(op);
}

PlayerBuff PlayerBuff::Read(wire::Reader& r)
{
    PlayerBuff m;
    m.playerId = r.U16();
    m.buffId = r.U32();
    m.layers = r.U8();
    m.remainingMs = r.U32();
    m.op = r.U8();
    return m;
}

void RaidRules::Write(wire::Writer& w) const
{
    w.U8(rulesetId);
    w.F32(damageFactorToPlayer);
    w.F32(enemyHealthFactor);
    w.F32(enemyAttackTimeFactor);
    w.F32(enemyAttackTimeSpaceFactor);
    w.F32(enemyReactionTimeFactor);
    w.U8(flags);
    w.F32(recoilMultiplier);
    w.U8(weather);
    w.U16(timeOfDayMinutes);
    w.U8(saveDeadbodyCount);
    w.U32(weatherSeed);
    w.Raw(baseStatsHash.data(), baseStatsHash.size());
}

RaidRules RaidRules::Read(wire::Reader& r)
{
    RaidRules m;
    m.rulesetId = r.U8();
    m.damageFactorToPlayer = r.F32();
    m.enemyHealthFactor = r.F32();
    m.enemyAttackTimeFactor = r.F32();
    m.enemyAttackTimeSpaceFactor = r.F32();
    m.enemyReactionTimeFactor = r.F32();
    m.flags = r.U8();
    m.recoilMultiplier = r.F32();
    m.weather = r.U8();
    m.timeOfDayMinutes = r.U16();
    m.saveDeadbodyCount = r.U8();
    m.weatherSeed = r.U32();
    r.Raw(m.baseStatsHash.data(), m.baseStatsHash.size());
    return m;
}

void HitReport::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(targetKind);
    w.U32(targetId);
    w.I32(damage);
}

HitReport HitReport::Read(wire::Reader& r)
{
    HitReport m;
    m.correlationId = r.U64();
    m.targetKind = r.U8();
    m.targetId = r.U32();
    m.damage = r.I32();
    return m;
}

void CombatEvent::Write(wire::Writer& w) const
{
    w.U64(eventId);
    w.U8(type);
    w.U8(targetKind);
    w.U32(targetId);
    w.U16(playerId);
    w.I32(value);
}

CombatEvent CombatEvent::Read(wire::Reader& r)
{
    CombatEvent m;
    m.eventId = r.U64();
    m.type = r.U8();
    m.targetKind = r.U8();
    m.targetId = r.U32();
    m.playerId = r.U16();
    m.value = r.I32();
    return m;
}

void PlayerSpawn::Write(wire::Writer& w) const
{
    w.Str(sceneName);
    w.I32(x);
    w.I32(y);
    w.I32(z);
    w.U32(raidEpoch);
}

PlayerSpawn PlayerSpawn::Read(wire::Reader& r)
{
    PlayerSpawn m;
    m.sceneName = r.Str();
    m.x = r.I32();
    m.y = r.I32();
    m.z = r.I32();
    m.raidEpoch = r.U32();
    return m;
}

void LoadoutRequest::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    const auto count = static_cast<std::uint8_t>(entries.size() > 255 ? 255 : entries.size());
    w.U8(count);
    for (std::size_t i = 0; i < count; ++i) {
        w.U8(entries[i].slot);
        w.U32(entries[i].itemId);
    }
}

LoadoutRequest LoadoutRequest::Read(wire::Reader& r)
{
    LoadoutRequest m;
    m.correlationId = r.U64();
    const std::uint8_t count = r.U8();
    m.entries.reserve(count);
    for (std::uint8_t i = 0; i < count; ++i) {
        LoadoutEntry entry;
        entry.slot = r.U8();
        entry.itemId = r.U32();
        m.entries.push_back(entry);
    }
    return m;
}

void LoadoutState::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(code);
    w.U16(playerId);
    const auto count = static_cast<std::uint8_t>(entries.size() > 255 ? 255 : entries.size());
    w.U8(count);
    for (std::size_t i = 0; i < count; ++i) {
        w.U8(entries[i].slot);
        w.U32(entries[i].itemId);
    }
}

LoadoutState LoadoutState::Read(wire::Reader& r)
{
    LoadoutState m;
    m.correlationId = r.U64();
    m.code = r.U8();
    m.playerId = r.U16();
    const std::uint8_t count = r.U8();
    m.entries.reserve(count);
    for (std::uint8_t i = 0; i < count; ++i) {
        LoadoutEntry entry;
        entry.slot = r.U8();
        entry.itemId = r.U32();
        m.entries.push_back(entry);
    }
    return m;
}

void MirrorState::Write(wire::Writer& w) const
{
    w.U64(version);
    w.U16(playerId);
    const auto count = static_cast<std::uint8_t>(entries.size() > 255 ? 255 : entries.size());
    w.U8(count);
    for (std::size_t i = 0; i < count; ++i) {
        w.U32(entries[i].itemId);
        w.U32(entries[i].count);
    }
}

MirrorState MirrorState::Read(wire::Reader& r)
{
    MirrorState m;
    m.version = r.U64();
    m.playerId = r.U16();
    const std::uint8_t count = r.U8();
    m.entries.reserve(count);
    for (std::uint8_t i = 0; i < count; ++i) {
        MirrorEntry entry;
        entry.itemId = r.U32();
        entry.count = r.U32();
        m.entries.push_back(entry);
    }
    return m;
}

void MirrorAck::Write(wire::Writer& w) const { w.U64(version); }

MirrorAck MirrorAck::Read(wire::Reader& r)
{
    MirrorAck m;
    m.version = r.U64();
    return m;
}

void AiTarget::Write(wire::Writer& w) const
{
    w.U32(aiId);
    w.U16(playerId);
    w.U16(reserved);
    w.I32(tick);
}

AiTarget AiTarget::Read(wire::Reader& r)
{
    AiTarget m;
    m.aiId = r.U32();
    m.playerId = r.U16();
    m.reserved = r.U16();
    m.tick = r.I32();
    return m;
}

void ResumeRequest::Write(wire::Writer& w) const
{
    w.Str(token);
}

ResumeRequest ResumeRequest::Read(wire::Reader& r)
{
    ResumeRequest m;
    m.token = r.Str();
    return m;
}

void ResumeResult::Write(wire::Writer& w) const
{
    w.U8(code);
    w.U16(playerId);
    w.Str(sceneName);
    w.I32(x);
    w.I32(y);
    w.I32(z);
    w.U32(raidEpoch);
}

ResumeResult ResumeResult::Read(wire::Reader& r)
{
    ResumeResult m;
    m.code = r.U8();
    m.playerId = r.U16();
    m.sceneName = r.Str();
    m.x = r.I32();
    m.y = r.I32();
    m.z = r.I32();
    m.raidEpoch = r.U32();
    return m;
}

void SpawnAssign::Write(wire::Writer& w) const
{
    w.I32(x);
    w.I32(y);
    w.I32(z);
    w.U16(seat);
}

SpawnAssign SpawnAssign::Read(wire::Reader& r)
{
    SpawnAssign m;
    m.x = r.I32();
    m.y = r.I32();
    m.z = r.I32();
    m.seat = r.U16();
    return m;
}

void Snapshot::Write(wire::Writer& w) const
{
    w.I32(tick);
    const auto count = static_cast<std::uint8_t>(players.size() > 255 ? 255 : players.size());
    w.U8(count);
    for (std::size_t i = 0; i < count; ++i) {
        const auto& p = players[i];
        w.U16(p.playerId);
        w.I32(p.x);
        w.I32(p.y);
        w.I32(p.z);
        w.U16(p.yaw);
        w.U8(p.flags);
        w.U16(p.hp);
        w.U16(p.maxHp);
        w.Str(p.scene);
    }
}

Snapshot Snapshot::Read(wire::Reader& r)
{
    Snapshot m;
    m.tick = r.I32();
    const std::uint8_t count = r.U8();
    m.players.reserve(count);
    for (std::uint8_t i = 0; i < count; ++i) {
        PlayerSnapshot p;
        p.playerId = r.U16();
        p.x = r.I32();
        p.y = r.I32();
        p.z = r.I32();
        p.yaw = r.U16();
        p.flags = r.U8();
        p.hp = r.U16();
        p.maxHp = r.U16();
        p.scene = r.Str();
        m.players.push_back(p);
    }
    return m;
}

// ---------------------------------------------------------------------------
// v9 item contract
// ---------------------------------------------------------------------------

void InventoryEntry::Write(wire::Writer& w) const
{
    w.U16(slot);
    w.U32(itemUid);
    w.U32(itemId);
    w.U32(count);
}

InventoryEntry InventoryEntry::Read(wire::Reader& r)
{
    InventoryEntry m;
    m.slot = r.U16();
    m.itemUid = r.U32();
    m.itemId = r.U32();
    m.count = r.U32();
    return m;
}

void InventorySnapshot::Write(wire::Writer& w) const
{
    w.U8(kind);
    w.U32(ownerId);
    w.U32(revision);
    w.U16(capacity);
    const std::uint16_t count = ClampCount(entries.size(), kMaxInventorySnapshotEntries);
    w.U16(count);
    for (std::uint16_t i = 0; i < count; ++i)
        entries[i].Write(w);
}

InventorySnapshot InventorySnapshot::Read(wire::Reader& r)
{
    InventorySnapshot m;
    m.kind = r.U8();
    m.ownerId = r.U32();
    m.revision = r.U32();
    m.capacity = r.U16();
    const std::uint16_t count = ReadClampedCount(r, kMaxInventorySnapshotEntries);
    m.entries.reserve(count);
    for (std::uint16_t i = 0; i < count; ++i)
        m.entries.push_back(InventoryEntry::Read(r));
    return m;
}

void InitialInventoryEntry::Write(wire::Writer& w) const
{
    w.U8(kind);
    entry.Write(w);
}

InitialInventoryEntry InitialInventoryEntry::Read(wire::Reader& r)
{
    InitialInventoryEntry m;
    m.kind = r.U8();
    m.entry = InventoryEntry::Read(r);
    return m;
}

void InitialInventory::Write(wire::Writer& w) const
{
    w.Str(ClampUtf8(ticketId, kMaxTicketIdBytes));
    w.U32(revision);
    w.U16(equipmentCapacity);
    w.U16(backpackCapacity);
    w.U16(petBagCapacity);
    const std::uint16_t count = ClampCount(entries.size(), kMaxInitialInventoryEntries);
    w.U16(count);
    for (std::uint16_t i = 0; i < count; ++i)
        entries[i].Write(w);
}

InitialInventory InitialInventory::Read(wire::Reader& r)
{
    InitialInventory m;
    m.ticketId = ReadClampedString(r, kMaxTicketIdBytes);
    m.revision = r.U32();
    m.equipmentCapacity = r.U16();
    m.backpackCapacity = r.U16();
    m.petBagCapacity = r.U16();
    const std::uint16_t count = ReadClampedCount(r, kMaxInitialInventoryEntries);
    m.entries.reserve(count);
    for (std::uint16_t i = 0; i < count; ++i)
        m.entries.push_back(InitialInventoryEntry::Read(r));
    return m;
}

void ItemTransfer::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(sourceKind);
    w.U32(sourceId);
    w.U16(sourceSlot);
    w.U8(targetKind);
    w.U32(targetId);
    w.U16(targetSlot);
    w.U32(itemUid);
    w.U32(count);
    w.U32(expectedItemId);
    w.U32(expectedRevision);
}

ItemTransfer ItemTransfer::Read(wire::Reader& r)
{
    ItemTransfer m;
    m.correlationId = r.U64();
    m.sourceKind = r.U8();
    m.sourceId = r.U32();
    m.sourceSlot = r.U16();
    m.targetKind = r.U8();
    m.targetId = r.U32();
    m.targetSlot = r.U16();
    m.itemUid = r.U32();
    m.count = r.U32();
    m.expectedItemId = r.U32();
    m.expectedRevision = r.U32();
    return m;
}

void ItemTransferResult::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(code);
    w.U32(itemUid);
    w.U32(sourceRevision);
    w.U32(targetRevision);
}

ItemTransferResult ItemTransferResult::Read(wire::Reader& r)
{
    ItemTransferResult m;
    m.correlationId = r.U64();
    m.code = r.U8();
    m.itemUid = r.U32();
    m.sourceRevision = r.U32();
    m.targetRevision = r.U32();
    return m;
}

void ItemArrange::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(kind);
    w.U32(ownerId);
    w.U32(expectedRevision);
    const std::uint16_t count = ClampCount(entries.size(), kMaxInventorySnapshotEntries);
    w.U16(count);
    for (std::uint16_t i = 0; i < count; ++i)
        entries[i].Write(w);
}

ItemArrange ItemArrange::Read(wire::Reader& r)
{
    ItemArrange m;
    m.correlationId = r.U64();
    m.kind = r.U8();
    m.ownerId = r.U32();
    m.expectedRevision = r.U32();
    const std::uint16_t count = ReadClampedCount(r, kMaxInventorySnapshotEntries);
    m.entries.reserve(count);
    for (std::uint16_t i = 0; i < count; ++i)
        m.entries.push_back(InventoryEntry::Read(r));
    return m;
}

void ItemArrangeResult::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(code);
    w.U32(revision);
}

ItemArrangeResult ItemArrangeResult::Read(wire::Reader& r)
{
    ItemArrangeResult m;
    m.correlationId = r.U64();
    m.code = r.U8();
    m.revision = r.U32();
    return m;
}

void ItemConsume::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(kind);
    w.U32(ownerId);
    w.U16(slot);
    w.U32(itemUid);
    w.U32(itemId);
    w.U32(count);
    w.U32(expectedRevision);
    w.U8(reason);
    w.U32(healAmount);
}

ItemConsume ItemConsume::Read(wire::Reader& r)
{
    ItemConsume m;
    m.correlationId = r.U64();
    m.kind = r.U8();
    m.ownerId = r.U32();
    m.slot = r.U16();
    m.itemUid = r.U32();
    m.itemId = r.U32();
    m.count = r.U32();
    m.expectedRevision = r.U32();
    m.reason = r.U8();
    m.healAmount = r.U32();
    return m;
}

void ItemConsumeResult::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(code);
    w.U32(itemUid);
    w.U32(remaining);
    w.U32(revision);
}

ItemConsumeResult ItemConsumeResult::Read(wire::Reader& r)
{
    ItemConsumeResult m;
    m.correlationId = r.U64();
    m.code = r.U8();
    m.itemUid = r.U32();
    m.remaining = r.U32();
    m.revision = r.U32();
    return m;
}

void CorpseSpawn::Write(wire::Writer& w) const
{
    w.U32(containerId);
    w.U16(playerId);
    w.Str(ClampUtf8(scene, kMaxSceneBytes));
    w.I32(x);
    w.I32(y);
    w.I32(z);
    w.U16(yaw);
}

CorpseSpawn CorpseSpawn::Read(wire::Reader& r)
{
    CorpseSpawn m;
    m.containerId = r.U32();
    m.playerId = r.U16();
    m.scene = ReadClampedString(r, kMaxSceneBytes);
    m.x = r.I32();
    m.y = r.I32();
    m.z = r.I32();
    m.yaw = r.U16();
    return m;
}

void CorpseDespawn::Write(wire::Writer& w) const
{
    w.U32(containerId);
}

CorpseDespawn CorpseDespawn::Read(wire::Reader& r)
{
    CorpseDespawn m;
    m.containerId = r.U32();
    return m;
}

void ItemDetail::Write(wire::Writer& w) const
{
    w.U32(itemUid);
    w.U32(durability);
    w.U32(maxDurability);
    w.U32(loadedAmmoItemId);
    w.U32(loadedAmmoCount);
}

ItemDetail ItemDetail::Read(wire::Reader& r)
{
    ItemDetail m;
    m.itemUid = r.U32();
    m.durability = r.U32();
    m.maxDurability = r.U32();
    m.loadedAmmoItemId = r.U32();
    m.loadedAmmoCount = r.U32();
    return m;
}

void ItemMods::Write(wire::Writer& w) const
{
    w.U32(itemUid);
    const std::size_t count = std::min<std::size_t>(mods.size(), kMaxItemMods);
    w.U16(static_cast<std::uint16_t>(count));
    for (std::size_t i = 0; i < count; ++i) {
        std::string key = mods[i].slotKey;
        if (key.size() > kMaxModSlotKeyBytes)
            key.resize(kMaxModSlotKeyBytes);
        w.Str(key);
        w.U32(mods[i].itemId);
    }
}

ItemMods ItemMods::Read(wire::Reader& r)
{
    ItemMods m;
    m.itemUid = r.U32();
    const std::uint16_t count = r.U16();
    const std::size_t stored = std::min<std::size_t>(count, kMaxItemMods);
    m.mods.reserve(stored);
    for (std::size_t i = 0; i < count; ++i) {
        ItemMod mod;
        mod.slotKey = r.Str();
        mod.itemId = r.U32();
        if (i < stored)
            m.mods.push_back(std::move(mod));
    }
    return m;
}

void ItemModDetach::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U32(parentUid);
    std::string key = slotKey;
    if (key.size() > kMaxModSlotKeyBytes)
        key.resize(kMaxModSlotKeyBytes);
    w.Str(key);
}

ItemModDetach ItemModDetach::Read(wire::Reader& r)
{
    ItemModDetach m;
    m.correlationId = r.U64();
    m.parentUid = r.U32();
    m.slotKey = r.Str();
    if (m.slotKey.size() > kMaxModSlotKeyBytes)
        m.slotKey.resize(kMaxModSlotKeyBytes);
    return m;
}

void ItemModDetachResult::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(code);
    w.U32(itemUid);
    w.U32(parentUid);
}

ItemModDetachResult ItemModDetachResult::Read(wire::Reader& r)
{
    ItemModDetachResult m;
    m.correlationId = r.U64();
    m.code = r.U8();
    m.itemUid = r.U32();
    m.parentUid = r.U32();
    return m;
}

void ItemModAttach::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U32(parentUid);
    w.U32(itemUid);
    std::string key = slotKey;
    if (key.size() > kMaxModSlotKeyBytes)
        key.resize(kMaxModSlotKeyBytes);
    w.Str(key);
}

ItemModAttach ItemModAttach::Read(wire::Reader& r)
{
    ItemModAttach m;
    m.correlationId = r.U64();
    m.parentUid = r.U32();
    m.itemUid = r.U32();
    m.slotKey = r.Str();
    if (m.slotKey.size() > kMaxModSlotKeyBytes)
        m.slotKey.resize(kMaxModSlotKeyBytes);
    return m;
}

void ItemModAttachResult::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(code);
    w.U32(parentUid);
    w.U32(itemUid);
    w.U32(itemId);
}

ItemModAttachResult ItemModAttachResult::Read(wire::Reader& r)
{
    ItemModAttachResult m;
    m.correlationId = r.U64();
    m.code = r.U8();
    m.parentUid = r.U32();
    m.itemUid = r.U32();
    m.itemId = r.U32();
    return m;
}

void ItemReload::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U32(gunUid);
    w.U32(ammoItemId);
    w.U32(count);
    w.U32(loadedAfter);
}

ItemReload ItemReload::Read(wire::Reader& r)
{
    ItemReload m;
    m.correlationId = r.U64();
    m.gunUid = r.U32();
    m.ammoItemId = r.U32();
    m.count = r.U32();
    m.loadedAfter = r.U32();
    return m;
}

void ItemReloadResult::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(code);
    w.U32(gunUid);
    w.U32(ammoItemId);
    w.U32(loadedCount);
    w.U32(backpackRevision);
}

ItemReloadResult ItemReloadResult::Read(wire::Reader& r)
{
    ItemReloadResult m;
    m.correlationId = r.U64();
    m.code = r.U8();
    m.gunUid = r.U32();
    m.ammoItemId = r.U32();
    m.loadedCount = r.U32();
    m.backpackRevision = r.U32();
    return m;
}

void ItemWeaponFire::Write(wire::Writer& w) const
{
    w.U32(gunUid);
    w.U32(ammoItemId);
    w.U32(loadedCount);
}

ItemWeaponFire ItemWeaponFire::Read(wire::Reader& r)
{
    ItemWeaponFire m;
    m.gunUid = r.U32();
    m.ammoItemId = r.U32();
    m.loadedCount = r.U32();
    return m;
}

void ItemDurability::Write(wire::Writer& w) const
{
    w.U32(itemUid);
    w.U32(durability);
    w.U32(maxDurability);
}

ItemDurability ItemDurability::Read(wire::Reader& r)
{
    ItemDurability m;
    m.itemUid = r.U32();
    m.durability = r.U32();
    m.maxDurability = r.U32();
    return m;
}

void BackpackCapacity::Write(wire::Writer& w) const
{
    w.U32(capacity);
}

BackpackCapacity BackpackCapacity::Read(wire::Reader& r)
{
    BackpackCapacity m;
    m.capacity = r.U32();
    return m;
}

void Respawn::Write(wire::Writer& w) const
{
    w.U16(playerId);
    w.I32(x);
    w.I32(y);
    w.I32(z);
    w.U16(yaw);
    w.U16(hp);
}

Respawn Respawn::Read(wire::Reader& r)
{
    Respawn m;
    m.playerId = r.U16();
    m.x = r.I32();
    m.y = r.I32();
    m.z = r.I32();
    m.yaw = r.U16();
    m.hp = r.U16();
    return m;
}

void ContainerOpen::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U32(containerId);
}

ContainerOpen ContainerOpen::Read(wire::Reader& r)
{
    ContainerOpen m;
    m.correlationId = r.U64();
    m.containerId = r.U32();
    return m;
}

void ContainerContents::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U32(containerId);
    w.U32(revision);
    w.U16(capacity);
    const std::uint16_t count = ClampCount(entries.size(), kMaxContainerEntries);
    w.U16(count);
    for (std::uint16_t i = 0; i < count; ++i)
        entries[i].Write(w);
}

ContainerContents ContainerContents::Read(wire::Reader& r)
{
    ContainerContents m;
    m.correlationId = r.U64();
    m.containerId = r.U32();
    m.revision = r.U32();
    m.capacity = r.U16();
    const std::uint16_t count = ReadClampedCount(r, kMaxContainerEntries);
    m.entries.reserve(count);
    for (std::uint16_t i = 0; i < count; ++i)
        m.entries.push_back(InventoryEntry::Read(r));
    return m;
}

void InventorySyncRequest::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(kind);
    w.U32(ownerId);
}

InventorySyncRequest InventorySyncRequest::Read(wire::Reader& r)
{
    InventorySyncRequest m;
    m.correlationId = r.U64();
    m.kind = r.U8();
    m.ownerId = r.U32();
    return m;
}

void InventoryState::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    snapshot.Write(w);
}

InventoryState InventoryState::Read(wire::Reader& r)
{
    InventoryState m;
    m.correlationId = r.U64();
    m.snapshot = InventorySnapshot::Read(r);
    return m;
}

void WorldItemInfo::Write(wire::Writer& w) const
{
    w.U32(worldItemId);
    w.U32(itemUid);
    w.U32(itemId);
    w.U32(count);
    w.Str(ClampUtf8(scene, kMaxSceneBytes));
    w.I32(x);
    w.I32(y);
    w.I32(z);
    w.U16(yaw);
    w.U8(source);
}

WorldItemInfo WorldItemInfo::Read(wire::Reader& r)
{
    WorldItemInfo m;
    m.worldItemId = r.U32();
    m.itemUid = r.U32();
    m.itemId = r.U32();
    m.count = r.U32();
    m.scene = ReadClampedString(r, kMaxSceneBytes);
    m.x = r.I32();
    m.y = r.I32();
    m.z = r.I32();
    m.yaw = r.U16();
    m.source = r.U8();
    return m;
}

void WorldItemPickup::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U32(worldItemId);
    w.U32(expectedItemUid);
    w.U32(expectedItemId);
    w.U32(count);
    w.U32(expectedBackpackRevision);
}

WorldItemPickup WorldItemPickup::Read(wire::Reader& r)
{
    WorldItemPickup m;
    m.correlationId = r.U64();
    m.worldItemId = r.U32();
    m.expectedItemUid = r.U32();
    m.expectedItemId = r.U32();
    m.count = r.U32();
    m.expectedBackpackRevision = r.U32();
    return m;
}

void WorldItemPickupResult::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(code);
    w.U32(itemUid);
    w.U32(backpackRevision);
}

WorldItemPickupResult WorldItemPickupResult::Read(wire::Reader& r)
{
    WorldItemPickupResult m;
    m.correlationId = r.U64();
    m.code = r.U8();
    m.itemUid = r.U32();
    m.backpackRevision = r.U32();
    return m;
}

void WorldItemDrop::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(sourceKind);
    w.U32(sourceId);
    w.U16(sourceSlot);
    w.U32(itemUid);
    w.U32(count);
    w.Str(ClampUtf8(scene, kMaxSceneBytes));
    w.I32(x);
    w.I32(y);
    w.I32(z);
    w.U16(yaw);
    w.U32(expectedRevision);
}

WorldItemDrop WorldItemDrop::Read(wire::Reader& r)
{
    WorldItemDrop m;
    m.correlationId = r.U64();
    m.sourceKind = r.U8();
    m.sourceId = r.U32();
    m.sourceSlot = r.U16();
    m.itemUid = r.U32();
    m.count = r.U32();
    m.scene = ReadClampedString(r, kMaxSceneBytes);
    m.x = r.I32();
    m.y = r.I32();
    m.z = r.I32();
    m.yaw = r.U16();
    m.expectedRevision = r.U32();
    return m;
}

void WorldItemDropResult::Write(wire::Writer& w) const
{
    w.U64(correlationId);
    w.U8(code);
    w.U32(worldItemId);
    w.U32(sourceRevision);
}

WorldItemDropResult WorldItemDropResult::Read(wire::Reader& r)
{
    WorldItemDropResult m;
    m.correlationId = r.U64();
    m.code = r.U8();
    m.worldItemId = r.U32();
    m.sourceRevision = r.U32();
    return m;
}

void WorldItemSpawn::Write(wire::Writer& w) const { info.Write(w); }

WorldItemSpawn WorldItemSpawn::Read(wire::Reader& r)
{
    WorldItemSpawn m;
    m.info = WorldItemInfo::Read(r);
    return m;
}

void WorldItemDespawn::Write(wire::Writer& w) const { w.U32(worldItemId); }

WorldItemDespawn WorldItemDespawn::Read(wire::Reader& r)
{
    WorldItemDespawn m;
    m.worldItemId = r.U32();
    return m;
}

void WorldItemSnapshotChunk::Write(wire::Writer& w) const
{
    w.U32(snapshotId);
    w.U16(chunkIndex);
    w.U16(chunkCount);
    const std::uint16_t count = ClampCount(items.size(), kMaxWorldItemChunkEntries);
    w.U16(count);
    for (std::uint16_t i = 0; i < count; ++i)
        items[i].Write(w);
}

WorldItemSnapshotChunk WorldItemSnapshotChunk::Read(wire::Reader& r)
{
    WorldItemSnapshotChunk m;
    m.snapshotId = r.U32();
    m.chunkIndex = r.U16();
    m.chunkCount = r.U16();
    const std::uint16_t count = ReadClampedCount(r, kMaxWorldItemChunkEntries);
    m.items.reserve(count);
    for (std::uint16_t i = 0; i < count; ++i)
        m.items.push_back(WorldItemInfo::Read(r));
    return m;
}

void SceneObjectState::Write(wire::Writer& w) const
{
    w.U32(key);
    w.U8(kind);
    w.U8(state);
    w.U32(raidEpoch);
}

SceneObjectState SceneObjectState::Read(wire::Reader& r)
{
    SceneObjectState m;
    m.key = r.U32();
    m.kind = r.U8();
    m.state = r.U8();
    m.raidEpoch = r.U32();
    return m;
}

void ExplosionEvent::Write(wire::Writer& w) const
{
    w.U32(explosionId);
    w.I32(x);
    w.I32(y);
    w.I32(z);
    w.U16(radiusCm);
    w.U16(damage);
    w.U32(weaponId);
    w.U8(sourceKind);
    w.U32(sourceId);
    w.U8(fxType);
    w.U32(raidEpoch);
}

ExplosionEvent ExplosionEvent::Read(wire::Reader& r)
{
    ExplosionEvent m;
    m.explosionId = r.U32();
    m.x = r.I32();
    m.y = r.I32();
    m.z = r.I32();
    m.radiusCm = r.U16();
    m.damage = r.U16();
    m.weaponId = r.U32();
    m.sourceKind = r.U8();
    m.sourceId = r.U32();
    m.fxType = r.U8();
    m.raidEpoch = r.U32();
    return m;
}

void ImpactFx::Write(wire::Writer& w) const
{
    w.U32(raidEpoch);
    w.U8(sourceKind);
    w.U32(sourceId);
    w.U32(weaponId);
    w.U32(shotId);
    w.I32(x);
    w.I32(y);
    w.I32(z);
    w.I16(nx);
    w.I16(ny);
    w.I16(nz);
    w.U8(flags);
}

ImpactFx ImpactFx::Read(wire::Reader& r)
{
    ImpactFx m;
    m.raidEpoch = r.U32();
    m.sourceKind = r.U8();
    m.sourceId = r.U32();
    m.weaponId = r.U32();
    m.shotId = r.U32();
    m.x = r.I32();
    m.y = r.I32();
    m.z = r.I32();
    m.nx = r.I16();
    m.ny = r.I16();
    m.nz = r.I16();
    m.flags = r.U8();
    return m;
}

std::vector<std::uint8_t> EncodeFrame(Op op, const wire::Header& header, const wire::Writer& payload)
{
    std::vector<std::uint8_t> out;
    out.reserve(wire::kHeaderSize + 1 + payload.Bytes().size());
    wire::WriteHeader(out, header);
    out.push_back(static_cast<std::uint8_t>(op));
    const auto& body = payload.Bytes();
    out.insert(out.end(), body.begin(), body.end());
    return out;
}

} // namespace coop::msg

