#pragma once

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#include "net/ReliableChannel.h"
#include "net/UdpSocket.h"
#include "protocol/Wire.h"
#include "world/World.h"

namespace coop {

struct GateConfig {
    std::uint16_t port = 9050;
    std::uint8_t tickRate = 20;
    std::size_t maxPlayers = 5;
    std::uint32_t sessionTimeoutMs = 60000; // design: 15s heartbeat, 60s expiry
    /// Host failover: when the current host has been silent for this long and another player
    /// is already joined, expire the host session early so the next-lowest player can take
    /// over without waiting the full session timeout. 0 disables the early expiry.
    std::uint32_t hostFailoverGraceMs = 30000;
    std::uint32_t pingIntervalMs = 1000;
    bool enforceContentHash = false;
    std::array<std::uint8_t, 32> contentHash{}; // ADR-020 option B: monsterCatalogHash
    /// Public-deployment access control: when non-empty, only these account names may log in.
    /// Empty keeps the open behaviour used by local/LAN tests.
    std::vector<std::string> allowedAccounts;
};

/// Gate: protocol boundary. Owns sockets + sessions + reliability, never business rules.
class Gate {
public:
    Gate(World& world, GateConfig config);
    ~Gate();

    bool Start(std::string& error);
    void Stop();

    /// Drain inbound queue, dispatch, then broadcast snapshot.
    void Tick(std::int32_t tick);

    std::size_t SessionCount() const { return _sessions.size(); }
    std::size_t JoinedCount() const;

    // ---- test/probe seams (no sockets required) --------------------------------

    /// Routes outbound frames here instead of the UDP socket.
    using PacketSink = std::function<void(const net::Endpoint&, const std::uint8_t*, std::size_t)>;
    void SetPacketSink(PacketSink sink) { _packetSink = std::move(sink); }

    /// Queues one datagram as if it had arrived from `from`; drained by Tick().
    void InjectPacket(const net::Endpoint& from, const std::uint8_t* data, std::size_t size);

    /// N2 observability: frames dropped because the header version is foreign.
    std::uint64_t HeaderDropCount() const { return _headerDrops; }
    /// N2 observability: CLogin rejected by the client version check.
    std::uint64_t VersionRejectCount() const { return _versionRejects; }
    /// A1 observability: packets dropped by the session fence.
    std::uint64_t FencedDropCount() const { return _fencedDrops; }

private:
    enum class SessionState : std::uint8_t {
        Active,
        Superseded, // A1: another session owns the identity; packets are fenced
        Rejected,   // N2: failed the version handshake, only flushing the SLogin
    };

    struct Session {
        Session();

        std::uint32_t sessionId = 0; // fencing token carried in every frame header
        std::uint64_t identity = 0;  // 0 until CLogin succeeds
        SessionState state = SessionState::Active;
        std::uint64_t closeAtMs = 0; // superseded/rejected: retransmit until then, then erase

        std::uint16_t playerId = 0; // assigned on login; survives a top-account
        std::uint16_t seat = 0;
        std::string token;
        std::string account;
        net::Endpoint endpoint;
        std::uint64_t lastSeenMs = 0;
        std::uint64_t lastPingMs = 0;
        bool joined = false;
        bool spawnAssigned = false; // SSpawnAssign already sent for this raid
        /// M2-v12: this non-host player is ready for the next raid.
        bool ready = false;
        /// M2-v12 ready gate: this session reported CPlayerSpawn for the current raid.
        bool spawnReported = false;
        /// v9: SInitialInventory was sent for the current join/raid (reset on CStartRaid).
        bool initialInventorySent = false;
        /// v9: the client answered with CBackpackCapacity, i.e. it applied the inventory.
        bool initialInventoryAcked = false;
        /// Retry time for a lost SInitialInventory (0 = nothing pending).
        std::uint64_t initialInventoryRetryAtMs = 0;
        int initialInventoryRetries = 0;
        /// v9: SWorldItemSnapshot chunks were sent for the current join/raid.
        bool worldItemSnapshotSent = false;
        Entity entity = kInvalidEntity;

        net::ReliableChannel control;  // channel 3: login / join / kick
        net::ReliableChannel commands; // channel 1: commands
        net::ReliableChannel input;    // channel 0: unreliable

        // B4: the authoritative stash mirror the client must persist before spawning
        std::uint64_t mirrorVersion = 0;
        bool mirrorSent = false;
        bool mirrorAcked = false;
    };

    struct Inbound {
        net::Endpoint from;
        std::vector<std::uint8_t> data;
    };

    void ReceiveLoop();
    void HandlePacket(const net::Endpoint& from, const std::uint8_t* data, std::size_t size);

    void HandleControl(Session& session, Op op, const std::vector<std::uint8_t>& payload);
    void HandleCommand(Session& session, Op op, const std::vector<std::uint8_t>& payload);

    void SendControl(Session& session, Op op, const wire::Writer& payload);
    void SendCommands(Session& session, Op op, const wire::Writer& payload);
    void SendMirror(Session& session);
    void SendLoadout(Session& session, std::uint16_t playerId, std::uint64_t correlationId, std::uint8_t code);
    void SendOutbound(const Outbound& message);
    void SendPing(Session& session, std::uint64_t nowMs);
    void SendRaw(const net::Endpoint& endpoint, const std::uint8_t* data, std::size_t size);

    void BroadcastSnapshot(std::int32_t tick);
    void ExpireSessions();
    void ReapClosedSessions(std::uint64_t nowMs);

    /// M2-v12: host is the smallest joined playerId; only it may start/reset a raid.
    std::uint16_t MinJoinedPlayerId() const;
    bool IsHost(const Session& session) const;
    bool AllNonHostPlayersReady(std::uint16_t hostPlayerId) const;
    void SendPlayerReadyState(Session& recipient, std::uint16_t playerId, bool ready);
    void BroadcastPlayerReadyStates();
    void ResetPlayerReadyStates();
    void ResetSessionRaidFlags();
    void SendRaidControlResult(Session& session, msg::RaidControlAction action, ErrorCode code);

    Session& GetOrCreateSession(const net::Endpoint& from);

    // A1: identity -> the single active session; everything else is fenced.
    Session* FindActiveSession(std::uint64_t identity);
    bool FenceAllows(const Session& session, const wire::Header& header, Op op);
    void RejectLogin(Session& session, ErrorCode code);
    void SupersedeSession(Session& session, bool notify);

    World& _world;
    GateConfig _config;
    net::UdpSocket _socket;

    std::unordered_map<std::uint64_t, Session> _sessions;
    std::unordered_map<std::uint64_t, std::uint32_t> _activeSessionByIdentity;
    std::uint16_t _nextPlayerId = 1;
    bool _hasSpawnAnchor = false;
    msg::SpawnAssign _spawnAnchor;
    /// M2-v12 ready gate: the raid starts once every joined player reported its spawn (or
    /// 12 s after the first report). Starting on the first report put one player in the world
    /// while the owner was still loading, which froze every AI replica for the others.
    bool _raidBegun = false;
    std::uint64_t _spawnGateAtMs = 0;
    /// CStartRaid was accepted but BeginRaid has not run yet (clients are still loading).
    bool _raidStartPending = false;
    std::uint64_t _raidStartPendingAtMs = 0;

    PacketSink _packetSink;
    std::uint64_t _headerDrops = 0;
    std::uint64_t _versionRejects = 0;
    std::uint64_t _fencedDrops = 0;
    std::uint64_t _lastVersionDropLogMs = 0;
    std::uint64_t _lastFenceDropLogMs = 0;
    std::uint64_t _lastLoginRejectLogMs = 0;

    std::mutex _queueMutex;
    std::deque<Inbound> _inbound;

    std::atomic<bool> _running{false};
    std::thread _receiveThread;
};

} // namespace coop
