using System;
using System.Collections.Generic;

namespace DuckovCoop.Client
{
    /// <summary>
    /// Pure client-side view of the server ground-item ledger (T6).
    /// Handles SWorldItemSnapshot chunk assembly (out-of-order and empty worlds),
    /// incremental spawn/despawn, the backpack revision used by pickup intents and the
    /// correlation map between CWorldItemPickup and SWorldItemPickup.
    /// Deliberately has no Unity dependency so the wire behaviour is unit-testable.
    /// </summary>
    public sealed class WorldItemMirrorState
    {
        public const byte PickupOk = 0;
        public const byte PickupIdempotentReplay = 7;

        private readonly Dictionary<ushort, WorldItemInfo[]> _snapshotChunks =
            new Dictionary<ushort, WorldItemInfo[]>();
        private readonly HashSet<ushort> _snapshotReceived = new HashSet<ushort>();
        private readonly Dictionary<ulong, uint> _pendingPickupByCorrelation =
            new Dictionary<ulong, uint>();
        private readonly Dictionary<uint, ulong> _pendingPickupByItem =
            new Dictionary<uint, ulong>();
        private readonly HashSet<ulong> _pendingDropCorrelations = new HashSet<ulong>();

        private bool _snapshotInProgress;
        private bool _hasSnapshot;

        /// <summary>Current mirror contents keyed by server worldItemId.</summary>
        public readonly Dictionary<uint, WorldItemInfo> Items = new Dictionary<uint, WorldItemInfo>();

        /// <summary>Id of the snapshot being assembled or the last completed one.</summary>
        public uint SnapshotId { get; private set; }

        /// <summary>Chunk count of the snapshot being assembled or the last completed one.</summary>
        public ushort SnapshotChunkCount { get; private set; }

        /// <summary>Chunks received for the current/last snapshot (F2 chunks=x/y).</summary>
        public int ReceivedChunkCount =>
            _snapshotInProgress ? _snapshotReceived.Count : SnapshotChunkCount;

        /// <summary>Chunks still missing before the pending snapshot can be applied.</summary>
        public int PendingChunkCount =>
            _snapshotInProgress ? Math.Max(0, SnapshotChunkCount - _snapshotReceived.Count) : 0;

        /// <summary>Items of the last completed snapshot (diagnostics/tests).</summary>
        public IReadOnlyList<WorldItemInfo> LastSnapshotItems { get; private set; } =
            Array.Empty<WorldItemInfo>();

        /// <summary>Backpack revision from SInitialInventory/SInventorySnapshot; 0 = unknown.</summary>
        public uint BackpackRevision { get; private set; }

        public WorldItemPickupResult LastPickup { get; private set; }
        public uint LastPickupWorldItemId { get; private set; }
        public int PickupCount { get; private set; }
        public int PendingPickupCount => _pendingPickupByCorrelation.Count;

        /// <summary>T6b: last SWorldItemDrop result for diagnostics.</summary>
        public WorldItemDropResult LastDrop { get; private set; }

        /// <summary>T6b: number of SWorldItemDrop results applied (monotonic per session).</summary>
        public int DropCount { get; private set; }

        /// <summary>T6b: drop intents sent but not answered yet.</summary>
        public int PendingDropCount => _pendingDropCorrelations.Count;

        public int SnapshotCount { get; private set; }
        public int SpawnCount { get; private set; }
        public int DespawnCount { get; private set; }
        public int RejectedChunkCount { get; private set; }

        public bool IsPickupPending(uint worldItemId) => _pendingPickupByItem.ContainsKey(worldItemId);

        public bool TryGetPendingPickup(uint worldItemId, out ulong correlationId)
            => _pendingPickupByItem.TryGetValue(worldItemId, out correlationId);

        public void ApplyInitialInventory(InitialInventory initial)
        {
            if (initial == null)
                return;
            BackpackRevision = initial.Revision;
        }

        public void ApplyInventoryState(InventoryState state)
        {
            if (state?.Snapshot == null)
                return;
            if (state.Snapshot.Kind != (byte)InventoryKind.PlayerBackpack)
                return;
            BackpackRevision = state.Snapshot.Revision;
        }

        /// <summary>
        /// Feed one SWorldItemSnapshot chunk. Returns true exactly when this chunk completed
        /// a snapshot and replaced <see cref="Items"/>.
        /// </summary>
        public bool ApplySnapshotChunk(WorldItemSnapshotChunk chunk)
        {
            if (chunk == null)
                return false;

            // Duplicate of the snapshot we already applied: reliable channels should prevent
            // this, but replaying a chunk must not start a half-assembled snapshot.
            if (!_snapshotInProgress && _hasSnapshot && chunk.SnapshotId == SnapshotId)
                return false;
            // A late chunk of an older snapshot must never overwrite a newer mirror.
            if (_hasSnapshot && chunk.SnapshotId < SnapshotId)
            {
                RejectedChunkCount++;
                return false;
            }

            if (!_snapshotInProgress || chunk.SnapshotId != SnapshotId ||
                chunk.ChunkCount != SnapshotChunkCount)
            {
                StartSnapshot(chunk);
            }

            if (chunk.ChunkCount == 0)
            {
                // Empty world: a single chunk with chunkCount=0 clears the mirror.
                if (chunk.ChunkIndex != 0)
                {
                    RejectedChunkCount++;
                    return false;
                }
                CompleteSnapshot(Array.Empty<WorldItemInfo>());
                return true;
            }

            if (chunk.ChunkIndex >= chunk.ChunkCount)
            {
                RejectedChunkCount++;
                return false;
            }

            if (_snapshotChunks.ContainsKey(chunk.ChunkIndex))
                return false; // duplicate chunk index

            _snapshotChunks[chunk.ChunkIndex] = chunk.Items ?? Array.Empty<WorldItemInfo>();
            _snapshotReceived.Add(chunk.ChunkIndex);
            if (_snapshotReceived.Count < chunk.ChunkCount)
                return false;

            var items = new List<WorldItemInfo>();
            for (ushort index = 0; index < chunk.ChunkCount; index++)
            {
                if (_snapshotChunks.TryGetValue(index, out var entries))
                    items.AddRange(entries);
            }
            CompleteSnapshot(items);
            return true;
        }

        /// <summary>Incremental SWorldItemSpawn (also replaces an existing id).</summary>
        public bool ApplySpawn(WorldItemInfo info)
        {
            if (info == null || info.WorldItemId == 0)
                return false;
            Items[info.WorldItemId] = info;
            SpawnCount++;
            return true;
        }

        /// <summary>Incremental SWorldItemDespawn.</summary>
        public bool ApplyDespawn(uint worldItemId)
        {
            if (!Items.Remove(worldItemId))
                return false;
            DespawnCount++;
            return true;
        }

        /// <summary>
        /// Register a pickup intent before sending CWorldItemPickup. Fails for duplicate
        /// correlations, duplicate pending world items, or items no longer in the mirror.
        /// </summary>
        public bool TryRegisterPickup(ulong correlationId, uint worldItemId)
        {
            if (correlationId == 0 || worldItemId == 0)
                return false;
            if (!Items.ContainsKey(worldItemId))
                return false;
            if (_pendingPickupByCorrelation.ContainsKey(correlationId))
                return false;
            if (_pendingPickupByItem.ContainsKey(worldItemId))
                return false;

            _pendingPickupByCorrelation[correlationId] = worldItemId;
            _pendingPickupByItem[worldItemId] = correlationId;
            return true;
        }

        /// <summary>
        /// Apply SWorldItemPickup. Returns false for an unknown correlation. Success (0 or
        /// IdempotentReplay) removes the item from the mirror; failures keep it.
        /// </summary>
        public bool ApplyPickupResult(WorldItemPickupResult result)
        {
            if (result == null ||
                !_pendingPickupByCorrelation.TryGetValue(result.CorrelationId, out var worldItemId))
                return false;

            _pendingPickupByCorrelation.Remove(result.CorrelationId);
            _pendingPickupByItem.Remove(worldItemId);
            LastPickup = result;
            LastPickupWorldItemId = worldItemId;
            PickupCount++;

            // The server returns the post-pickup backpack revision; refresh it even on a
            // failure so a stale client can recover on the next attempt.
            if (result.BackpackRevision != 0)
                BackpackRevision = result.BackpackRevision;

            if (result.Code == PickupOk || result.Code == PickupIdempotentReplay)
                Items.Remove(worldItemId);
            return true;
        }

        /// <summary>
        /// T6b: register a drop intent before sending CWorldItemDrop. Returns false for a
        /// duplicate or zero correlation so a retransmit can never spend the same item twice.
        /// </summary>
        public bool TryRegisterDrop(ulong correlationId)
        {
            if (correlationId == 0)
                return false;
            return _pendingDropCorrelations.Add(correlationId);
        }

        /// <summary>
        /// T6b: apply SWorldItemDrop. Returns false for an unknown correlation (late/replayed
        /// result); the drop itself is already applied on the server, so the client only waits
        /// for the source snapshot and the broadcast SWorldItemSpawn.
        /// </summary>
        public bool ApplyDropResult(WorldItemDropResult result)
        {
            if (result == null || !_pendingDropCorrelations.Remove(result.CorrelationId))
                return false;

            LastDrop = result;
            DropCount++;
            return true;
        }

        /// <summary>Drop all raid-scoped state (new raid/resume/leave).</summary>
        public void ResetForRaid()
        {
            Items.Clear();
            _snapshotChunks.Clear();
            _snapshotReceived.Clear();
            _pendingPickupByCorrelation.Clear();
            _pendingPickupByItem.Clear();
            _pendingDropCorrelations.Clear();
            _snapshotInProgress = false;
            _hasSnapshot = false;
            SnapshotId = 0;
            SnapshotChunkCount = 0;
            LastSnapshotItems = Array.Empty<WorldItemInfo>();
            BackpackRevision = 0;
            LastDrop = null;
        }

        private void StartSnapshot(WorldItemSnapshotChunk chunk)
        {
            SnapshotId = chunk.SnapshotId;
            SnapshotChunkCount = chunk.ChunkCount;
            _snapshotChunks.Clear();
            _snapshotReceived.Clear();
            _snapshotInProgress = true;
        }

        private void CompleteSnapshot(IReadOnlyList<WorldItemInfo> items)
        {
            Items.Clear();
            foreach (var item in items)
            {
                if (item != null && item.WorldItemId != 0)
                    Items[item.WorldItemId] = item;
            }

            LastSnapshotItems = items;
            _snapshotChunks.Clear();
            _snapshotReceived.Clear();
            _snapshotInProgress = false;
            _hasSnapshot = true;
            SnapshotCount++;
        }
    }
}
