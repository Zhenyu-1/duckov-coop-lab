using System;
using System.Collections.Generic;

namespace DuckovCoop.Client
{
    /// <summary>
    /// T5: pure client-side view of server searchable containers (kind 3 map containers now,
    /// kind 5 corpse containers later). Handles CContainerOpen/SContainerContents and the
    /// unsolicited SInventorySnapshot(kind=3) pushes. No Unity dependency.
    /// </summary>
    public sealed class ContainerMirrorState
    {
        public const byte MapContainerKind = 3;
        public const byte CorpseContainerKind = 5;

        public sealed class View
        {
            public uint ContainerId;
            public uint Revision;
            public ushort Capacity;
            public List<InventoryEntry> Entries = new List<InventoryEntry>();
        }

        private readonly Dictionary<uint, View> _views = new Dictionary<uint, View>();

        public IReadOnlyDictionary<uint, View> Views => _views;
        public ContainerContents LastContents { get; private set; }
        public int ApplyCount { get; private set; }
        public int RejectedCount { get; private set; }

        public bool TryGet(uint containerId, out View view)
            => _views.TryGetValue(containerId, out view);

        /// <summary>Apply one SContainerContents reply. Invalid payloads keep the old view.</summary>
        public bool Apply(ContainerContents contents)
        {
            if (contents == null || contents.ContainerId == 0)
            {
                RejectedCount++;
                return false;
            }

            var view = BuildView(contents.ContainerId, contents.Revision, contents.Capacity,
                contents.Entries);
            if (view == null)
            {
                RejectedCount++;
                return false;
            }

            _views[contents.ContainerId] = view;
            LastContents = contents;
            ApplyCount++;
            return true;
        }

        /// <summary>
        /// Apply an unsolicited/solicited SInventorySnapshot. Only container kinds (3/5) are
        /// accepted; player kinds 0/1/2 stay in InventoryMirrorState.
        /// </summary>
        public bool ApplySnapshot(InventorySnapshot snapshot)
        {
            if (snapshot == null || snapshot.Kind != MapContainerKind &&
                snapshot.Kind != CorpseContainerKind)
                return false;
            if (snapshot.OwnerId == 0)
            {
                RejectedCount++;
                return false;
            }

            var view = BuildView(snapshot.OwnerId, snapshot.Revision, snapshot.Capacity,
                snapshot.Entries);
            if (view == null)
            {
                RejectedCount++;
                return false;
            }

            _views[snapshot.OwnerId] = view;
            ApplyCount++;
            return true;
        }

        public void Clear()
        {
            _views.Clear();
            LastContents = null;
            ApplyCount = 0;
            RejectedCount = 0;
        }

        private static View BuildView(uint containerId, uint revision, ushort capacity,
                                      InventoryEntry[] entries)
        {
            if (containerId == 0 || capacity == 0)
                return null;

            var view = new View
            {
                ContainerId = containerId,
                Revision = revision,
                Capacity = capacity,
            };
            if (entries == null)
                return view;

            var used = new HashSet<int>();
            foreach (var entry in entries)
            {
                if (entry.Slot >= capacity || entry.ItemUid == 0 || entry.ItemId == 0 ||
                    entry.Count == 0 || !used.Add(entry.Slot))
                    return null;
                view.Entries.Add(entry);
            }

            view.Entries.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
            return view;
        }
    }
}
