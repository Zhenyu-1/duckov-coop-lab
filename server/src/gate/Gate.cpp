#include "gate/Gate.h"

#include <algorithm>
#include <chrono>
#include <cstring>
#include <functional>
#include <iomanip>
#include <random>
#include <sstream>

#include "core/Log.h"
#include "protocol/Messages.h"
#include "protocol/Opcodes.h"

namespace coop {

namespace {

constexpr std::size_t kMaxDatagram = 2048;

/// How long a superseded/rejected session keeps retransmitting its final SLogin
/// (the "flush" in ADR-007/ADR-008) before the record is dropped.
constexpr std::uint64_t kCloseWindowMs = 1000;

std::uint64_t NowMs()
{
    using namespace std::chrono;
    return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
}

std::string MakeToken()
{
    static std::mt19937_64 rng{std::random_device{}()};
    std::uniform_int_distribution<std::uint64_t> dist;
    std::ostringstream out;
    out << std::hex << std::setw(16) << std::setfill('0') << dist(rng);
    return out.str();
}

/// A1: the per-connection fencing token. Random so a re-login never reuses the
/// previous session id (the old packets must not be accepted by the new session).
std::uint32_t MakeSessionId()
{
    static std::mt19937 rng{std::random_device{}()};
    std::uniform_int_distribution<std::uint32_t> dist(1, 0xFFFFFFFFu);
    return dist(rng);
}

/// Identity is a hash; logs keep only the tail so it is correlatable but not a key.
std::string MaskIdentity(std::uint64_t identity)
{
    if (identity == 0)
        return "anon";
    std::ostringstream out;
    out << "..." << std::hex << std::setw(6) << std::setfill('0') << (identity & 0xFFFFFFu);
    return out.str();
}

std::string IdText(std::uint32_t value)
{
    std::ostringstream out;
    out << std::hex << value;
    return out.str();
}

} // namespace

Gate::Session::Session()
    : control(3, true, true)
    , commands(1, true, false)
    , input(0, false, false)
{
}

Gate::Gate(World& world, GateConfig config)
    : _world(world), _config(config)
{
}

Gate::~Gate()
{
    Stop();
}

bool Gate::Start(std::string& error)
{
    if (!_socket.Bind(_config.port, error))
        return false;

    _running = true;
    _receiveThread = std::thread([this] { ReceiveLoop(); });

    Log::Info("gate listening on udp/" + std::to_string(_socket.LocalPort()) +
              " tick=" + std::to_string(_config.tickRate) +
              " maxPlayers=" + std::to_string(_config.maxPlayers));
    return true;
}

void Gate::Stop()
{
    if (!_running.exchange(false))
        return;

    if (_receiveThread.joinable())
        _receiveThread.join();

    _socket.Close();
}

std::size_t Gate::JoinedCount() const
{
    std::size_t n = 0;
    for (const auto& [key, session] : _sessions)
        if (session.state == SessionState::Active && session.joined) ++n;
    return n;
}

std::uint16_t Gate::MinJoinedPlayerId() const
{
    std::uint16_t minId = 0;
    for (const auto& [key, session] : _sessions) {
        (void)key;
        if (session.state != SessionState::Active || !session.joined || session.playerId == 0)
            continue;
        if (minId == 0 || session.playerId < minId)
            minId = session.playerId;
    }
    return minId;
}

bool Gate::IsHost(const Session& session) const
{
    const std::uint16_t hostId = MinJoinedPlayerId();
    return hostId != 0 && session.joined && session.playerId == hostId;
}

bool Gate::AllNonHostPlayersReady(std::uint16_t hostPlayerId) const
{
    for (const auto& [key, session] : _sessions) {
        (void)key;
        if (session.state != SessionState::Active || !session.joined)
            continue;
        if (session.playerId == hostPlayerId)
            continue;
        if (!session.ready)
            return false;
    }
    return true;
}

void Gate::SendPlayerReadyState(Session& recipient, std::uint16_t playerId, bool ready)
{
    if (recipient.state != SessionState::Active || !recipient.joined)
        return;

    msg::PlayerReadyState state;
    state.playerId = playerId;
    state.ready = ready;

    wire::Writer writer;
    state.Write(writer);
    SendControl(recipient, Op::SPlayerReady, writer);
}

void Gate::BroadcastPlayerReadyStates()
{
    for (auto& [recipientKey, recipient] : _sessions) {
        (void)recipientKey;
        if (recipient.state != SessionState::Active || !recipient.joined)
            continue;
        for (const auto& [subjectKey, subject] : _sessions) {
            (void)subjectKey;
            if (subject.state != SessionState::Active || !subject.joined)
                continue;
            SendPlayerReadyState(recipient, subject.playerId, subject.ready);
        }
    }
}

void Gate::ResetPlayerReadyStates()
{
    for (auto& [key, session] : _sessions) {
        (void)key;
        session.ready = false;
    }
}

void Gate::ResetSessionRaidFlags()
{
    _hasSpawnAnchor = false;
    _raidBegun = false;
    _spawnGateAtMs = 0;
    for (auto& [key, other] : _sessions) {
        (void)key;
        other.spawnAssigned = false;
        other.spawnReported = false;
        other.initialInventorySent = false;
        other.initialInventoryAcked = false;
        other.initialInventoryRetryAtMs = 0;
        other.initialInventoryRetries = 0;
        other.worldItemSnapshotSent = false;
    }
}

void Gate::SendRaidControlResult(Session& session, msg::RaidControlAction action, ErrorCode code)
{
    if (session.state != SessionState::Active || !session.joined)
        return;

    msg::RaidControlResult result;
    result.action = static_cast<std::uint8_t>(action);
    result.code = static_cast<std::uint8_t>(code);
    result.playerId = session.playerId;

    wire::Writer writer;
    result.Write(writer);
    SendControl(session, Op::SRaidControlResult, writer);
}

void Gate::InjectPacket(const net::Endpoint& from, const std::uint8_t* data, std::size_t size)
{
    if (data == nullptr || size == 0)
        return;

    Inbound packet;
    packet.from = from;
    packet.data.assign(data, data + size);

    std::lock_guard<std::mutex> lock(_queueMutex);
    _inbound.push_back(std::move(packet));
}

void Gate::SendRaw(const net::Endpoint& endpoint, const std::uint8_t* data, std::size_t size)
{
    if (_packetSink) {
        _packetSink(endpoint, data, size);
        return;
    }
    _socket.SendTo(endpoint, data, size);
}

void Gate::ReceiveLoop()
{
    std::uint8_t buffer[kMaxDatagram];

    while (_running.load(std::memory_order_relaxed)) {
        net::Endpoint from;
        const int received = _socket.RecvFrom(buffer, sizeof(buffer), from, 100);
        if (received <= 0)
            continue;

        Inbound packet;
        packet.from = from;
        packet.data.assign(buffer, buffer + received);

        std::lock_guard<std::mutex> lock(_queueMutex);
        _inbound.push_back(std::move(packet));
    }
}

void Gate::Tick(std::int32_t tick)
{
    const std::uint64_t now = NowMs();

    std::deque<Inbound> packets;
    {
        std::lock_guard<std::mutex> lock(_queueMutex);
        packets.swap(_inbound);
    }

    for (const auto& packet : packets)
        HandlePacket(packet.from, packet.data.data(), packet.data.size());

    // fixed 8-phase world pipeline
    _world.Tick(tick);
    for (const auto& event : _world.TakeEvents()) {
        Log::Count("world_event_" + std::to_string(static_cast<int>(event.type)));
        if (event.type == EventType::PlayerDied || event.type == EventType::AiDied)
            Log::Debug("entity died: " + std::to_string(event.entity));
    }

    for (const auto& outbound : _world.TakeOutbound())
        SendOutbound(outbound);

    // retransmissions + heartbeat. A superseded/rejected session only flushes its final
    // SLogin (channel 3) until the close window elapses; nothing else is sent to it.
    for (auto& [key, session] : _sessions) {
        const bool flushing = session.state != SessionState::Active;
        if (flushing && now >= session.closeAtMs)
            continue;

        std::vector<std::vector<std::uint8_t>> resends;
        session.control.CollectRetransmits(session.sessionId, now, resends);

        if (!flushing) {
            // A one-shot SInitialInventory can be lost while the command channel is still
            // settling after login/scene load. Resend until the client acks with its backpack
            // capacity request (or give up after a few attempts).
            if (session.joined && session.initialInventoryRetryAtMs != 0 &&
                now >= session.initialInventoryRetryAtMs) {
                if (session.initialInventoryAcked || session.initialInventoryRetries >= 3) {
                    session.initialInventoryRetryAtMs = 0;
                } else {
                    ++session.initialInventoryRetries;
                    session.initialInventoryRetryAtMs = now + 1200;
                    _world.SendInitialInventory(session.playerId);
                    Log::Info("initial inventory resent: player=" +
                              std::to_string(session.playerId) + " attempt=" +
                              std::to_string(session.initialInventoryRetries) +
                              " acked=" + std::to_string(session.initialInventoryAcked ? 1 : 0));
                }
            }
            session.commands.CollectRetransmits(session.sessionId, now, resends);
            if (now - session.lastPingMs >= _config.pingIntervalMs) {
                session.lastPingMs = now;
                SendPing(session, now);
            }
        }

        for (const auto& frame : resends)
            SendRaw(session.endpoint, frame.data(), frame.size());
    }

    ExpireSessions();
    ReapClosedSessions(now);
    // A start that never produced a spawn (every client dropped during scene load) must not
    // pin the lobby forever. 30 s is well past the 12 s party gate.
    if (_raidStartPending && !_world.RaidStarted() &&
        (JoinedCount() == 0 || now - _raidStartPendingAtMs > 30000)) {
        _raidStartPending = false;
        Log::Warn("raid start pending cleared: no player reached BeginRaid");
    }
    BroadcastSnapshot(tick);
}

Gate::Session& Gate::GetOrCreateSession(const net::Endpoint& from)
{
    const std::uint64_t key = from.Key();
    auto it = _sessions.find(key);
    if (it != _sessions.end()) {
        it->second.lastSeenMs = NowMs();
        return it->second;
    }

    Session session;
    session.sessionId = MakeSessionId();
    while (session.sessionId == 0 || std::any_of(_sessions.begin(), _sessions.end(),
                                                 [&](const auto& other) {
                                                     return other.second.sessionId == session.sessionId;
                                                 }))
        session.sessionId = MakeSessionId();

    session.token = MakeToken();
    session.endpoint = from;
    session.lastSeenMs = NowMs();

    auto [inserted, ok] = _sessions.emplace(key, std::move(session));
    (void)ok;
    return inserted->second;
}

bool Gate::FenceAllows(const Session& session, const wire::Header& header, Op op)
{
    // Login-level messages run their own checks (they may revive a rejected session or
    // answer a reconnect); everything else must come from the single active session.
    if (op == Op::CLogin || op == Op::CResume)
        return true;

    const auto reject = [&]() {
        ++_fencedDrops;
        Log::Count("gate_fence_drop");

        const std::uint64_t now = NowMs();
        if (now - _lastFenceDropLogMs >= 1000) {
            _lastFenceDropLogMs = now;
            Log::Warn("fenced packet dropped: op=" + std::to_string(static_cast<int>(op)) +
                      " session=0x" + IdText(session.sessionId) +
                      " identity=" + MaskIdentity(session.identity) +
                      " drops=" + std::to_string(_fencedDrops));
        }
        return false;
    };

    if (session.state != SessionState::Active)
        return reject();

    // A stale fencing token (earlier login on this endpoint) is never accepted.
    if (header.sessionId != session.sessionId)
        return reject();

    if (session.identity != 0) {
        const auto active = _activeSessionByIdentity.find(session.identity);
        if (active == _activeSessionByIdentity.end() || active->second != session.sessionId)
            return reject();
    }

    return true;
}

Gate::Session* Gate::FindActiveSession(std::uint64_t identity)
{
    if (identity == 0)
        return nullptr;

    const auto active = _activeSessionByIdentity.find(identity);
    if (active == _activeSessionByIdentity.end())
        return nullptr;

    for (auto& [key, session] : _sessions) {
        if (session.sessionId == active->second && session.state == SessionState::Active)
            return &session;
    }
    return nullptr;
}

void Gate::SupersedeSession(Session& session, bool notify)
{
    if (notify) {
        // ADR-008 order: the old client gets the code first, then it stops being active.
        msg::LoginResult result;
        result.code = static_cast<std::uint8_t>(ErrorCode::SessionReplaced);
        result.token.clear();
        result.expiresAtMs = static_cast<std::int64_t>(NowMs());

        wire::Writer writer;
        result.Write(writer);
        SendControl(session, Op::SLogin, writer);
    }

    if (session.identity != 0) {
        const auto active = _activeSessionByIdentity.find(session.identity);
        if (active != _activeSessionByIdentity.end() && active->second == session.sessionId)
            _activeSessionByIdentity.erase(active);
    }

    session.state = SessionState::Superseded;
    session.closeAtMs = NowMs() + kCloseWindowMs;
    session.joined = false; // the world entity now belongs to the new session
    session.mirrorSent = false;
    session.mirrorAcked = false;
    session.mirrorVersion = 0;
}

void Gate::RejectLogin(Session& session, ErrorCode code)
{
    msg::LoginResult result;
    result.code = static_cast<std::uint8_t>(code);
    result.token.clear();
    result.expiresAtMs = static_cast<std::int64_t>(NowMs());

    wire::Writer writer;
    result.Write(writer);
    SendControl(session, Op::SLogin, writer);

    if (session.joined) {
        // A live joined session cannot be downgraded by a later bad CLogin: keep playing,
        // only tell the caller why the re-login was refused.
        Log::Warn("login rejected after join: keeping the live session player=" +
                  std::to_string(session.playerId));
        return;
    }

    session.state = SessionState::Rejected;
    session.closeAtMs = NowMs() + kCloseWindowMs;
    session.identity = 0;
}

void Gate::ReapClosedSessions(std::uint64_t nowMs)
{
    for (auto it = _sessions.begin(); it != _sessions.end();) {
        if (it->second.state != SessionState::Active && nowMs >= it->second.closeAtMs) {
            it = _sessions.erase(it);
            continue;
        }
        ++it;
    }
}

void Gate::HandlePacket(const net::Endpoint& from, const std::uint8_t* data, std::size_t size)
{
    const std::uint64_t now = NowMs();

    // N2/ADR-007 layer 1: the layout version is a connection-level guard. A foreign
    // version never reaches a session record, let alone CLogin.
    if (size >= wire::kHeaderSize && data[0] != wire::kProtocolVersion) {
        ++_headerDrops;
        Log::Count("gate_header_version_drop");

        if (now - _lastVersionDropLogMs >= 1000) {
            _lastVersionDropLogMs = now;
            Log::Warn("frame dropped: version=" + std::to_string(data[0]) +
                      " expected=" + std::to_string(wire::kProtocolVersion) +
                      " drops=" + std::to_string(_headerDrops) + " from=" + from.ToString());
        }
        return;
    }

    wire::Header header;
    if (!wire::ReadHeader(data, size, header)) {
        Log::Debug("drop: bad header from " + from.ToString());
        return;
    }

    if (header.type == wire::FrameType::Pong) {
        // heartbeat ack: refresh the session, it carries no payload we need
        const auto it = _sessions.find(from.Key());
        if (it != _sessions.end())
            it->second.lastSeenMs = now;
        return;
    }

    if (header.type == wire::FrameType::Ping) {
        // reply with Pong carrying the same client ticks
        wire::Reader reader(data + wire::kHeaderSize, size - wire::kHeaderSize);
        const std::uint64_t clientTicks = reader.U64();
        wire::Writer writer;
        writer.U64(clientTicks);
        wire::Header reply;
        reply.type = wire::FrameType::Pong;
        reply.sessionId = header.sessionId;
        reply.seq = 0;

        std::vector<std::uint8_t> frame;
        wire::WriteHeader(frame, reply);
        const auto& body = writer.Bytes();
        frame.insert(frame.end(), body.begin(), body.end());

        Session& session = GetOrCreateSession(from);
        SendRaw(session.endpoint, frame.data(), frame.size());
        return;
    }

    if (header.type != wire::FrameType::Data || size < wire::kHeaderSize + 1)
        return;

    const auto op = static_cast<Op>(data[wire::kHeaderSize]);
    const std::uint8_t* payload = data + wire::kHeaderSize + 1;
    const std::size_t payloadSize = size - wire::kHeaderSize - 1;

    Session& session = GetOrCreateSession(from);
    if (!FenceAllows(session, header, op))
        return;

    std::vector<net::Delivered> delivered;

    switch (header.channel) {
    case 3:
        session.control.OnFrame(op, header, payload, payloadSize, now, delivered);
        for (const auto& message : delivered)
            HandleControl(session, message.op, message.payload);
        break;
    case 1:
        session.commands.OnFrame(op, header, payload, payloadSize, now, delivered);
        for (const auto& message : delivered)
            HandleCommand(session, message.op, message.payload);
        break;
    case 0:
        session.input.OnFrame(op, header, payload, payloadSize, now, delivered);
        for (const auto& message : delivered)
            HandleCommand(session, message.op, message.payload);
        break;
    default:
        Log::Debug("drop: unknown channel " + std::to_string(header.channel));
        break;
    }
}

void Gate::HandleControl(Session& session, Op op, const std::vector<std::uint8_t>& payload)
{
    switch (op) {
    case Op::CLogin: {
        wire::Reader reader(payload.data(), payload.size());
        const auto request = msg::LoginRequest::Read(reader);
        if (!reader.Ok()) {
            Log::Warn("login: malformed payload");
            return;
        }

        // N2/ADR-007 layer 2: the contract version is checked before any identity,
        // entity, stash or mirror state is touched.
        const auto version = wire::ClassifyClientVersion(request.clientVersion, wire::kClientVersion);
        if (version != wire::ClientVersionStatus::Ok) {
            ++_versionRejects;
            Log::Count("gate_login_version_reject");

            const std::uint64_t nowMs = NowMs();
            if (nowMs - _lastLoginRejectLogMs >= 1000) {
                _lastLoginRejectLogMs = nowMs;
                Log::Warn("login rejected: client=" + request.clientVersion +
                          " server=" + std::string(wire::kClientVersion) +
                          " direction=" + wire::ClientVersionStatusText(version) +
                          " rejects=" + std::to_string(_versionRejects) +
                          " from=" + session.endpoint.ToString());
            }
            RejectLogin(session, ErrorCode::VersionMismatch);
            return;
        }

        if (_config.enforceContentHash &&
            std::memcmp(request.contentHash.data(), _config.contentHash.data(),
                        _config.contentHash.size()) != 0) {
            Log::Warn("login rejected: content hash mismatch from " + session.endpoint.ToString());
            RejectLogin(session, ErrorCode::ContentMismatch);
            return;
        }

        if (!_config.allowedAccounts.empty()) {
            bool allowed = false;
            for (const auto& account : _config.allowedAccounts) {
                if (account == request.account) {
                    allowed = true;
                    break;
                }
            }
            if (!allowed) {
                Log::Warn("login rejected: account '" + request.account +
                          "' is not in --allow-accounts");
                RejectLogin(session, ErrorCode::Unauthorized);
                return;
            }
        }

        const std::uint64_t identity = std::hash<std::string>{}(request.account);
        Session* previous = FindActiveSession(identity);
        if (previous == &session)
            previous = nullptr;

        // A1/ADR-008: a second login for the same identity tops the old session. The
        // entity is reused - no EndRaid, no settlement, no stash rewrite.
        bool replaced = false;
        std::uint32_t previousSessionId = 0;
        bool previousInRaid = false;
        if (previous != nullptr) {
            const std::uint16_t previousPlayerId = previous->playerId;
            const std::uint16_t previousSeat = previous->seat;
            const Entity previousEntity = previous->entity;
            const bool previousJoined = previous->joined;
            const std::string previousToken = previous->token;
            previousInRaid = _world.IsInRaid(previousPlayerId);
            previousSessionId = previous->sessionId;

            SupersedeSession(*previous, /*notify=*/true); // code first, then the switch

            session.playerId = previousPlayerId;
            session.seat = previousSeat;
            session.entity = previousEntity;
            session.joined = previousJoined;
            session.spawnAssigned = previous->spawnAssigned;
            session.spawnReported = previous->spawnReported;
            session.ready = previous->ready;
            session.token = previousToken; // the identity keeps its resume token
            session.mirrorSent = false;    // the new session mirrors + acks again
            session.mirrorAcked = false;
            session.mirrorVersion = 0;

            // the new client restarts its input sequence at 1; drop the old session's
            // stale-sequence guard so its first input is not rejected (A1)
            _world.ResetInputBaseline(session.playerId);
            replaced = true;
        } else if (session.playerId == 0) {
            session.playerId = _nextPlayerId++;
        }

        // an in-process re-login with another account must not keep the old identity bound
        if (session.identity != 0 && session.identity != identity) {
            const auto stale = _activeSessionByIdentity.find(session.identity);
            if (stale != _activeSessionByIdentity.end() && stale->second == session.sessionId)
                _activeSessionByIdentity.erase(stale);
            Log::Warn("login: session 0x" + IdText(session.sessionId) + " switched identity " +
                      MaskIdentity(session.identity) + " -> " + MaskIdentity(identity));
        }

        session.state = SessionState::Active;
        session.closeAtMs = 0;
        session.identity = identity;
        session.account = request.account;
        _activeSessionByIdentity[identity] = session.sessionId;

        if (replaced) {
            Log::Info("session replaced: identity=" + MaskIdentity(identity) +
                      " old=0x" + IdText(previousSessionId) + " new=0x" + IdText(session.sessionId) +
                      " player=" + std::to_string(session.playerId) +
                      " inRaid=" + std::to_string(previousInRaid ? 1 : 0) +
                      " mirrorResend=" + std::to_string(session.joined ? 1 : 0));
        } else {
            Log::Info("login ok: player " + std::to_string(session.playerId) +
                      " identity=" + MaskIdentity(identity) +
                      " client=" + request.clientVersion + " from=" + session.endpoint.ToString());
        }

        msg::LoginResult result;
        result.code = 0;
        result.token = session.token;
        result.expiresAtMs = static_cast<std::int64_t>(NowMs()) + 24LL * 3600 * 1000;

        wire::Writer writer;
        result.Write(writer);
        SendControl(session, Op::SLogin, writer);
        break;
    }
    case Op::CJoin: {
        wire::Reader reader(payload.data(), payload.size());
        const auto request = msg::JoinRequest::Read(reader);
        if (!reader.Ok())
            return;

        msg::JoinResult result;
        if (request.token != session.token) {
            result.code = static_cast<std::uint8_t>(ErrorCode::Unauthorized);
            Log::Warn("join denied: bad token from " + session.endpoint.ToString());
        } else if (!session.joined) {
            if (JoinedCount() >= _config.maxPlayers) {
                result.code = static_cast<std::uint8_t>(ErrorCode::RoomFull);
                Log::Warn("join denied: room full");
            } else {
                session.entity = _world.AddPlayer(session.playerId, session.endpoint.Key(),
                                                  session.identity);
                session.seat = static_cast<std::uint16_t>(JoinedCount());
                session.joined = true;
                session.ready = false;
                session.initialInventorySent = false;
                session.worldItemSnapshotSent = false;
                _world.ReopenRaid();
                result.code = 0;
                result.playerId = session.playerId;
                result.raidId = 1;
                result.sceneId = "raid_test";
                result.startTick = 0;
                result.tickRate = _config.tickRate;
                Log::Info("join ok: player " + std::to_string(session.playerId) +
                          " from " + session.endpoint.ToString());
            }
        } else {
            result.code = 0;
            result.playerId = session.playerId;
            result.raidId = 1;
            result.sceneId = "raid_test";
            result.tickRate = _config.tickRate;
        }

        wire::Writer writer;
        result.Write(writer);
        SendControl(session, Op::SJoin, writer);

        if (result.code == 0) {
            // B4: hand the client the authoritative stash mirror; it must ack before spawn
            if (!session.mirrorSent || !session.mirrorAcked)
                SendMirror(session);

            // B2: tell the joiner about the loadouts everyone already reported
            for (auto& [key, other] : _sessions) {
                if (other.joined)
                    SendLoadout(session, other.playerId, 0, 0);
            }

            // M2-v12: the new player starts unready; everyone (including the host UI)
            // receives the current ready matrix.
            BroadcastPlayerReadyStates();

            // late join: replay alive AI (normal for a player joining mid-raid)
            _world.ReplayActiveAi(session.playerId);
        }
        break;
    }
    case Op::CStartRaid: {
        if (!session.joined)
            return;

        wire::Reader reader(payload.data(), payload.size());
        auto request = msg::RaidStart::Read(reader);
        if (!reader.Ok() || request.sceneName.empty()) {
            Log::Warn("raid start: malformed payload");
            return;
        }

        // M2-v12: host-only. Host = smallest joined playerId; readiness excludes the host.
        if (!IsHost(session)) {
            Log::Warn("raid start rejected: player " + std::to_string(session.playerId) +
                      " is not host");
            SendRaidControlResult(session, msg::RaidControlAction::StartRaid,
                                  ErrorCode::Unauthorized);
            return;
        }
        if ((_world.RaidStarted() || _raidStartPending) && _world.RaidPhase() <= 2) {
            Log::Warn("raid start rejected: raid already running phase=" +
                      std::to_string(static_cast<int>(_world.RaidPhase())));
            SendRaidControlResult(session, msg::RaidControlAction::StartRaid, ErrorCode::Conflict);
            return;
        }
        if (!AllNonHostPlayersReady(session.playerId)) {
            Log::Warn("raid start rejected: not every non-host player is ready");
            SendRaidControlResult(session, msg::RaidControlAction::StartRaid, ErrorCode::Conflict);
            return;
        }

        Log::Info("raid start requested by player " + std::to_string(session.playerId) +
                  ": scene=" + request.sceneName + " index=" + std::to_string(request.sceneIndex));

        _hasSpawnAnchor = false;
        _raidBegun = false;
        _spawnGateAtMs = 0;
        for (auto& [key, other] : _sessions) {
            other.spawnAssigned = false;
            other.spawnReported = false;
        }

        _world.EndRaid(); // pre-start: never carry the previous raid over
        _world.ResetInputBaselines();
        _world.ResetAiSpawn();
        _raidStartPending = true;
        _raidStartPendingAtMs = NowMs();

        // stamp the new raid epoch so stale spawn reports can be rejected
        request.raidEpoch = _world.RaidEpoch();
        Log::Info("raid epoch -> " + std::to_string(request.raidEpoch));

        wire::Writer writer;
        request.Write(writer);
        for (auto& [key, other] : _sessions) {
            if (other.joined) {
                // T3: a new raid reseeds inventories (BeginRaid); the next spawn sends a
                // fresh SInitialInventory for the new state.
                other.initialInventorySent = false;
                other.initialInventoryAcked = false;
                other.initialInventoryRetryAtMs = 0;
                other.initialInventoryRetries = 0;
                other.worldItemSnapshotSent = false;
                SendControl(other, Op::SRaidStart, writer);
            }
        }

        // A raid consumes the ready gate; the next one needs a fresh toggle from every
        // non-host player. Broadcast after SRaidStart so clients apply the clear in order.
        ResetPlayerReadyStates();
        BroadcastPlayerReadyStates();
        break;
    }
    case Op::CPlayerReady: {
        if (!session.joined)
            return;

        wire::Reader reader(payload.data(), payload.size());
        const auto request = msg::PlayerReady::Read(reader);
        if (!reader.Ok())
            return;

        session.ready = request.ready;
        Log::Info("player ready: player=" + std::to_string(session.playerId) +
                  " ready=" + (session.ready ? "1" : "0") +
                  " host=" + (IsHost(session) ? "1" : "0"));
        BroadcastPlayerReadyStates();
        break;
    }
    case Op::CForceResetRaid: {
        if (!session.joined)
            return;

        if (!IsHost(session)) {
            Log::Warn("force reset rejected: player " + std::to_string(session.playerId) +
                      " is not host");
            SendRaidControlResult(session, msg::RaidControlAction::ForceReset,
                                  ErrorCode::Unauthorized);
            return;
        }
        if (!_world.RaidStarted() && !_raidStartPending) {
            Log::Warn("force reset rejected: no raid is running");
            SendRaidControlResult(session, msg::RaidControlAction::ForceReset,
                                  ErrorCode::NotInRaid);
            return;
        }

        Log::Info("force reset by host player " + std::to_string(session.playerId) +
                  " epoch=" + std::to_string(_world.RaidEpoch()));

        // EndRaid() destroys all AI, clears scenes/participants, advances the epoch and
        // broadcasts RaidPhase(1). The dedicated SForceResetRaid then tells every client to
        // leave the raid scene for the base.
        _world.EndRaid();
        ResetSessionRaidFlags();
        ResetPlayerReadyStates();
        _raidStartPending = false;
        _raidStartPendingAtMs = 0;

        msg::ForceResetRaidState reset;
        reset.byPlayerId = session.playerId;
        wire::Writer resetWriter;
        reset.Write(resetWriter);
        for (auto& [key, other] : _sessions) {
            if (other.state == SessionState::Active && other.joined)
                SendControl(other, Op::SForceResetRaid, resetWriter);
        }

        SendRaidControlResult(session, msg::RaidControlAction::ForceReset, ErrorCode::Ok);
        BroadcastPlayerReadyStates();
        break;
    }
    case Op::CResume: {
        wire::Reader reader(payload.data(), payload.size());
        const auto request = msg::ResumeRequest::Read(reader);
        if (!reader.Ok() || request.token.empty())
            return;

        // A superseded session already handed the identity over: the old token can no
        // longer resume it (A1/ADR-008), it only gets to flush its SLogin(9).
        if (session.state != SessionState::Active) {
            ++_fencedDrops;
            Log::Count("gate_fence_drop");
            Log::Warn("resume denied: session 0x" + IdText(session.sessionId) +
                      " is no longer active (player=" + std::to_string(session.playerId) +
                      " drops=" + std::to_string(_fencedDrops) + ")");
            return;
        }

        msg::ResumeResult result;
        const std::uint64_t nowMs = NowMs();

        const auto answerFromSession = [&](std::uint16_t playerId) {
            // only an active raid participant is pulled back into the raid; everyone
            // else resumes in the lobby with no scene teleport
            result.playerId = playerId;
            if (_world.IsInRaid(playerId)) {
                _world.GetPlayerPosition(playerId, result.x, result.y, result.z);
                result.sceneName = _world.PlayerScene(playerId);
                result.raidEpoch = _world.RaidEpoch();
            }
        };

        Session* previous = nullptr;
        for (auto& [key, other] : _sessions) {
            if (&other == &session)
                continue;
            if (other.state != SessionState::Active || !other.joined)
                continue;
            if (other.token == request.token &&
                nowMs - other.lastSeenMs <= _config.sessionTimeoutMs) {
                previous = &other;
                break;
            }
        }

        if (session.joined && request.token == session.token) {
            // A1: a top-account login inherits the identity token, so the new session
            // resumes exactly the bind it just took over.
            answerFromSession(session.playerId);
            result.code = 0;
            Log::Info("session resumed in place: player " + std::to_string(session.playerId) +
                      " from " + session.endpoint.ToString() +
                      (result.sceneName.empty()
                           ? std::string(" (lobby)")
                           : (" scene=" + result.sceneName + " epoch=" + std::to_string(result.raidEpoch))));
        } else if (previous != nullptr) {
            const std::uint16_t previousPlayerId = previous->playerId;
            const std::uint16_t previousSeat = previous->seat;
            const std::uint64_t previousIdentity = previous->identity;
            const std::string previousToken = previous->token;
            const std::string previousAccount = previous->account;
            const Entity previousEntity = previous->entity;

            answerFromSession(previousPlayerId);

            // reconnect, not a top-account: move the bind over without an extra SLogin
            SupersedeSession(*previous, /*notify=*/false);

            session.playerId = previousPlayerId;
            session.seat = previousSeat;
            session.identity = previousIdentity;
            session.account = previousAccount;
            session.token = previousToken;
            session.entity = previousEntity;
            session.joined = true;
            session.lastSeenMs = nowMs;
            session.mirrorSent = false;
            session.mirrorAcked = false;
            session.mirrorVersion = 0;
            if (previousIdentity != 0)
                _activeSessionByIdentity[previousIdentity] = session.sessionId;

            // a restarted client starts its input sequence over: drop the old guard
            _world.ResetInputBaseline(previousPlayerId);

            result.code = 0;
            Log::Info("session resumed: player " + std::to_string(previousPlayerId) +
                      " from " + session.endpoint.ToString() +
                      (result.sceneName.empty()
                           ? std::string(" (lobby)")
                           : (" scene=" + result.sceneName + " epoch=" + std::to_string(result.raidEpoch))));
        } else {
            result.code = 1;
            Log::Info("session resume failed from " + session.endpoint.ToString());
        }

        wire::Writer writer;
        result.Write(writer);
        SendControl(session, Op::SResume, writer);

        if (result.code == 0) {
            SendMirror(session);
            for (auto& [key, other] : _sessions) {
                if (other.joined)
                    SendLoadout(session, other.playerId, 0, 0);
            }
            // late join / reconnect: replay alive AI before normal state updates
            _world.ReplayActiveAi(session.playerId);
            // T3: a reconnected client lost its inventory view — resend it once here.
            _world.SendInitialInventory(session.playerId);
            session.initialInventorySent = true;
            session.initialInventoryAcked = false;
            session.initialInventoryRetries = 0;
            session.initialInventoryRetryAtMs = NowMs() + 1200;
            _world.SendWorldItemSnapshot(session.playerId);
            _world.SendCorpseSnapshot(session.playerId);
            session.worldItemSnapshotSent = true;
        }
        break;
    }
    case Op::CPlayerSpawn: {
        if (!session.joined)
            return;

        wire::Reader reader(payload.data(), payload.size());
        const auto report = msg::PlayerSpawn::Read(reader);
        if (!reader.Ok())
            return;

        // spawn reports from the base scene are meaningless for raid AI
        if (report.sceneName.rfind("Level_", 0) != 0)
            return;

        // B4: the client must have written and acknowledged the stash mirror first
        if (session.mirrorSent && !session.mirrorAcked) {
            Log::Warn("spawn rejected: mirror not acked for player " + std::to_string(session.playerId));
            return;
        }

        // trust the client's post-teleport position, but only for the running raid and
        // only for players that are still in it
        if (!_world.SetPlayerPosition(session.entity, report.x, report.y, report.z, report.sceneName,
                                      report.raidEpoch))
            return;

        session.spawnReported = true;

        if (!_hasSpawnAnchor) {
            _hasSpawnAnchor = true;
            _spawnAnchor.x = report.x;
            _spawnAnchor.y = report.y;
            _spawnAnchor.z = report.z;
            _spawnGateAtMs = NowMs();

            Log::Info("spawn anchor from player " + std::to_string(session.playerId) + ": " +
                      std::to_string(report.x) + "," + std::to_string(report.y) + "," + std::to_string(report.z) +
                      " scene=" + report.sceneName + " epoch=" + std::to_string(report.raidEpoch));
        }

        if (!_raidBegun) {
            // M2-v12 ready gate: wait for the whole party (12 s cap). Starting on the first
            // report put one player in the world while the other was still loading: if the
            // owner was the slow one, nobody simulated the AI and every client saw frozen
            // replicas; if the late player landed next to a pack, every AI retargeted to it.
            std::size_t reported = 0;
            std::size_t joined = 0;
            bool allReported = true;
            for (const auto& [key, other] : _sessions) {
                if (!other.joined || other.state != SessionState::Active)
                    continue;
                ++joined;
                if (other.spawnReported)
                    ++reported;
                else
                    allReported = false;
            }
            const bool timedOut = NowMs() >= _spawnGateAtMs + 12000;
            if (!allReported && !timedOut) {
                Log::Info("spawn gate: waiting for the party, reported=" + std::to_string(reported) +
                          "/" + std::to_string(joined));
                return;
            }
            if (!allReported)
                Log::Info("spawn gate: timeout after 12s, starting with reported=" +
                          std::to_string(reported) + "/" + std::to_string(joined));

            _raidBegun = true;
            _raidStartPending = false;
            _raidStartPendingAtMs = 0;
            _world.ResetInputBaselines();

            // The owner must be a player that is actually in the raid. Otherwise no client
            // simulates the AI and the others see an empty map.
            std::uint16_t ownerPlayerId = 0;
            for (const auto& [key, other] : _sessions) {
                if (!other.joined || !other.spawnReported)
                    continue;
                if (ownerPlayerId == 0 || other.playerId < ownerPlayerId)
                    ownerPlayerId = other.playerId;
            }

            _world.BeginRaid(_spawnAnchor.x, _spawnAnchor.y, _spawnAnchor.z, ownerPlayerId);
            Log::Info("raid begun: owner=" + std::to_string(ownerPlayerId) +
                      " scene=" + report.sceneName);

            for (auto& [key, other] : _sessions) {
                if (!other.joined || !other.spawnReported)
                    continue; // still loading: its own report takes the late-join path
                msg::SpawnAssign assign;
                std::int32_t x = 0, y = 0, z = 0;
                std::uint16_t yaw = 0;
                if (_world.PickPlayerSpawn(report.sceneName, other.seat, x, y, z, yaw)) {
                    assign.x = x;
                    assign.y = y;
                    assign.z = z;
                    assign.seat = 0; // coordinates are already per-player
                } else {
                    assign = _spawnAnchor;
                    assign.seat = other.seat;
                }
                wire::Writer writer;
                assign.Write(writer);
                SendControl(other, Op::SSpawnAssign, writer);
                // The server commanded this teleport: move its authoritative actor now
                // so later AI→player hit validation uses the assigned point.
                _world.SetPlayerTeleportTarget(other.playerId, assign.x, assign.y, assign.z);
                other.spawnAssigned = true;
                _world.ProtectPlayer(other.playerId, 100);
                Log::Info("spawn assign player " + std::to_string(other.playerId) +
                          " seat=" + std::to_string(other.seat) + ": " +
                          std::to_string(assign.x) + "," + std::to_string(assign.y) + "," +
                          std::to_string(assign.z) + " source=" +
                          (assign.seat == 0 ? "table" : "anchor"));
            }

            // BeginRaid seeded every inventory from the ticket; players who reported before
            // the gate opened must be sent that seeded state now (their handler returned
            // early while waiting, so the tail of this function never ran for them).
            for (auto& [key, other] : _sessions) {
                if (!other.joined || !other.spawnReported)
                    continue;
                if (!other.initialInventorySent) {
                    _world.SendInitialInventory(other.playerId);
                    other.initialInventorySent = true;
                    other.initialInventoryAcked = false;
                    other.initialInventoryRetries = 0;
                    other.initialInventoryRetryAtMs = NowMs() + 1200;
                }
                if (!other.worldItemSnapshotSent) {
                    _world.SendWorldItemSnapshot(other.playerId);
                    _world.SendCorpseSnapshot(other.playerId);
                    other.worldItemSnapshotSent = true;
                }
            }
        } else if (!session.spawnAssigned) {
            // Late join: assign this player its own authored point instead of the
            // first player's anchor. Existing players keep their assignment.
            msg::SpawnAssign assign;
            std::int32_t x = 0, y = 0, z = 0;
            std::uint16_t yaw = 0;
            if (_world.PickPlayerSpawn(report.sceneName, session.seat, x, y, z, yaw)) {
                assign.x = x;
                assign.y = y;
                assign.z = z;
                assign.seat = 0;
            } else {
                assign = _spawnAnchor;
                assign.seat = session.seat;
            }
            wire::Writer writer;
            assign.Write(writer);
            SendControl(session, Op::SSpawnAssign, writer);
            _world.SetPlayerTeleportTarget(session.playerId, assign.x, assign.y, assign.z);
            session.spawnAssigned = true;
            _world.ProtectPlayer(session.playerId, 100);
            Log::Info("late spawn assign player " + std::to_string(session.playerId) +
                      " seat=" + std::to_string(session.seat) + ": " +
                      std::to_string(assign.x) + "," + std::to_string(assign.y) + "," +
                      std::to_string(assign.z));
        } else {
            // A repeated CPlayerSpawn (retry / trusted teleport report) updates the
            // position above but must not teleport the player back to StartPoint.
            Log::Info("spawn report accepted without reassign: player " +
                      std::to_string(session.playerId) + " pos=" +
                      std::to_string(report.x) + "," + std::to_string(report.y) + "," +
                      std::to_string(report.z));
        }

        // T3: first successful spawn of this join/raid sends the authoritative inventory once.
        if (!session.initialInventorySent) {
            _world.SendInitialInventory(session.playerId);
            session.initialInventorySent = true;
            session.initialInventoryAcked = false;
            session.initialInventoryRetries = 0;
            session.initialInventoryRetryAtMs = NowMs() + 1200;
        }
        // T3B: the late-join ground-item snapshot is also sent once per join/raid.
        if (!session.worldItemSnapshotSent) {
            _world.SendWorldItemSnapshot(session.playerId);
            _world.SendCorpseSnapshot(session.playerId);
            session.worldItemSnapshotSent = true;
        }

        break;
    }
    case Op::CLoadout: {
        if (!session.joined)
            return;

        wire::Reader reader(payload.data(), payload.size());
        const auto request = msg::LoadoutRequest::Read(reader);
        if (!reader.Ok())
            return;

        const bool accepted = _world.SetLoadout(session.playerId, request.entries);
        if (!accepted)
            Log::Warn("loadout rejected: player " + std::to_string(session.playerId));

        msg::LoadoutState state;
        state.correlationId = request.correlationId;
        state.code = accepted ? 0 : static_cast<std::uint8_t>(ErrorCode::InvalidArgument);
        state.playerId = session.playerId;
        _world.GetLoadout(session.playerId, state.entries);

        wire::Writer writer;
        state.Write(writer);
        for (auto& [key, other] : _sessions) {
            if (other.joined)
                SendControl(other, Op::SLoadout, writer);
        }

        Log::Info("loadout from player " + std::to_string(session.playerId) +
                  ": accepted=" + std::to_string(accepted) + " slots=" +
                  std::to_string(state.entries.size()));
        break;
    }
    case Op::CUseConsumable: {
        if (!session.joined)
            return;
        wire::Reader reader(payload.data(), payload.size());
        const auto use = msg::UseConsumable::Read(reader);
        if (reader.Ok())
            _world.SubmitUseConsumable(session.playerId, use);
        break;
    }
    case Op::CLeaveRaid: {
        if (session.joined) {
            _world.LeaveRaid(session.playerId);
            Log::Info("player left raid: " + std::to_string(session.playerId));
        }
        break;
    }
    case Op::CMirrorAck: {
        wire::Reader reader(payload.data(), payload.size());
        const auto ack = msg::MirrorAck::Read(reader);
        if (!reader.Ok())
            return;

        if (!session.mirrorSent || ack.version != session.mirrorVersion) {
            Log::Warn("mirror ack ignored: player " + std::to_string(session.playerId) +
                      " version=" + std::to_string(ack.version));
            return;
        }

        session.mirrorAcked = true;
        Log::Info("mirror acked: player " + std::to_string(session.playerId) +
                  " version=" + std::to_string(ack.version));
        break;
    }
    default:
        Log::Debug("unhandled control op " + std::to_string(static_cast<int>(op)));
        break;
    }
}

void Gate::HandleCommand(Session& session, Op op, const std::vector<std::uint8_t>& payload)
{
    if (!session.joined)
        return;

    // v9 item contract: all validation/state changes live in World (authoritative ledger).
    if (op == Op::CItemTransfer) {
        wire::Reader reader(payload.data(), payload.size());
        const auto transfer = msg::ItemTransfer::Read(reader);
        if (reader.Ok())
            _world.SubmitItemTransfer(session.playerId, transfer);
        return;
    }

    if (op == Op::CItemArrange) {
        wire::Reader reader(payload.data(), payload.size());
        const auto arrange = msg::ItemArrange::Read(reader);
        if (reader.Ok())
            _world.SubmitItemArrange(session.playerId, arrange);
        return;
    }

    if (op == Op::CItemConsume) {
        wire::Reader reader(payload.data(), payload.size());
        const auto consume = msg::ItemConsume::Read(reader);
        if (reader.Ok())
            _world.SubmitItemConsume(session.playerId, consume);
        return;
    }

    if (op == Op::CItemReload) {
        wire::Reader reader(payload.data(), payload.size());
        const auto reload = msg::ItemReload::Read(reader);
        if (reader.Ok())
            _world.SubmitItemReload(session.playerId, reload);
        return;
    }

    if (op == Op::CItemWeaponFire) {
        wire::Reader reader(payload.data(), payload.size());
        const auto fire = msg::ItemWeaponFire::Read(reader);
        if (reader.Ok())
            _world.SubmitItemWeaponFire(session.playerId, fire);
        return;
    }

    if (op == Op::CItemDurability) {
        wire::Reader reader(payload.data(), payload.size());
        const auto durability = msg::ItemDurability::Read(reader);
        if (reader.Ok())
            _world.SubmitItemDurability(session.playerId, durability);
        return;
    }

    if (op == Op::CItemModDetach) {
        wire::Reader reader(payload.data(), payload.size());
        const auto detach = msg::ItemModDetach::Read(reader);
        if (reader.Ok())
            _world.SubmitItemModDetach(session.playerId, detach);
        return;
    }

    if (op == Op::CItemModAttach) {
        wire::Reader reader(payload.data(), payload.size());
        const auto attach = msg::ItemModAttach::Read(reader);
        if (reader.Ok())
            _world.SubmitItemModAttach(session.playerId, attach);
        return;
    }

    if (op == Op::CBackpackCapacity) {
        wire::Reader reader(payload.data(), payload.size());
        const auto capacity = msg::BackpackCapacity::Read(reader);
        if (reader.Ok()) {
            // The client asks for the backpack capacity only after it applied the initial
            // inventory; use it as the ack that stops the SInitialInventory retry.
            session.initialInventoryAcked = true;
            _world.SubmitBackpackCapacity(session.playerId, capacity);
        }
        return;
    }

    if (op == Op::CSceneObjectState) {
        wire::Reader reader(payload.data(), payload.size());
        const auto state = msg::SceneObjectState::Read(reader);
        if (reader.Ok())
            _world.SubmitSceneObjectState(session.playerId, state);
        return;
    }

    if (op == Op::CExplosion) {
        wire::Reader reader(payload.data(), payload.size());
        const auto explosion = msg::ExplosionEvent::Read(reader);
        if (reader.Ok())
            _world.SubmitExplosion(session.playerId, explosion);
        return;
    }

    if (op == Op::CImpactFx) {
        wire::Reader reader(payload.data(), payload.size());
        const auto impact = msg::ImpactFx::Read(reader);
        if (reader.Ok() && reader.Remaining() == 0)
            _world.SubmitImpactFx(session.playerId, impact);
        return;
    }

    if (op == Op::CInventorySync) {
        wire::Reader reader(payload.data(), payload.size());
        const auto request = msg::InventorySyncRequest::Read(reader);
        if (reader.Ok())
            _world.SubmitInventorySync(session.playerId, request);
        return;
    }

    if (op == Op::CContainerOpen) {
        wire::Reader reader(payload.data(), payload.size());
        const auto request = msg::ContainerOpen::Read(reader);
        if (reader.Ok())
            _world.SubmitContainerOpen(session.playerId, request);
        return;
    }

    if (op == Op::CWorldItemPickup) {
        wire::Reader reader(payload.data(), payload.size());
        const auto request = msg::WorldItemPickup::Read(reader);
        if (reader.Ok())
            _world.SubmitWorldItemPickup(session.playerId, request);
        return;
    }

    if (op == Op::CWorldItemDrop) {
        wire::Reader reader(payload.data(), payload.size());
        const auto request = msg::WorldItemDrop::Read(reader);
        if (reader.Ok())
            _world.SubmitWorldItemDrop(session.playerId, request);
        return;
    }

    if (op == Op::CAiState) {
        wire::Reader reader(payload.data(), payload.size());
        const auto state = msg::AiState::Read(reader);
        if (reader.Ok())
            _world.SubmitAiState(session.playerId, state);
        return;
    }

    if (op == Op::CHitEvent) {
        wire::Reader reader(payload.data(), payload.size());
        const auto hit = msg::HitEvent::Read(reader);
        if (reader.Ok())
            _world.SubmitHitEvent(session.playerId, hit);
        return;
    }

    if (op == Op::CShootFact) {
        wire::Reader reader(payload.data(), payload.size());
        const auto fact = msg::ShootFact::Read(reader);
        if (reader.Ok())
            _world.SubmitShootFact(session.playerId, fact);
        return;
    }

    if (op == Op::CAiAnim) {
        wire::Reader reader(payload.data(), payload.size());
        const auto anim = msg::AiAnim::Read(reader);
        if (reader.Ok())
            _world.SubmitAiAnim(session.playerId, anim);
        return;
    }

    if (op == Op::CAiAim) {
        wire::Reader reader(payload.data(), payload.size());
        const auto aim = msg::AiAim::Read(reader);
        if (reader.Ok())
            _world.SubmitAiAim(session.playerId, aim);
        return;
    }

    if (op == Op::CAiStateSample) {
        wire::Reader reader(payload.data(), payload.size());
        const auto sample = msg::AiStateSample::Read(reader);
        if (reader.Ok())
            _world.SubmitAiStateSample(session.playerId, sample);
        return;
    }

    if (op == Op::CAiShot) {
        wire::Reader reader(payload.data(), payload.size());
        const auto shot = msg::AiShot::Read(reader);
        if (reader.Ok())
            _world.SubmitAiShot(session.playerId, shot);
        return;
    }

    if (op == Op::CAiLoadoutV7) {
        wire::Reader reader(payload.data(), payload.size());
        const auto loadout = msg::AiLoadoutV7::Read(reader);
        if (reader.Ok())
            _world.SubmitAiLoadoutV7(session.playerId, loadout);
        return;
    }

    if (op == Op::CPlayerStateSample) {
        wire::Reader reader(payload.data(), payload.size());
        const auto sample = msg::PlayerStateSample::Read(reader);
        if (reader.Ok())
            _world.SubmitPlayerStateSample(session.playerId, sample);
        return;
    }

    if (op == Op::CPlayerShot) {
        wire::Reader reader(payload.data(), payload.size());
        const auto shot = msg::PlayerShot::Read(reader);
        if (reader.Ok())
            _world.SubmitPlayerShot(session.playerId, shot);
        return;
    }

    if (op == Op::CPlayerLoadoutV8) {
        wire::Reader reader(payload.data(), payload.size());
        const auto loadout = msg::PlayerLoadoutV8::Read(reader);
        if (reader.Ok())
            _world.SubmitPlayerLoadoutV8(session.playerId, loadout);
        return;
    }

    if (op == Op::CPlayerFaceV8) {
        wire::Reader reader(payload.data(), payload.size());
        const auto face = msg::PlayerFaceV8::Read(reader);
        if (reader.Ok())
            _world.SubmitPlayerFaceV8(session.playerId, face);
        return;
    }

    if (op == Op::CPlayerStats) {
        wire::Reader reader(payload.data(), payload.size());
        const auto stats = msg::PlayerStats::Read(reader);
        if (!reader.Ok())
            return;
        _world.SetPlayerStats(session.playerId, stats);
        Log::Info("player stats updated: player " + std::to_string(session.playerId) +
                  " gunDamage=" + std::to_string(stats.gunDamageMultiplier) +
                  " gunCritGain=" + std::to_string(stats.gunCritRateGain) +
                  " armor=" + std::to_string(stats.bodyArmor) + "/" +
                  std::to_string(stats.headArmor));
        return;
    }

    if (op == Op::CAiLoadout) {
        wire::Reader reader(payload.data(), payload.size());
        const auto loadout = msg::AiLoadout::Read(reader);
        if (!reader.Ok())
            return;
        if (!_world.SetAiLoadout(session.playerId, loadout)) {
            Log::Warn("ai loadout rejected: player " + std::to_string(session.playerId) +
                      " ai=" + std::to_string(loadout.aiId) +
                      " epoch=" + std::to_string(loadout.raidEpoch));
            return;
        }
        Log::Info("ai loadout: ai=" + std::to_string(loadout.aiId) +
                  " player=" + std::to_string(session.playerId) +
                  " weapon=" + std::to_string(loadout.weaponId) +
                  " bullet=" + std::to_string(loadout.bulletId) +
                  " armor=" + std::to_string(loadout.armorId) +
                  "/" + std::to_string(loadout.helmetId));
        return;
    }

    if (op == Op::CHitReport) {
        wire::Reader reader(payload.data(), payload.size());
        const auto report = msg::HitReport::Read(reader);
        if (reader.Ok())
            _world.SubmitHit(session.entity, session.playerId, report);
        return;
    }

    if (op == Op::CLoot) {
        wire::Reader reader(payload.data(), payload.size());
        const auto request = msg::LootRequest::Read(reader);
        if (reader.Ok())
            _world.SubmitLoot(session.entity, session.playerId, request);
        return;
    }

    if (op == Op::CExtract) {
        wire::Reader reader(payload.data(), payload.size());
        const auto request = msg::ExtractRequest::Read(reader);
        if (reader.Ok())
            _world.SubmitExtract(session.entity, session.playerId, request);
        return;
    }

    if (op == Op::CContainerView) {
        wire::Reader reader(payload.data(), payload.size());
        const auto request = msg::ContainerViewRequest::Read(reader);
        if (reader.Ok())
            _world.SubmitContainerView(session.playerId, request);
        return;
    }

    if (op != Op::CInput)
        return;

    wire::Reader reader(payload.data(), payload.size());
    const auto input = msg::Input::Read(reader);
    if (!reader.Ok())
        return;

    // validation happens inside the world tick (phase 2), not here
    _world.SubmitInput(session.entity, input);
}

void Gate::SendControl(Session& session, Op op, const wire::Writer& payload)
{
    const auto frame = session.control.BuildFrame(op, payload.Bytes(), session.sessionId, NowMs());
    SendRaw(session.endpoint, frame.data(), frame.size());
}

void Gate::SendCommands(Session& session, Op op, const wire::Writer& payload)
{
    const auto frame = session.commands.BuildFrame(op, payload.Bytes(), session.sessionId, NowMs());
    SendRaw(session.endpoint, frame.data(), frame.size());
}

void Gate::SendMirror(Session& session)
{
    if (!session.joined)
        return;

    msg::MirrorState mirror;
    mirror.version = _world.StashVersion();
    mirror.playerId = session.playerId;
    for (const auto& item : _world.GetStoredItems(session.playerId))
        mirror.entries.push_back(msg::MirrorEntry{item.itemId, item.count});

    session.mirrorVersion = mirror.version;
    session.mirrorSent = true;
    session.mirrorAcked = false;

    wire::Writer writer;
    mirror.Write(writer);
    SendControl(session, Op::SMirror, writer);

    Log::Info("mirror sent: player " + std::to_string(session.playerId) +
              " version=" + std::to_string(mirror.version) +
              " items=" + std::to_string(mirror.entries.size()));
}

void Gate::SendLoadout(Session& session, std::uint16_t playerId, std::uint64_t correlationId,
                       std::uint8_t code)
{
    msg::LoadoutState state;
    state.correlationId = correlationId;
    state.code = code;
    state.playerId = playerId;
    _world.GetLoadout(playerId, state.entries);

    wire::Writer writer;
    state.Write(writer);
    SendControl(session, Op::SLoadout, writer);
}

void Gate::SendOutbound(const Outbound& message)
{
    const std::uint64_t now = NowMs();
    auto buildFrame = [&](Session& session) {
        return message.channel == 0
                   ? session.input.BuildFrame(message.op, message.payload, session.sessionId, now)
                   : session.commands.BuildFrame(message.op, message.payload, session.sessionId,
                                                 now);
    };

    if (message.playerId == 0) {
        for (auto& [key, session] : _sessions) {
            if (session.state != SessionState::Active || !session.joined)
                continue;
            const auto frame = buildFrame(session);
            SendRaw(session.endpoint, frame.data(), frame.size());
        }
        return;
    }

    for (auto& [key, session] : _sessions) {
        if (session.state != SessionState::Active || !session.joined ||
            session.playerId != message.playerId)
            continue;
        const auto frame = buildFrame(session);
        SendRaw(session.endpoint, frame.data(), frame.size());
        return;
    }
}

void Gate::SendPing(Session& session, std::uint64_t nowMs)
{
    wire::Header header;
    header.type = wire::FrameType::Ping;
    header.sessionId = session.sessionId;

    wire::Writer writer;
    writer.U64(nowMs);

    std::vector<std::uint8_t> frame;
    wire::WriteHeader(frame, header);
    const auto& body = writer.Bytes();
    frame.insert(frame.end(), body.begin(), body.end());

    SendRaw(session.endpoint, frame.data(), frame.size());
}

void Gate::BroadcastSnapshot(std::int32_t tick)
{
    (void)tick;
    if (_sessions.empty())
        return;

    wire::Writer writer;
    _world.LastSnapshot().Write(writer);

    for (auto& [key, session] : _sessions) {
        if (session.state != SessionState::Active || !session.joined)
            continue;

        // snapshot goes through the unreliable channel so it never blocks on retransmits
        auto frame = session.input.BuildFrame(Op::SSnapshot, writer.Bytes(), session.sessionId, NowMs());
        SendRaw(session.endpoint, frame.data(), frame.size());
    }
}

void Gate::ExpireSessions()
{
    const std::uint64_t now = NowMs();
    const std::uint16_t hostId = MinJoinedPlayerId();
    const bool hasSuccessor = JoinedCount() > 1;
    // Host migration is a special case: a crashed host must not keep the lobby blocked for
    // the full 60 s resume window. Once a successor is joined, a silent host is expired
    // after the shorter grace, which reassigns AI and lets the client-side host election
    // move to the next-lowest player. A solo host keeps the normal timeout so a slow load
    // or a brief disconnect can still resume.
    const bool earlyHostFailover =
        _config.hostFailoverGraceMs > 0 && _config.hostFailoverGraceMs < _config.sessionTimeoutMs;

    for (auto it = _sessions.begin(); it != _sessions.end();) {
        Session& session = it->second;

        // superseded/rejected records are reaped by ReapClosedSessions, never here
        if (session.state != SessionState::Active) {
            ++it;
            continue;
        }

        const bool isHost = hasSuccessor && session.joined && session.playerId != 0 &&
                            session.playerId == hostId;
        const std::uint64_t timeout =
            isHost && earlyHostFailover ? _config.hostFailoverGraceMs : _config.sessionTimeoutMs;
        if (now - session.lastSeenMs <= timeout) {
            ++it;
            continue;
        }

        const bool wasHost = isHost;
        if (session.joined) {
            if (wasHost && earlyHostFailover) {
                Log::Info("host heartbeat lost: player " + std::to_string(session.playerId) +
                          " silent for " + std::to_string(now - session.lastSeenMs) +
                          "ms (grace " + std::to_string(_config.hostFailoverGraceMs) +
                          "ms), expiring early for failover");
            }
            _world.OnPlayerDisconnected(session.playerId);
            _world.RemovePlayer(session.entity);
            Log::Info("session expired: player " + std::to_string(session.playerId) +
                      (wasHost ? " (host)" : ""));
        }

        if (session.identity != 0) {
            const auto active = _activeSessionByIdentity.find(session.identity);
            if (active != _activeSessionByIdentity.end() && active->second == session.sessionId)
                _activeSessionByIdentity.erase(active);
        }

        it = _sessions.erase(it);

        if (wasHost) {
            const std::uint16_t nextHost = MinJoinedPlayerId();
            Log::Info("host failover: " +
                      (nextHost != 0 ? "player " + std::to_string(nextHost) : std::string("no successor")));
        }
    }
}

} // namespace coop
