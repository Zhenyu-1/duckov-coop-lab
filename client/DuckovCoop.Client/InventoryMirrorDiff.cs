namespace DuckovCoop.Client
{
    /// <summary>
    /// T4C.1: identity rule for native Item preservation during a per-kind snapshot apply.
    /// A native item is kept when the server entry still has the same kind, UID and itemId;
    /// the slot is only a location, so a moved item is relocated instead of rebuilt.
    /// Count changes are applied in place. Only a missing UID or a changed itemId forces a
    /// rebuild; the relocation path may still fall back to a rebuild when the native move
    /// itself fails.
    /// </summary>
    public static class InventoryMirrorDiff
    {
        public static bool CanKeep(InventoryMirrorState.Entry current,
                                   InventoryMirrorState.Entry desired)
        {
            return current != null && desired != null &&
                   current.Kind == desired.Kind &&
                   current.ItemUid == desired.ItemUid &&
                   current.ItemId == desired.ItemId;
        }
    }
}
