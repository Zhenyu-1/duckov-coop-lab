using System;
using System.Collections.Generic;

namespace DuckovCoop.Client
{
    /// <summary>
    /// T4A/T4B: pure client-side model of the server's three fixed inventories. It validates
    /// and orders SInitialInventory entries (equipment/backpack/petBag), applies
    /// SInventorySnapshot replacements per kind, keeps the serverUid map and exposes stable
    /// per-kind lists plus per-kind revisions for transfer intents. No Unity dependency.
    /// </summary>
    public sealed class InventoryMirrorState
    {
        public const int EquipmentCapacity = 10;
        public const int BackpackCapacity = 20;
        public const int PetBagCapacity = 6;

        public sealed class Entry
        {
            public byte Kind;
            public int Slot;
            public uint ItemUid;
            public uint ItemId;
            public uint Count;

            public Entry Clone() => new Entry
            {
                Kind = Kind,
                Slot = Slot,
                ItemUid = ItemUid,
                ItemId = ItemId,
                Count = Count,
            };
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Dictionary<uint, Entry> _byUid = new Dictionary<uint, Entry>();
        private readonly HashSet<int> _usedSlots = new HashSet<int>();
        private readonly Dictionary<byte, int> _kindCounts = new Dictionary<byte, int>();
        private readonly uint[] _kindRevisions = new uint[3];
        private readonly int[] _capacities = { EquipmentCapacity, BackpackCapacity, PetBagCapacity };

        /// <summary>Initial payload revision (SInitialInventory); 0 before the first apply.</summary>
        public uint Revision { get; private set; }
        public string TicketId { get; private set; } = string.Empty;
        public IReadOnlyList<Entry> Entries => _entries;
        public IReadOnlyDictionary<uint, Entry> ByUid => _byUid;
        public int RejectedCount { get; private set; }
        public int DuplicateUidCount { get; private set; }
        public int DuplicateSlotCount { get; private set; }
        public int InvalidEntryCount { get; private set; }
        public int SlotOutOfRangeCount { get; private set; }
        public int SnapshotApplyCount { get; private set; }
        public bool HasData => _entries.Count > 0;

        public int EquipmentCount => CountOf(0);
        public int BackpackCount => CountOf(1);
        public int PetBagCount => CountOf(2);

        public int CountOf(byte kind) => _kindCounts.TryGetValue(kind, out var count) ? count : 0;

        /// <summary>First free target slot of a kind (used by click-unload), -1 when full.</summary>
        public int FindFirstFreeSlot(byte kind)
        {
            var capacity = CapacityOfKind(kind);
            if (capacity <= 0)
                return -1;

            var used = new bool[capacity];
            foreach (var entry in _entries)
            {
                if (entry.Kind == kind && entry.Slot >= 0 && entry.Slot < capacity)
                    used[entry.Slot] = true;
            }
            for (var slot = 0; slot < capacity; slot++)
            {
                if (!used[slot])
                    return slot;
            }
            return -1;
        }

        /// <summary>Per-container revision used as CItemTransfer.expectedRevision.</summary>
        public uint GetRevision(byte kind) => kind < 3 ? _kindRevisions[kind] : 0;

        public static int CapacityOf(byte kind)
        {
            switch (kind)
            {
                case 0: return EquipmentCapacity;
                case 1: return BackpackCapacity;
                case 2: return PetBagCapacity;
                default: return 0;
            }
        }

        /// <summary>Current per-kind capacity from SInitialInventory/SInventorySnapshot.</summary>
        public int CapacityOfKind(byte kind) => kind < 3 ? _capacities[kind] : 0;

        public void SetCapacity(byte kind, int capacity)
        {
            if (kind < 3 && capacity > 0)
                _capacities[kind] = Math.Min(capacity, 64);
        }

        /// <summary>
        /// Replace the mirror with a full SInitialInventory. Invalid entries are skipped and
        /// counted; valid entries are kept in stable (kind, slot) order.
        /// </summary>
        public bool Apply(InitialInventory initial)
        {
            Clear();
            if (initial == null)
                return false;

            Revision = initial.Revision;
            TicketId = initial.TicketId ?? string.Empty;
            _kindRevisions[0] = initial.Revision;
            _kindRevisions[1] = initial.Revision;
            _kindRevisions[2] = initial.Revision;
            SetCapacity(0, initial.EquipmentCapacity);
            SetCapacity(1, initial.BackpackCapacity);
            SetCapacity(2, initial.PetBagCapacity);

            if (initial.Entries != null)
            {
                foreach (var row in initial.Entries)
                {
                    AddEntry(new Entry
                    {
                        Kind = row.Kind,
                        Slot = row.Entry.Slot,
                        ItemUid = row.Entry.ItemUid,
                        ItemId = row.Entry.ItemId,
                        Count = row.Entry.Count,
                    });
                }
            }
            SortEntries();
            return true;
        }

        /// <summary>
        /// T4B: replace one kind from SInventorySnapshot (after a transfer/pickup) while keeping
        /// the other two kinds and updating that kind's revision.
        /// </summary>
        public bool ApplySnapshot(InventorySnapshot snapshot)
        {
            if (snapshot == null || snapshot.Kind > 2)
                return false;

            var kind = snapshot.Kind;
            SetCapacity(kind, snapshot.Capacity);
            var kept = new List<Entry>();
            foreach (var entry in _entries)
            {
                if (entry.Kind != kind)
                    kept.Add(entry.Clone());
            }

            ResetEntries();
            foreach (var entry in kept)
                AddEntry(entry);

            if (snapshot.Entries != null)
            {
                foreach (var row in snapshot.Entries)
                {
                    AddEntry(new Entry
                    {
                        Kind = kind,
                        Slot = row.Slot,
                        ItemUid = row.ItemUid,
                        ItemId = row.ItemId,
                        Count = row.Count,
                    });
                }
            }

            SortEntries();
            _kindRevisions[kind] = snapshot.Revision;
            SnapshotApplyCount++;
            return true;
        }

        public bool TryGet(uint itemUid, out Entry entry) => _byUid.TryGetValue(itemUid, out entry);

        public void Clear()
        {
            ResetEntries();
            Revision = 0;
            TicketId = string.Empty;
            _kindRevisions[0] = 0;
            _kindRevisions[1] = 0;
            _kindRevisions[2] = 0;
            _capacities[0] = EquipmentCapacity;
            _capacities[1] = BackpackCapacity;
            _capacities[2] = PetBagCapacity;
        }

        private void ResetEntries()
        {
            _entries.Clear();
            _byUid.Clear();
            _usedSlots.Clear();
            _kindCounts.Clear();
            RejectedCount = 0;
            DuplicateUidCount = 0;
            DuplicateSlotCount = 0;
            InvalidEntryCount = 0;
            SlotOutOfRangeCount = 0;
        }

        private void AddEntry(Entry entry)
        {
            if (!IsValid(entry))
                return;

            var slotKey = (entry.Kind << 16) | entry.Slot;
            if (!_usedSlots.Add(slotKey))
            {
                DuplicateSlotCount++;
                RejectedCount++;
                return;
            }
            if (_byUid.ContainsKey(entry.ItemUid))
            {
                DuplicateUidCount++;
                RejectedCount++;
                _usedSlots.Remove(slotKey);
                return;
            }

            _byUid[entry.ItemUid] = entry;
            _entries.Add(entry);
            _kindCounts[entry.Kind] = CountOf(entry.Kind) + 1;
        }

        private void SortEntries()
        {
            _entries.Sort(static (a, b) =>
            {
                var kind = a.Kind.CompareTo(b.Kind);
                return kind != 0 ? kind : a.Slot.CompareTo(b.Slot);
            });
        }

        private bool IsValid(Entry entry)
        {
            if (entry.Kind > 2 || entry.ItemUid == 0 || entry.ItemId == 0 || entry.Count == 0)
            {
                InvalidEntryCount++;
                RejectedCount++;
                return false;
            }

            var capacity = CapacityOfKind(entry.Kind);
            if (entry.Slot < 0 || entry.Slot >= capacity)
            {
                SlotOutOfRangeCount++;
                RejectedCount++;
                return false;
            }

            return true;
        }
    }
}
