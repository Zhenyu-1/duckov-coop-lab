using System;

namespace DuckovCoop.Client
{
    /// <summary>
    /// Server equipment slot order (T3 contract) mapped to native Slot.Key values.
    /// The native SlotCollection physical order is not guaranteed to match, so transfer and
    /// apply code must resolve slots by key first and only fall back to collection order for
    /// unknown keys.
    /// </summary>
    public static class InventorySlotMap
    {
        public const int EquipmentCapacity = 10;

        public static readonly string[] Keys =
        {
            "PrimaryWeapon",
            "SecondaryWeapon",
            "MeleeWeapon",
            "Armor",
            "Helmat",
            "FaceMask",
            "Backpack",
            "Headset",
            "Totem1",
            "Totem2",
        };

        public static string KeyAt(int index)
            => index >= 0 && index < Keys.Length ? Keys[index] : null;

        /// <summary>Server equipment slot index for a native Slot.Key; -1 for unknown keys.</summary>
        public static int IndexOfKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return -1;
            for (var i = 0; i < Keys.Length; i++)
            {
                if (string.Equals(Keys[i], key, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }
    }
}
