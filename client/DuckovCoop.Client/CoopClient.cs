using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DuckovCoop.Client
{
    public enum ClientState
    {
        Idle,
        LoggingIn,
        Joining,
        InGame,
        Failed,
    }

    /// <summary>
    /// UDP client for the C++ gate: login 鈫?join 鈫?input, with the same reliable channels.
    /// Single-threaded: call Login/Join then Tick() in a loop.
    /// </summary>
    public sealed class CoopClient : IDisposable
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private UdpClient _udp;
        private readonly IPEndPoint _server;
        private ReliableChannel _control = new ReliableChannel(3, true, true);
        private ReliableChannel _commands = new ReliableChannel(1, true, false);
        private ReliableChannel _input = new ReliableChannel(0, false, false);
        private readonly List<byte[]> _retransmits = new List<byte[]>();
        private readonly List<Delivered> _delivered = new List<Delivered>();
        private readonly Dictionary<uint, ContainerView> _views = new Dictionary<uint, ContainerView>();

        private uint _sessionId;
        private long _lastLoginSentMs;
        private long _lastJoinSentMs;
        private long _lastSocketRecreateMs;
        private string _resumeToken = string.Empty;
        private long _lastTrafficMs;
        private long _lastReceiveMs;
        private string _account = string.Empty;
        private string _version = Protocol.BotClientVersion;

        /// <summary>32-byte catalogue hash sent in CLogin; the Unity mod sets this from its bundled content.</summary>
        public byte[] ContentHash { get; set; } = new byte[32];

        public CoopClient(string host, int port)
        {
            _server = new IPEndPoint(IPAddress.Parse(host), port);
            _udp = new UdpClient();
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        }

        public ClientState State { get; private set; } = ClientState.Idle;
        public ushort PlayerId { get; private set; }
        public string Token { get; private set; } = string.Empty;
        public Snapshot LatestSnapshot { get; private set; }
        public LootResult LastLoot { get; private set; }
        public ExtractResult LastExtract { get; private set; }
        /// <summary>M2-v12: server-authoritative ready state keyed by playerId.</summary>
        public readonly Dictionary<ushort, bool> PlayerReady = new Dictionary<ushort, bool>();
        public PlayerReadyState LastPlayerReadyState { get; private set; }
        public int PlayerReadyCount { get; private set; }
        public ForceResetRaidState LastForceReset { get; private set; }
        public int ForceResetCount { get; private set; }
        public RaidControlResult LastRaidControlResult { get; private set; }
        public int RaidControlResultCount { get; private set; }
        /// <summary>Local receive order for AI spawn/despawn records (owner migration rebuild).</summary>
        public readonly Dictionary<uint, uint> AiSpawnOrder = new Dictionary<uint, uint>();
        public readonly Dictionary<uint, uint> AiDespawnOrder = new Dictionary<uint, uint>();
        private uint _aiMessageOrder;
        public byte RaidPhase { get; private set; }
        public ContainerView LastContainerView { get; private set; }
        public string RequestedScene { get; private set; } = string.Empty;
        public int SceneLoadRequests { get; private set; }
        public SpawnAssign SpawnAnchor { get; private set; }
        public int SpawnAssignCount { get; private set; }
        public CombatEvent LastCombatEvent { get; private set; }
        public CombatEventV4 LastCombatEventV4 { get; private set; }
        /// <summary>
        /// Every v4 combat event since the consumer last drained it. A single server tick can
        /// carry several events (an explosion damages several victims), and keeping only the
        /// last one made clients miss their own authoritative HP update.
        /// </summary>
        public readonly Queue<CombatEventV4> CombatEventV4Queue = new Queue<CombatEventV4>();
        public int CombatEventCount { get; private set; }
        public int CombatEventV4Count { get; private set; }
        public readonly Dictionary<uint, AiAnim> AiAnims = new Dictionary<uint, AiAnim>();
        public readonly Dictionary<uint, AiHealth> AiHealths = new Dictionary<uint, AiHealth>();
        public int AiAnimCount { get; private set; }
        public int AiHealthCount { get; private set; }
        public readonly Dictionary<uint, AiState> AiStates = new Dictionary<uint, AiState>();
        public int AiStateCount { get; private set; }
        public readonly Dictionary<uint, AiSpawn> AiSpawns = new Dictionary<uint, AiSpawn>();
        public readonly Dictionary<uint, AiLoadout> AiLoadouts = new Dictionary<uint, AiLoadout>();
        public readonly Dictionary<uint, AiAim> AiAims = new Dictionary<uint, AiAim>();
        public readonly Dictionary<uint, AiStateSample> AiStateSamples = new Dictionary<uint, AiStateSample>();
        public readonly Dictionary<uint, AiLoadoutV7> AiLoadoutSnapshots = new Dictionary<uint, AiLoadoutV7>();
        public readonly Dictionary<uint, AiShot> AiShots = new Dictionary<uint, AiShot>();
        public int AiLoadoutCount { get; private set; }
        public int AiAimCount { get; private set; }
        public int AiStateSampleCount { get; private set; }
        public int AiLoadoutSnapshotCount { get; private set; }
        public int AiShotCount { get; private set; }
        public readonly Dictionary<uint, AiDespawn> AiDespawns = new Dictionary<uint, AiDespawn>();
        public int AiSpawnCount { get; private set; }
        public int AiDespawnCount { get; private set; }
        public readonly Dictionary<uint, AiTarget> AiTargets = new Dictionary<uint, AiTarget>();
        public int AiTargetCount { get; private set; }
        // M2-v10 scene objects + explosions.
        public readonly Queue<SceneObjectState> SceneObjectUpdates = new Queue<SceneObjectState>();
        public int SceneObjectUpdateCount { get; private set; }
        public readonly Queue<ExplosionEvent> ExplosionEvents = new Queue<ExplosionEvent>();
        public int ExplosionEventCount { get; private set; }
        // M2-v12 bullet impact presentation relay.
        public readonly Queue<ImpactFx> ImpactFxs = new Queue<ImpactFx>();
        public int ImpactFxCount { get; private set; }
        public readonly Dictionary<ushort, LoadoutState> Loadouts = new Dictionary<ushort, LoadoutState>();
        public int LoadoutCount { get; private set; }
        /// <summary>v5: latest SRaidRules snapshot (66B); the mod compares baseStatsHash and applies client-side values.</summary>
        public RaidRules LatestRaidRules { get; private set; }
        public int RaidRulesCount { get; private set; }
        /// <summary>v5: server-authoritative buff state keyed by (playerId &lt;&lt; 32) | buffId.</summary>
        public readonly Dictionary<ulong, PlayerBuff> PlayerBuffs = new Dictionary<ulong, PlayerBuff>();
        /// <summary>v5: every received SPlayerBuff in order (late-join replays several Adds at once).</summary>
        public readonly Queue<PlayerBuff> PlayerBuffEvents = new Queue<PlayerBuff>();
        public PlayerBuff LastPlayerBuff { get; private set; }
        public int PlayerBuffCount { get; private set; }
        public readonly Dictionary<ushort, PlayerStateSample> PlayerStateSamples = new Dictionary<ushort, PlayerStateSample>();
        public readonly Dictionary<ushort, PlayerFaceV8> PlayerFaces = new Dictionary<ushort, PlayerFaceV8>();
        public readonly Dictionary<ulong, PlayerLoadoutV8> PlayerLoadoutsV8 = new Dictionary<ulong, PlayerLoadoutV8>();
        /// <summary>
        /// v8.1: latest active (held) slot per player, derived from SPlayerLoadoutV8. Read-only
        /// local view; it is not sent back and deliberately survives ResetInventoryState (the
        /// in-raid epoch check is what discards stale messages).
        /// </summary>
        public readonly Dictionary<ushort, byte> PlayerActiveSlots = new Dictionary<ushort, byte>();
        public readonly Dictionary<ushort, PlayerShot> PlayerShots = new Dictionary<ushort, PlayerShot>();
        public int PlayerStateSampleCount { get; private set; }
        public int PlayerFaceCount { get; private set; }
        public int PlayerLoadoutV8Count { get; private set; }
        public int PlayerShotCount { get; private set; }
        public MirrorState LastMirror { get; private set; }
        public int MirrorCount { get; private set; }
        public bool AutoAckMirror { get; set; }
        public ResumeResult Resume { get; private set; }
        public int ResumeCount { get; private set; }
        public uint RaidEpoch { get; private set; }
        public int PlayerEventCount { get; private set; }
        public PlayerEvent LastPlayerEvent { get; private set; }
        public int SnapshotsReceived { get; private set; }
        public string LastError { get; private set; } = string.Empty;

        /// <summary>T6: server ground-item mirror, snapshot chunks and pickup correlations.</summary>
        public WorldItemMirrorState WorldItemMirror { get; } = new WorldItemMirrorState();
        public IReadOnlyDictionary<uint, WorldItemInfo> WorldItems => WorldItemMirror.Items;
        public uint BackpackRevision => WorldItemMirror.BackpackRevision;
        public WorldItemPickupResult LastWorldItemPickup => WorldItemMirror.LastPickup;
        public int WorldItemPickupCount => WorldItemMirror.PickupCount;
        public WorldItemDropResult LastWorldItemDrop => WorldItemMirror.LastDrop;
        public int WorldItemDropCount => WorldItemMirror.DropCount;
        public int WorldItemSpawnCount => WorldItemMirror.SpawnCount;
        public int WorldItemDespawnCount => WorldItemMirror.DespawnCount;
        public int WorldItemSnapshotCount => WorldItemMirror.SnapshotCount;

        /// <summary>T5: server searchable-container mirror (kind 3 map containers now).</summary>
        public ContainerMirrorState ContainerMirror { get; } = new ContainerMirrorState();
        public ContainerContents LastContainerContents => ContainerMirror.LastContents;
        public int ContainerContentsCount => ContainerMirror.ApplyCount;

        /// <summary>T7/D6: corpse container spawn records keyed by containerId.</summary>
        public readonly Dictionary<uint, CorpseSpawn> Corpses = new Dictionary<uint, CorpseSpawn>();
        public int CorpseSpawnCount { get; private set; }
        public int CorpseDespawnCount { get; private set; }

        /// <summary>P1 phase 3: durability/loaded-ammo detail keyed by item uid.</summary>
        public readonly Dictionary<uint, ItemDetail> ItemDetails =
            new Dictionary<uint, ItemDetail>();
        public ItemDetail LastItemDetail { get; private set; }
        public int ItemDetailCount { get; private set; }
        /// <summary>Contract 2.14.0: server-owned attachments keyed by item uid.</summary>
        public readonly Dictionary<uint, ItemMods> ItemModsByUid =
            new Dictionary<uint, ItemMods>();
        public ItemMods LastItemMods { get; private set; }
        public int ItemModsCount { get; private set; }
        /// <summary>Contract 2.15.0: result of a detach request (new backpack item uid).</summary>
        public ItemModDetachResult LastItemModDetach { get; private set; }
        public int ItemModDetachCount { get; private set; }
        /// <summary>Contract 2.17.0: result of an attach request (attached item id).</summary>
        public ItemModAttachResult LastItemModAttach { get; private set; }
        public int ItemModAttachCount { get; private set; }
        public ItemReloadResult LastItemReload { get; private set; }
        public int ItemReloadCount { get; private set; }
        public Respawn LastRespawn { get; private set; }
        public int RespawnCount { get; private set; }
        public int InitialInventoryCount { get; private set; }
        public int InventorySnapshotCount { get; private set; }

        /// <summary>T4A: full initial three-inventory payload and the pure native mirror model.</summary>
        public InitialInventory LastInitialInventory { get; private set; }
        public InventoryMirrorState InventoryMirror { get; } = new InventoryMirrorState();
        private readonly Dictionary<byte, InventorySnapshot> _inventorySnapshots =
            new Dictionary<byte, InventorySnapshot>();
        public IReadOnlyDictionary<byte, InventorySnapshot> InventorySnapshots => _inventorySnapshots;
        public ItemTransferResult LastItemTransfer { get; private set; }
        public int ItemTransferCount { get; private set; }
        public ItemArrangeResult LastItemArrange { get; private set; }
        public int ItemArrangeCount { get; private set; }
        public ItemConsumeResult LastItemConsume { get; private set; }
        public int ItemConsumeCount { get; private set; }
        /// <summary>T4B: SInventorySnapshot count for the three player inventories only.</summary>
        public int PlayerInventorySnapshotCount { get; private set; }
        /// <summary>T4C: per-kind revision of the three player inventories (0/1/2).</summary>
        public uint PlayerInventoryRevision(byte kind) => InventoryMirror.GetRevision(kind);

        /// <summary>Code of the newest SLogin (0 = accepted).</summary>
        public byte LastLoginCode { get; private set; }

        /// <summary>A1: the server fenced this session because the account logged in elsewhere.</summary>
        public bool SessionReplaced => LastLoginCode == (byte)ErrorCode.SessionReplaced;

        public static long NowMs() => Clock.ElapsedMilliseconds;

        public void Login(string account, string version)
        {
            _account = account;
            _version = version;
            State = ClientState.LoggingIn;
            SendLogin();
        }

        /// <summary>Logs in with the protocol bot version from the shared protocol library.</summary>
        public void Login(string account) => Login(account, Protocol.BotClientVersion);

        public void Tick()
        {
            var now = NowMs();

            if (State == ClientState.LoggingIn && now - _lastLoginSentMs >= 300)
                SendLogin();
            if (State == ClientState.Joining && now - _lastJoinSentMs >= 300)
            {
                if (!string.IsNullOrEmpty(_resumeToken))
                    SendResume();
                else
                    SendJoin();
            }

            Pump(now);

            // server restarted or link died: fall back to logging in again
            if (State == ClientState.InGame && now - _lastReceiveMs > 5000)
            {
                State = ClientState.LoggingIn;
                LastError = "link lost, reconnecting";
                SendLogin();
            }

            // keep the session alive even when the local player is not available yet
            if (State == ClientState.InGame && now - _lastTrafficMs >= 1000)
                SendPing(now);

            _retransmits.Clear();
            _control.CollectRetransmits(_sessionId, now, _retransmits);
            _commands.CollectRetransmits(_sessionId, now, _retransmits);
            foreach (var frame in _retransmits)
                _udp.Send(frame, frame.Length, _server);
        }

        public void SendInput(uint seq, int x, int y, int z, ushort yaw, short pitch, byte flags)
        {
            if (State != ClientState.InGame)
                return;

            var writer = new Writer();
            new Input { Seq = seq, X = x, Y = y, Z = z, Yaw = yaw, Pitch = pitch, Flags = flags }.Write(writer);
            var frame = _input.BuildFrame(OpCode.CInput, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendLoot(ulong correlationId, uint containerId, ushort slot, byte action,
                             uint itemUid = 0, uint count = 0)
        {
            if (State != ClientState.InGame)
                return;

            var writer = new Writer();
            new LootRequest
            {
                CorrelationId = correlationId,
                ContainerId = containerId,
                Slot = slot,
                Action = action,
                ItemUid = itemUid,
                Count = count,
            }.Write(writer);

            var frame = _commands.BuildFrame(OpCode.CLoot, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendExtract(ulong correlationId)
        {
            if (State != ClientState.InGame)
                return;

            var writer = new Writer();
            new ExtractRequest { CorrelationId = correlationId }.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CExtract, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        /// <summary>M2-v12: toggle this player's ready flag for the next raid.</summary>
        public bool SendPlayerReady(bool ready)
        {
            if (State != ClientState.InGame)
                return false;
            var writer = new Writer();
            new PlayerReady { Ready = ready }.Write(writer);
            var frame = _control.BuildFrame(OpCode.CPlayerReady, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        /// <summary>M2-v12: host-only force reset; the server ends the raid and broadcasts it.</summary>
        public bool SendForceResetRaid()
        {
            if (State != ClientState.InGame)
                return false;
            var writer = new Writer();
            new ForceResetRaid().Write(writer);
            var frame = _control.BuildFrame(OpCode.CForceResetRaid, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        /// <summary>
        /// True when the newest SAiSpawn for this aiId arrived after its SAiDespawn. Both
        /// records can be delivered in the same Tick during owner migration; the despawn
        /// handler must not delete the replacement spawn in that case.
        /// </summary>
        public bool AiSpawnIsNewerThanDespawn(uint aiId)
        {
            if (!AiSpawnOrder.TryGetValue(aiId, out var spawnOrder))
                return false;
            if (!AiDespawnOrder.TryGetValue(aiId, out var despawnOrder))
                return true;
            return spawnOrder > despawnOrder;
        }

        public void SendContainerView(ulong correlationId, uint containerId)
        {
            if (State != ClientState.InGame)
                return;

            var writer = new Writer();
            new ContainerViewRequest { CorrelationId = correlationId, ContainerId = containerId }.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CContainerView, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        /// <summary>T5: open one server container (CContainerOpen 35).</summary>
        public bool SendContainerOpen(ulong correlationId, uint containerId)
        {
            if (State != ClientState.InGame || containerId == 0)
                return false;

            var writer = new Writer();
            new ContainerOpen
            {
                CorrelationId = correlationId,
                ContainerId = containerId,
            }.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CContainerOpen, writer.ToArray(), _sessionId,
                NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        /// <summary>
        /// T5/T6: request the authoritative snapshot for one player inventory. Used to recover
        /// when an initial-inventory packet was missed and the backpack revision is still 0.
        /// </summary>
        public bool SendInventorySync(ulong correlationId, byte kind, uint ownerId)
        {
            if (State != ClientState.InGame)
                return false;
            var writer = new Writer();
            new InventorySyncRequest
            {
                CorrelationId = correlationId,
                Kind = kind,
                OwnerId = ownerId,
            }.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CInventorySync, writer.ToArray(), _sessionId,
                NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        /// <summary>
        /// T6: ask the server to move a mirrored ground item into the backpack. Returns false
        /// (and sends nothing) before SInitialInventory/SInventorySnapshot provides a revision,
        /// or when this worldItemId already has a pickup in flight.
        /// </summary>
        public bool SendWorldItemPickup(ulong correlationId, uint worldItemId, uint expectedItemUid,
                                        uint expectedItemId, uint count)
        {
            if (State != ClientState.InGame)
                return false;

            var revision = WorldItemMirror.BackpackRevision;
            if (revision == 0)
                return false;
            if (!WorldItemMirror.TryRegisterPickup(correlationId, worldItemId))
                return false;

            var writer = new Writer();
            new WorldItemPickup
            {
                CorrelationId = correlationId,
                WorldItemId = worldItemId,
                ExpectedItemUid = expectedItemUid,
                ExpectedItemId = expectedItemId,
                Count = count,
                ExpectedBackpackRevision = revision,
            }.Write(writer);

            var frame = _commands.BuildFrame(OpCode.CWorldItemPickup, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        /// <summary>
        /// T6b: ask the server to take an item out of one of the player's three inventories and
        /// create a ground WorldItem at the given pose. Only kinds 0/1/2 are accepted here;
        /// container/corpse sources are intentionally not part of T6b. Returns false (and sends
        /// nothing) before the kind revision is known, or when this correlation already has a
        /// drop in flight.
        /// </summary>
        public bool SendWorldItemDrop(ulong correlationId, byte sourceKind, uint sourceId,
                                      ushort sourceSlot, uint itemUid, uint count, string scene,
                                      int x, int y, int z, ushort yaw, uint expectedRevision)
        {
            if (State != ClientState.InGame)
                return false;
            if (sourceKind > 2)
                return false;
            if (expectedRevision == 0)
                return false;
            if (!WorldItemMirror.TryRegisterDrop(correlationId))
                return false;

            var writer = new Writer();
            new WorldItemDrop
            {
                CorrelationId = correlationId,
                SourceKind = sourceKind,
                SourceId = sourceId,
                SourceSlot = sourceSlot,
                ItemUid = itemUid,
                Count = count,
                Scene = scene,
                X = x,
                Y = y,
                Z = z,
                Yaw = yaw,
                ExpectedRevision = expectedRevision,
            }.Write(writer);

            var frame = _commands.BuildFrame(OpCode.CWorldItemDrop, writer.ToArray(), _sessionId,
                NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        public ContainerView GetContainerView(uint containerId)
            => _views.TryGetValue(containerId, out var view) ? view : null;

        /// <summary>
        /// T4B: route one native equipment/backpack/petBag move to the server. Only the three
        /// player inventories are accepted; the server owns validation/revisions.
        /// </summary>
        public bool SendItemTransfer(ulong correlationId, byte sourceKind, uint sourceId,
                                     ushort sourceSlot, byte targetKind, uint targetId,
                                     ushort targetSlot, uint itemUid, uint count,
                                     uint expectedItemId, uint expectedRevision)
        {
            if (State != ClientState.InGame)
                return false;
            // kind 4 is the WorldItem pseudo-kind and never a transfer endpoint; kind 5 is a
            // corpse container (take-only, enforced by the server).
            if (sourceKind > 5 || targetKind > 5 || sourceKind == 4 || targetKind == 4)
                return false;
            if (expectedRevision == 0)
                return false;

            var writer = new Writer();
            new ItemTransfer
            {
                CorrelationId = correlationId,
                SourceKind = sourceKind,
                SourceId = sourceId,
                SourceSlot = sourceSlot,
                TargetKind = targetKind,
                TargetId = targetId,
                TargetSlot = targetSlot,
                ItemUid = itemUid,
                Count = count,
                ExpectedItemId = expectedItemId,
                ExpectedRevision = expectedRevision,
            }.Write(writer);

            var frame = _commands.BuildFrame(OpCode.CItemTransfer, writer.ToArray(), _sessionId,
                NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        /// <summary>
        /// Contract 2.15.0: ask the server to pull one attachment off <paramref name="parentUid"/>.
        /// On success the server creates a normal backpack item and replies with its uid.
        /// </summary>
        public bool SendItemModDetach(ulong correlationId, uint parentUid, string slotKey)
        {
            if (State != ClientState.InGame || parentUid == 0 || string.IsNullOrEmpty(slotKey))
                return false;

            var writer = new Writer();
            new ItemModDetach
            {
                CorrelationId = correlationId,
                ParentUid = parentUid,
                SlotKey = slotKey,
            }.Write(writer);

            var frame = _commands.BuildFrame(OpCode.CItemModDetach, writer.ToArray(), _sessionId,
                NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        /// <summary>
        /// Contract 2.17.0: plug the backpack item <paramref name="itemUid"/> into the attachment
        /// slot <paramref name="slotKey"/> of <paramref name="parentUid"/>. The backpack item is
        /// consumed into the attachment; the server replies with the attached item id.
        /// </summary>
        public bool SendItemModAttach(ulong correlationId, uint parentUid, uint itemUid,
                                      string slotKey)
        {
            if (State != ClientState.InGame || parentUid == 0 || itemUid == 0 ||
                string.IsNullOrEmpty(slotKey))
                return false;

            var writer = new Writer();
            new ItemModAttach
            {
                CorrelationId = correlationId,
                ParentUid = parentUid,
                ItemUid = itemUid,
                SlotKey = slotKey,
            }.Write(writer);

            var frame = _commands.BuildFrame(OpCode.CItemModAttach, writer.ToArray(), _sessionId,
                NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        /// <summary>
        /// P1: submit a validated final layout for a container (sort/merge/reorder). Only
        /// backpack/petBag/map containers are accepted; equipment slots are fixed by slot.Key.
        /// </summary>
        public bool SendItemArrange(ulong correlationId, byte kind, uint ownerId,
                                    uint expectedRevision, InventoryEntry[] entries)
        {
            if (State != ClientState.InGame)
                return false;
            if (kind == 0 || kind > 3)
                return false;
            if (expectedRevision == 0)
                return false;

            var writer = new Writer();
            new ItemArrange
            {
                CorrelationId = correlationId,
                Kind = kind,
                OwnerId = ownerId,
                ExpectedRevision = expectedRevision,
                Entries = entries ?? Array.Empty<InventoryEntry>(),
            }.Write(writer);

            var frame = _commands.BuildFrame(OpCode.CItemArrange, writer.ToArray(), _sessionId,
                NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        /// <summary>
        /// P1: consume/use a stack in one of the player's containers. The server owns the
        /// count and pushes a snapshot; the client must not decrement StackCount locally.
        /// </summary>
        /// <param name="healAmount">
        /// Contract 2.16.0: HP the native usage behavior would restore. The server clamps it
        /// and settles the player's HP authoritatively (broadcast as CombatEventV4 type=2).
        /// </param>
        public bool SendItemConsume(ulong correlationId, byte kind, uint ownerId, ushort slot,
                                    uint itemUid, uint itemId, uint count,
                                    uint expectedRevision, byte reason, uint healAmount = 0)
        {
            if (State != ClientState.InGame)
                return false;
            if (kind > 2)
                return false;
            if (expectedRevision == 0 || count == 0)
                return false;

            var writer = new Writer();
            new ItemConsume
            {
                CorrelationId = correlationId,
                Kind = kind,
                OwnerId = ownerId,
                Slot = slot,
                ItemUid = itemUid,
                ItemId = itemId,
                Count = count,
                ExpectedRevision = expectedRevision,
                Reason = reason,
                HealAmount = healAmount,
            }.Write(writer);

            var frame = _commands.BuildFrame(OpCode.CItemConsume, writer.ToArray(), _sessionId,
                NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        public bool SendItemReload(ulong correlationId, uint gunUid, uint ammoItemId,
                                   uint count, uint loadedAfter)
        {
            if (State != ClientState.InGame)
                return false;
            if (gunUid == 0 || ammoItemId == 0 || count == 0 || loadedAfter < count)
                return false;

            var writer = new Writer();
            new ItemReload
            {
                CorrelationId = correlationId,
                GunUid = gunUid,
                AmmoItemId = ammoItemId,
                Count = count,
                LoadedAfter = loadedAfter,
            }.Write(writer);

            var frame = _commands.BuildFrame(OpCode.CItemReload, writer.ToArray(), _sessionId,
                NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        public bool SendItemWeaponFire(uint gunUid, uint ammoItemId, uint loadedCount)
        {
            if (State != ClientState.InGame)
                return false;
            if (gunUid == 0 || ammoItemId == 0)
                return false;

            var writer = new Writer();
            new ItemWeaponFire
            {
                GunUid = gunUid,
                AmmoItemId = ammoItemId,
                LoadedCount = loadedCount,
            }.Write(writer);

            var frame = _commands.BuildFrame(OpCode.CItemWeaponFire, writer.ToArray(), _sessionId,
                NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        public bool SendItemDurability(uint itemUid, uint durability, uint maxDurability)
        {
            if (State != ClientState.InGame)
                return false;
            if (itemUid == 0 || maxDurability == 0 || durability > maxDurability)
                return false;

            var writer = new Writer();
            new ItemDurability
            {
                ItemUid = itemUid,
                Durability = durability,
                MaxDurability = maxDurability,
            }.Write(writer);

            var frame = _commands.BuildFrame(OpCode.CItemDurability, writer.ToArray(), _sessionId,
                NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        public bool SendBackpackCapacity(uint capacity)
        {
            if (State != ClientState.InGame || capacity == 0 || capacity > 64)
                return false;
            var writer = new Writer();
            new BackpackCapacity { Capacity = capacity }.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CBackpackCapacity, writer.ToArray(),
                _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
            return true;
        }

        private void ResetInventoryState()
        {
            InventoryMirror.Clear();
            ContainerMirror.Clear();
            Corpses.Clear();
            ItemDetails.Clear();
            LastItemDetail = null;
            ItemModsByUid.Clear();
            LastItemMods = null;
            _inventorySnapshots.Clear();
            LastInitialInventory = null;
            LastItemTransfer = null;
            LastItemArrange = null;
            LastItemConsume = null;
            LastItemReload = null;
            LastRespawn = null;
        }

        public void SendStartRaid(uint sceneIndex, string sceneName)
        {
            if (State != ClientState.InGame)
                return;

            var writer = new Writer();
            new RaidStart { SceneIndex = sceneIndex, SceneName = sceneName }.Write(writer);
            var frame = _control.BuildFrame(OpCode.CStartRaid, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendPlayerSpawn(string sceneName, int x, int y, int z, uint raidEpoch)
        {
            if (State != ClientState.InGame)
                return;

            var writer = new Writer();
            new PlayerSpawn { SceneName = sceneName, X = x, Y = y, Z = z, RaidEpoch = raidEpoch }.Write(writer);
            var frame = _control.BuildFrame(OpCode.CPlayerSpawn, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendHitReport(ulong correlationId, byte targetKind, uint targetId, int damage)
        {
            if (State != ClientState.InGame)
                return;

            var writer = new Writer();
            new HitReport
            {
                CorrelationId = correlationId,
                TargetKind = targetKind,
                TargetId = targetId,
                Damage = damage,
            }.Write(writer);

            var frame = _commands.BuildFrame(OpCode.CHitReport, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendHitEvent(HitEvent hit)
        {
            if (State != ClientState.InGame || hit == null)
                return;
            var writer = new Writer();
            hit.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CHitEvent, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        /// <summary>M2-v10: one authoritative scene-object transition (door/breakable).</summary>
        public void SendSceneObjectState(byte kind, uint key, byte state, uint raidEpoch)
        {
            if (State != ClientState.InGame)
                return;
            var message = new SceneObjectState
            {
                Key = key,
                Kind = kind,
                State = state,
                RaidEpoch = raidEpoch,
            };
            var writer = new Writer();
            message.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CSceneObjectState, writer.ToArray(),
                _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        /// <summary>M2-v10: explosion fact, used for both presentation relay and hit damage.</summary>
        public void SendExplosion(ExplosionEvent explosion)
        {
            if (State != ClientState.InGame || explosion == null)
                return;
            var writer = new Writer();
            explosion.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CExplosion, writer.ToArray(), _sessionId,
                NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        /// <summary>M2-v12: presentation-only bullet impact fact (unreliable, no gameplay effect).</summary>
        public void SendImpactFx(ImpactFx impact)
        {
            if (State != ClientState.InGame || impact == null)
                return;
            var writer = new Writer();
            impact.Write(writer);
            var frame = _input.BuildFrame(OpCode.CImpactFx, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendAiAnim(AiAnim anim)
        {
            if (State != ClientState.InGame || anim == null)
                return;
            var writer = new Writer();
            anim.Write(writer);
            var frame = _input.BuildFrame(OpCode.CAiAnim, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendAiLoadout(AiLoadout loadout)
        {
            if (State != ClientState.InGame || loadout == null)
                return;
            var writer = new Writer();
            loadout.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CAiLoadout, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendAiAim(AiAim aim)
        {
            if (State != ClientState.InGame || aim == null)
                return;
            var writer = new Writer();
            aim.Write(writer);
            var frame = _input.BuildFrame(OpCode.CAiAim, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendAiStateSample(AiStateSample sample)
        {
            if (State != ClientState.InGame || sample == null)
                return;
            var writer = new Writer();
            sample.Write(writer);
            var frame = _input.BuildFrame(OpCode.CAiStateSample, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendAiLoadoutV7(AiLoadoutV7 loadout)
        {
            if (State != ClientState.InGame || loadout == null)
                return;
            var writer = new Writer();
            loadout.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CAiLoadoutV7, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendAiShot(AiShot shot)
        {
            if (State != ClientState.InGame || shot == null)
                return;
            var writer = new Writer();
            shot.Write(writer);
            var frame = _input.BuildFrame(OpCode.CAiShot, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendPlayerStateSample(PlayerStateSample sample)
        {
            if (State != ClientState.InGame || sample == null)
                return;
            var writer = new Writer();
            sample.Write(writer);
            var frame = _input.BuildFrame(OpCode.CPlayerStateSample, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendPlayerLoadoutV8(PlayerLoadoutV8 loadout)
        {
            if (State != ClientState.InGame || loadout == null)
                return;
            var writer = new Writer();
            loadout.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CPlayerLoadoutV8, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendPlayerShot(PlayerShot shot)
        {
            if (State != ClientState.InGame || shot == null)
                return;
            var writer = new Writer();
            shot.Write(writer);
            var frame = _input.BuildFrame(OpCode.CPlayerShot, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendPlayerFaceV8(PlayerFaceV8 face)
        {
            if (State != ClientState.InGame || face == null)
                return;
            var writer = new Writer();
            face.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CPlayerFaceV8, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendPlayerStats(PlayerStats stats)
        {
            if (State != ClientState.InGame || stats == null)
                return;
            var writer = new Writer();
            stats.Write(writer);
            var frame = _commands.BuildFrame(OpCode.CPlayerStats, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        /// <summary>v5 CShootFact: one fact per trigger pull (never per bullet), unreliable.</summary>
        public void SendShootFact(ShootFact fact)
        {
            if (State != ClientState.InGame || fact == null)
                return;
            var writer = new Writer();
            fact.Write(writer);
            var frame = _input.BuildFrame(OpCode.CShootFact, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        /// <summary>v5 CUseConsumable: use-completed fact; the server only maps it to buffs (no inventory deduction).</summary>
        public void SendUseConsumable(UseConsumable message)
        {
            if (State != ClientState.InGame || message == null)
                return;
            var writer = new Writer();
            message.Write(writer);
            var frame = _control.BuildFrame(OpCode.CUseConsumable, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendAiState(uint aiId, ushort ownerPlayerId, int x, int y, int z, ushort yaw, byte flags)
        {
            if (State != ClientState.InGame)
                return;

            var writer = new Writer();
            new AiState
            {
                AiId = aiId,
                OwnerPlayerId = ownerPlayerId,
                X = x,
                Y = y,
                Z = z,
                Yaw = yaw,
                Flags = flags,
            }.Write(writer);

            var frame = _input.BuildFrame(OpCode.CAiState, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendLeaveRaid()
        {
            if (State != ClientState.InGame)
                return;

            var frame = _control.BuildFrame(OpCode.CLeaveRaid, Array.Empty<byte>(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendLoadout(ulong correlationId, LoadoutEntry[] entries)
        {
            if (State != ClientState.InGame)
                return;

            var writer = new Writer();
            new LoadoutRequest
            {
                CorrelationId = correlationId,
                Entries = entries ?? Array.Empty<LoadoutEntry>(),
            }.Write(writer);

            var frame = _control.BuildFrame(OpCode.CLoadout, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        public void SendMirrorAck(ulong version)
        {
            if (State != ClientState.InGame)
                return;

            var writer = new Writer();
            new MirrorAck { Version = version }.Write(writer);
            var frame = _control.BuildFrame(OpCode.CMirrorAck, writer.ToArray(), _sessionId, NowMs());
            _udp.Send(frame, frame.Length, _server);
        }

        private void SendLogin()
        {
            _lastLoginSentMs = NowMs();
            var writer = new Writer();
            new LoginRequest { Account = _account, ClientVersion = _version, ContentHash = ContentHash }
                .Write(writer);
            var frame = _control.BuildFrame(OpCode.CLogin, writer.ToArray(), _sessionId, _lastLoginSentMs);
            _udp.Send(frame, frame.Length, _server);
        }

        public void SetResumeToken(string token)
        {
            _resumeToken = token ?? string.Empty;
        }

        private void SendResume()
        {
            _lastJoinSentMs = NowMs();
            var writer = new Writer();
            new ResumeRequest { Token = _resumeToken }.Write(writer);
            var frame = _control.BuildFrame(OpCode.CResume, writer.ToArray(), _sessionId, _lastJoinSentMs);
            _udp.Send(frame, frame.Length, _server);
        }

        private void SendJoin()
        {
            _lastJoinSentMs = NowMs();
            var writer = new Writer();
            new JoinRequest { Token = Token }.Write(writer);
            var frame = _control.BuildFrame(OpCode.CJoin, writer.ToArray(), _sessionId, _lastJoinSentMs);
            _udp.Send(frame, frame.Length, _server);
        }

        private void Pump(long now)
        {
            try
            {
                while (_udp.Available > 0)
                {
                    IPEndPoint from = null;
                    var data = _udp.Receive(ref from);
                    if (data == null || data.Length < FrameCodec.HeaderSize + 1)
                        continue;

                    if (!FrameCodec.TryDecode(data, out var header, out var op, out var payload))
                        continue;

                    _lastReceiveMs = now;
                    _lastTrafficMs = now;

                    if (header.Type == FrameType.Ping)
                    {
                        SendPong(header.SessionId, payload);
                        continue;
                    }

                    if (header.Type != FrameType.Data)
                        continue;

                    var body = payload.ToArray();
                    _delivered.Clear();

                    switch (header.Channel)
                    {
                        case 3:
                            _control.OnFrame(op, header, body, now, _delivered);
                            break;
                        case 1:
                            _commands.OnFrame(op, header, body, now, _delivered);
                            break;
                        default:
                            _input.OnFrame(op, header, body, now, _delivered);
                            break;
                    }

                    var messages = _delivered.ToArray();
                    foreach (var message in messages)
                    {
                        // keep the local session id equal to the server's player id.
                        // SLogin with a different id means a brand new server session:
                        // both sides restart their channel sequence spaces together.
                        if (message.Op == OpCode.SLogin && header.SessionId != 0)
                        {
                            if (_sessionId != 0 && header.SessionId != _sessionId)
                                ResetChannels();
                            _sessionId = header.SessionId;
                        }
                        else if ((message.Op == OpCode.SJoin || message.Op == OpCode.SResume) &&
                                 header.SessionId != 0)
                        {
                            _sessionId = header.SessionId;
                        }

                        Handle(message.Op, message.Payload);
                    }
                }
            }
            catch (SocketException ex)
            {
                // WSAENOBUFS / ECONNRESET are transient on UDP; recreating the socket
                // in a tight loop only makes things worse.
                LastError = "socket error: " + ex.SocketErrorCode;

                var stamp = NowMs();
                if (stamp - _lastSocketRecreateMs > 2000)
                {
                    _lastSocketRecreateMs = stamp;

                    if (ex.SocketErrorCode != SocketError.NoBufferSpaceAvailable &&
                        ex.SocketErrorCode != SocketError.ConnectionReset)
                        RecreateSocket();
                }
            }
            catch (ObjectDisposedException)
            {
                RecreateSocket();
            }
        }

        private void RecreateSocket()
        {
            try { _udp?.Close(); } catch { /* ignore */ }

            _udp = new UdpClient();
            try
            {
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            }
            catch
            {
                // keep the object; the next send will surface the error
            }
        }

        private void ResetChannels()
        {
            _control = new ReliableChannel(3, true, true);
            _commands = new ReliableChannel(1, true, false);
            _input = new ReliableChannel(0, false, false);
            _retransmits.Clear();
            _delivered.Clear();
        }

        private void SendPong(uint sessionId, ArraySegment<byte> payload)
        {
            var header = Header.Create(3, 0, 0, 0, sessionId);
            header.Type = FrameType.Pong;

            var headerBytes = FrameCodec.EncodeHeader(header);
            var frame = new byte[headerBytes.Length + payload.Count];
            Array.Copy(headerBytes, frame, headerBytes.Length);
            if (payload.Count > 0)
                Array.Copy(payload.Array!, payload.Offset, frame, headerBytes.Length, payload.Count);

            _udp.Send(frame, frame.Length, _server);
            _lastTrafficMs = NowMs();
        }

        private void SendPing(long now)
        {
            var header = Header.Create(3, 0, 0, 0, _sessionId);
            header.Type = FrameType.Ping;

            var headerBytes = FrameCodec.EncodeHeader(header);
            var frame = new byte[headerBytes.Length + 8];
            Array.Copy(headerBytes, frame, headerBytes.Length);

            var ticks = (ulong)now;
            for (var i = 0; i < 8; i++)
                frame[headerBytes.Length + i] = (byte)(ticks >> (i * 8));

            _udp.Send(frame, frame.Length, _server);
            _lastTrafficMs = now;
        }

        private void Handle(OpCode op, byte[] payload)
        {
            switch (op)
            {
                case OpCode.SLogin:
                {
                    var result = LoginResult.Read(new Reader(payload, 0, payload.Length));
                    LastLoginCode = result.Code;
                    if (result.Code != 0)
                    {
                        State = ClientState.Failed;
                        LastError = $"login failed: {result.Code}";
                        return;
                    }

                    Token = result.Token;
                    // _sessionId is synced from the server frame header in Pump()

                    if (!string.IsNullOrEmpty(_resumeToken))
                    {
                        State = ClientState.Joining;
                        SendResume();
                    }
                    else
                    {
                        State = ClientState.Joining;
                        SendJoin();
                    }
                    break;
                }
                case OpCode.SResume:
                {
                    var result = ResumeResult.Read(new Reader(payload, 0, payload.Length));
                    if (result.Code != 0)
                    {
                        SendJoin();
                        break;
                    }

                    Resume = result;
                    RaidEpoch = result.RaidEpoch;
                    PlayerId = result.PlayerId;
                    // T6: the server re-sends initial inventory + world item snapshot on
                    // resume; drop the old mirror/revision until those arrive.
                    WorldItemMirror.ResetForRaid();
                    ResetInventoryState();
                    // the resumed server session keeps the token that was used to resume;
                    // future reconnects must present that token again, not the fresh
                    // login token of the temporary session
                    if (!string.IsNullOrEmpty(_resumeToken))
                        Token = _resumeToken;
                    State = ClientState.InGame;
                    ResumeCount++;
                    break;
                }
                case OpCode.SJoin:
                {
                    var result = JoinResult.Read(new Reader(payload, 0, payload.Length));
                    if (result.Code != 0)
                    {
                        State = ClientState.Failed;
                        LastError = $"join failed: {result.Code}";
                        return;
                    }

                    PlayerId = result.PlayerId;
                    // T6: a fresh join (including re-login after a server restart) starts a
                    // new ledger/snapshot id sequence; drop any mirror from the old session.
                    WorldItemMirror.ResetForRaid();
                    ResetInventoryState();
                    // keep RaidEpoch: an in-process re-login on an already joined session
                    // must not lose the epoch of the raid it is still in
                    State = ClientState.InGame;
                    break;
                }
                case OpCode.SSnapshot:
                {
                    var snapshot = Snapshot.Read(new Reader(payload, 0, payload.Length));
                    LatestSnapshot = snapshot;
                    SnapshotsReceived++;
                    break;
                }
                case OpCode.SLoot:
                {
                    LastLoot = LootResult.Read(new Reader(payload, 0, payload.Length));
                    break;
                }
                case OpCode.SExtract:
                {
                    LastExtract = ExtractResult.Read(new Reader(payload, 0, payload.Length));
                    break;
                }
                case OpCode.SPlayerReady:
                {
                    var ready = PlayerReadyState.Read(new Reader(payload, 0, payload.Length));
                    PlayerReady[ready.PlayerId] = ready.Ready;
                    LastPlayerReadyState = ready;
                    PlayerReadyCount++;
                    break;
                }
                case OpCode.SForceResetRaid:
                {
                    LastForceReset = ForceResetRaidState.Read(new Reader(payload, 0, payload.Length));
                    ForceResetCount++;
                    // ready gates belong to the raid that just ended; the server replays the
                    // cleared state right after this message.
                    PlayerReady.Clear();
                    break;
                }
                case OpCode.SRaidControlResult:
                {
                    LastRaidControlResult = RaidControlResult.Read(new Reader(payload, 0, payload.Length));
                    RaidControlResultCount++;
                    break;
                }
                case OpCode.SRaidPhase:
                {
                    RaidPhase = RaidPhaseMessage.Read(new Reader(payload, 0, payload.Length)).Phase;
                    break;
                }
                case OpCode.SRaidStart:
                {
                    var request = RaidStart.Read(new Reader(payload, 0, payload.Length));
                    RequestedScene = request.SceneName;
                    RaidEpoch = request.RaidEpoch;
                    // T6: a new raid reseeds the server ledger; never keep old ground visuals.
                    WorldItemMirror.ResetForRaid();
                    ResetInventoryState();
                    SceneLoadRequests++;
                    // A new raid always starts from a clean ready gate; the server follows
                    // SRaidStart with SPlayerReady(false) for every session.
                    PlayerReady.Clear();
                    AiSpawnOrder.Clear();
                    AiDespawnOrder.Clear();
                    _aiMessageOrder = 0;
                    break;
                }
                case OpCode.SSpawnAssign:
                {
                    SpawnAnchor = SpawnAssign.Read(new Reader(payload, 0, payload.Length));
                    SpawnAssignCount++;
                    break;
                }
                case OpCode.SCombatEvent:
                {
                    var eventV4 = CombatEventV4.Read(new Reader(payload, 0, payload.Length));
                    LastCombatEventV4 = eventV4;
                    CombatEventV4Queue.Enqueue(eventV4);
                    CombatEventV4Count++;
                    // Keep the legacy view populated for existing mod code during migration.
                    LastCombatEvent = new CombatEvent
                    {
                        EventId = eventV4.EventId,
                        Type = eventV4.Type,
                        TargetKind = eventV4.TargetKind,
                        TargetId = eventV4.TargetId,
                        PlayerId = eventV4.ReporterPlayerId,
                        Value = eventV4.HpAfter,
                    };
                    CombatEventCount++;
                    break;
                }
                case OpCode.SAiLoadout:
                {
                    var loadout = AiLoadout.Read(new Reader(payload, 0, payload.Length));
                    AiLoadouts[loadout.AiId] = loadout;
                    AiLoadoutCount++;
                    break;
                }
                case OpCode.SAiAim:
                {
                    var aim = AiAim.Read(new Reader(payload, 0, payload.Length));
                    AiAims[aim.AiId] = aim;
                    AiAimCount++;
                    break;
                }
                case OpCode.SAiStateSample:
                {
                    var sample = AiStateSample.Read(new Reader(payload, 0, payload.Length));
                    AiStateSamples[sample.AiId] = sample;
                    AiStateSampleCount++;
                    break;
                }
                case OpCode.SAiLoadoutV7:
                {
                    var loadout = AiLoadoutV7.Read(new Reader(payload, 0, payload.Length));
                    AiLoadoutSnapshots[loadout.AiId] = loadout;
                    AiLoadoutSnapshotCount++;
                    break;
                }
                case OpCode.SAiShot:
                {
                    var shot = AiShot.Read(new Reader(payload, 0, payload.Length));
                    AiShots[shot.AiId] = shot;
                    AiShotCount++;
                    break;
                }
                case OpCode.SPlayerStateSample:
                {
                    var reader = new Reader(payload, 0, payload.Length);
                    var playerId = reader.U16();
                    var sample = PlayerStateSample.Read(reader);
                    PlayerStateSamples[playerId] = sample;
                    PlayerStateSampleCount++;
                    break;
                }
                case OpCode.SPlayerLoadoutV8:
                {
                    var reader = new Reader(payload, 0, payload.Length);
                    var playerId = reader.U16();
                    var loadout = PlayerLoadoutV8.Read(reader);
                    PlayerLoadoutsV8[((ulong)playerId << 8) | loadout.Slot] = loadout;
                    PlayerActiveSlots[playerId] = loadout.ActiveSlot;
                    PlayerLoadoutV8Count++;
                    break;
                }
                case OpCode.SPlayerShot:
                {
                    var reader = new Reader(payload, 0, payload.Length);
                    var playerId = reader.U16();
                    var shot = PlayerShot.Read(reader);
                    PlayerShots[playerId] = shot;
                    PlayerShotCount++;
                    break;
                }
                case OpCode.SPlayerFaceV8:
                {
                    var reader = new Reader(payload, 0, payload.Length);
                    var playerId = reader.U16();
                    var face = PlayerFaceV8.Read(reader);
                    PlayerFaces[playerId] = face;
                    PlayerFaceCount++;
                    break;
                }
                case OpCode.SAiAnim:
                {
                    var anim = AiAnim.Read(new Reader(payload, 0, payload.Length));
                    AiAnims[anim.AiId] = anim;
                    AiAnimCount++;
                    break;
                }
                case OpCode.SAiHealth:
                {
                    var health = AiHealth.Read(new Reader(payload, 0, payload.Length));
                    AiHealths[health.AiId] = health;
                    AiHealthCount++;
                    break;
                }
                case OpCode.SAiState:
                {
                    var state = AiState.Read(new Reader(payload, 0, payload.Length));
                    AiStates[state.AiId] = state;
                    AiStateCount++;
                    break;
                }
                case OpCode.SAiSpawn:
                {
                    var spawn = AiSpawn.Read(new Reader(payload, 0, payload.Length));
                    AiSpawns[spawn.AiId] = spawn;
                    AiSpawnOrder[spawn.AiId] = ++_aiMessageOrder;
                    AiSpawnCount++;
                    break;
                }
                case OpCode.SAiDespawn:
                {
                    var despawn = AiDespawn.Read(new Reader(payload, 0, payload.Length));
                    AiDespawns[despawn.AiId] = despawn;
                    AiDespawnOrder[despawn.AiId] = ++_aiMessageOrder;
                    AiDespawnCount++;
                    break;
                }
                case OpCode.SAiTarget:
                {
                    var target = AiTarget.Read(new Reader(payload, 0, payload.Length));
                    AiTargets[target.AiId] = target;
                    AiTargetCount++;
                    break;
                }
                case OpCode.SObjectState:
                {
                    var state = SceneObjectState.Read(new Reader(payload, 0, payload.Length));
                    SceneObjectUpdates.Enqueue(state);
                    SceneObjectUpdateCount++;
                    break;
                }
                case OpCode.SExplosion:
                {
                    var explosion = ExplosionEvent.Read(new Reader(payload, 0, payload.Length));
                    ExplosionEvents.Enqueue(explosion);
                    ExplosionEventCount++;
                    break;
                }
                case OpCode.SImpactFx:
                {
                    var impact = ImpactFx.Read(new Reader(payload, 0, payload.Length));
                    ImpactFxs.Enqueue(impact);
                    ImpactFxCount++;
                    break;
                }
                case OpCode.SPlayerBuff:
                {
                    var buff = PlayerBuff.Read(new Reader(payload, 0, payload.Length));
                    var key = ((ulong)buff.PlayerId << 32) | buff.BuffId;
                    if (buff.Op == PlayerBuff.OpRemove)
                        PlayerBuffs.Remove(key);
                    else
                        PlayerBuffs[key] = buff;
                    PlayerBuffEvents.Enqueue(buff);
                    LastPlayerBuff = buff;
                    PlayerBuffCount++;
                    break;
                }
                case OpCode.SRaidRules:
                {
                    LatestRaidRules = RaidRules.Read(new Reader(payload, 0, payload.Length));
                    RaidRulesCount++;
                    break;
                }
                case OpCode.SLoadout:
                {
                    var state = LoadoutState.Read(new Reader(payload, 0, payload.Length));
                    Loadouts[state.PlayerId] = state;
                    LoadoutCount++;
                    break;
                }
                case OpCode.SMirror:
                {
                    var mirror = MirrorState.Read(new Reader(payload, 0, payload.Length));
                    LastMirror = mirror;
                    MirrorCount++;
                    if (AutoAckMirror)
                        SendMirrorAck(mirror.Version);
                    break;
                }
                case OpCode.SContainerContents:
                {
                    var contents = ContainerContents.Read(new Reader(payload, 0, payload.Length));
                    ContainerMirror.Apply(contents);
                    break;
                }
                case OpCode.SCorpseSpawn:
                {
                    var spawn = CorpseSpawn.Read(new Reader(payload, 0, payload.Length));
                    if (spawn.ContainerId != 0)
                        Corpses[spawn.ContainerId] = spawn;
                    CorpseSpawnCount++;
                    break;
                }
                case OpCode.SCorpseDespawn:
                {
                    var despawn = CorpseDespawn.Read(new Reader(payload, 0, payload.Length));
                    Corpses.Remove(despawn.ContainerId);
                    CorpseDespawnCount++;
                    break;
                }
                case OpCode.SItemDetail:
                {
                    var detail = ItemDetail.Read(new Reader(payload, 0, payload.Length));
                    if (detail.ItemUid != 0)
                        ItemDetails[detail.ItemUid] = detail;
                    LastItemDetail = detail;
                    ItemDetailCount++;
                    break;
                }
                case OpCode.SItemMods:
                {
                    var mods = ItemMods.Read(new Reader(payload, 0, payload.Length));
                    if (mods.ItemUid != 0)
                        ItemModsByUid[mods.ItemUid] = mods;
                    LastItemMods = mods;
                    ItemModsCount++;
                    break;
                }
                case OpCode.SItemModDetachResult:
                {
                    var detach = ItemModDetachResult.Read(new Reader(payload, 0, payload.Length));
                    LastItemModDetach = detach;
                    ItemModDetachCount++;
                    break;
                }
                case OpCode.SItemModAttachResult:
                {
                    var attach = ItemModAttachResult.Read(new Reader(payload, 0, payload.Length));
                    LastItemModAttach = attach;
                    ItemModAttachCount++;
                    break;
                }
                case OpCode.SContainerView:
                {
                    var view = ContainerView.Read(new Reader(payload, 0, payload.Length));
                    LastContainerView = view;
                    _views[view.ContainerId] = view;
                    break;
                }
                case OpCode.SInitialInventory:
                {
                    var initial = InitialInventory.Read(new Reader(payload, 0, payload.Length));
                    LastInitialInventory = initial;
                    InventoryMirror.Apply(initial);
                    WorldItemMirror.ApplyInitialInventory(initial);
                    InitialInventoryCount++;
                    break;
                }
                case OpCode.SInventorySnapshot:
                {
                    var state = InventoryState.Read(new Reader(payload, 0, payload.Length));
                    _inventorySnapshots[state.Snapshot.Kind] = state.Snapshot;
                    InventoryMirror.ApplySnapshot(state.Snapshot);
                    WorldItemMirror.ApplyInventoryState(state);
                    if (state.Snapshot.Kind == ContainerMirrorState.MapContainerKind ||
                        state.Snapshot.Kind == ContainerMirrorState.CorpseContainerKind)
                        ContainerMirror.ApplySnapshot(state.Snapshot);
                    InventorySnapshotCount++;
                    if (state.Snapshot.Kind <= 2)
                        PlayerInventorySnapshotCount++;
                    break;
                }
                case OpCode.SItemTransfer:
                {
                    LastItemTransfer = ItemTransferResult.Read(new Reader(payload, 0, payload.Length));
                    ItemTransferCount++;
                    break;
                }
                case OpCode.SItemArrange:
                {
                    LastItemArrange = ItemArrangeResult.Read(new Reader(payload, 0, payload.Length));
                    ItemArrangeCount++;
                    break;
                }
                case OpCode.SItemConsume:
                {
                    LastItemConsume = ItemConsumeResult.Read(new Reader(payload, 0, payload.Length));
                    ItemConsumeCount++;
                    break;
                }
                case OpCode.SItemReloadResult:
                {
                    LastItemReload = ItemReloadResult.Read(new Reader(payload, 0, payload.Length));
                    ItemReloadCount++;
                    break;
                }
                case OpCode.SRespawn:
                {
                    LastRespawn = Respawn.Read(new Reader(payload, 0, payload.Length));
                    RespawnCount++;
                    break;
                }
                case OpCode.SWorldItemSnapshot:
                {
                    var chunk = WorldItemSnapshotChunk.Read(new Reader(payload, 0, payload.Length));
                    WorldItemMirror.ApplySnapshotChunk(chunk);
                    break;
                }
                case OpCode.SWorldItemSpawn:
                {
                    var spawn = WorldItemSpawn.Read(new Reader(payload, 0, payload.Length));
                    WorldItemMirror.ApplySpawn(spawn.Info);
                    break;
                }
                case OpCode.SWorldItemDespawn:
                {
                    var despawn = WorldItemDespawn.Read(new Reader(payload, 0, payload.Length));
                    WorldItemMirror.ApplyDespawn(despawn.WorldItemId);
                    break;
                }
                case OpCode.SWorldItemPickup:
                {
                    var result = WorldItemPickupResult.Read(new Reader(payload, 0, payload.Length));
                    WorldItemMirror.ApplyPickupResult(result);
                    break;
                }
                case OpCode.SWorldItemDrop:
                {
                    var drop = WorldItemDropResult.Read(new Reader(payload, 0, payload.Length));
                    WorldItemMirror.ApplyDropResult(drop);
                    break;
                }
                case OpCode.SPlayerEvent:
                {
                    var playerEvent = PlayerEvent.Read(new Reader(payload, 0, payload.Length));
                    LastPlayerEvent = playerEvent;
                    PlayerEventCount++;
                    break;
                }
            }
        }

        public void Dispose() => _udp.Dispose();
    }
}

