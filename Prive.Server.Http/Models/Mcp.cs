namespace Prive.Server.Http;

public class SetCosmeticLockerSlotRequest {
    [K("category")] public string? Category { get; set; }
    [K("itemToSlot")] public string? ItemToSlot { get; set; }
    [K("lockerItem")] public string? LockerItem { get; set; }
    [K("slotIndex")] public int SlotIndex { get; set; }
    [K("variantUpdates")] public List<VariantUpdate> VariantUpdates { get; set; } = new();
    [K("optLockerUseCountOverride")] public int? LockerUseCountOverride { get; set; } // ?
}

public class VariantUpdate {
    [K("active")] public required string Active { get; set; }
    [K("channel")] public required string Channel { get; set; }
    [K("owned")] public string[] Owned { get; set; } = Array.Empty<string>();

    public Variant ToVariant() => new() {
        Active = Active,
        Channel = Channel,
        Owned = Owned
    };

    public static VariantUpdate FromVariant(Variant variant) => new() {
        Active = variant.Active,
        Channel = variant.Channel,
        Owned = variant.Owned
    };
}

public class EquipBattleRoyaleCustomizationRequest {
    [K("slotName")] public string? SlotName { get; set; }
    [K("itemToSlot")] public string? ItemToSlot { get; set; }
    [K("indexWithinSlot")] public int IndexWithinSlot { get; set; }
}

public class PartyRequest {
    [K("config")] public required object Config { get; init; }
    [K("meta")] public required object Meta { get; init; }
    [K("join_info")] public required object JoinInfo { get; init; }
}
