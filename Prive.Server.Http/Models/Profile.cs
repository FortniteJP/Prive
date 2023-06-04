using MongoDB.Bson;

namespace Prive.Server.Http;

public class Profile {
    public ObjectId _id { get; set; }

    public required string AccountId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AthenaProfile : Profile {
    public int Level { get; set; } = 1;
    public string Banner { get; set; } = "AthenaBanner:OtherBanner2";
    public string BannerColor { get; set; } = "AthenaBannerColor:defaultcolor20";
    public string CharacterId { get; set; } = "AthenaCharacter:CID_001_Athena_Commando_F_Default";
    public Variant[] CharacterVariants { get; set; } = Array.Empty<Variant>();
    public string BackpackId { get; set; } = "";
    public Variant[] BackpackVariants { get; set; } = Array.Empty<Variant>();
    public string PickaxeId { get; set; } = "AthenaPickaxe:Pickaxe_Lockjaw";
    public Variant[] PickaxeVariants { get; set; } = Array.Empty<Variant>();
    public string GliderId { get; set; } = "";
    public Variant[] GliderVariants { get; set; } = Array.Empty<Variant>();
    public string SkyDiveContrailId { get; set; } = "";
    public Variant[] SkyDiveContrailVariants { get; set; } = Array.Empty<Variant>();
    public string LoadingScreenId { get; set; } = "";
    public string MusicPackId { get; set; } = "";
    public string[] Dances { get; set; } = new string[6] { "", "", "", "", "", "" };
    public string[] ItemWraps { get; set; } = new string[7] { "", "", "", "", "", "", "" };
}

public class CommonCoreProfile : Profile {
    public int VBucks { get; set; } = int.MaxValue;
    public string MtxPlatform { get; set; } = "EpicPC";
    public List<Gift> Gifts { get; set; } = new();
}

public class Variant {
    public required string Channel { get; set; }
    public required string Active { get; set; }
    public string[] Owned { get; set; } = Array.Empty<string>();
}

public class Gift {
    public required string FromAccountId { get; set; }
    public required string GiftId { get; set; } = GenerateToken();
    public required string BoxId { get; set; } = "GB_Default";
    public GiftElement[] Items { get; set; } = new GiftElement[1] { new() };
    public DateTime GiftedAt { get; set; } = DateTime.UtcNow;
    public string Message { get; set; } = "An awesome gift!";
}

public class GiftElement {
    public string ItemId { get; set; } = "BBID_DefaultBus";
    public string ProfileId { get; set; } = "Athena";
    public int quantity { get; set; } = 1;
}

public class ProfileChange {
    [K("changeType")] public string ChangeType { get; set; } = "fullProfileUpdate";
    [K("profile")] public required FortniteProfile Profile { get; set; }
}

public class FortniteProfile {
    [K("_id")] public required string Id { get; set; }
    [K("created")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [K("updated")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [K("rvn")] public int Revision { get; set; } = 1;
    [K("wipeNumber")] public int WipeNumber { get; set; } = 1;
    [K("accountId")] public required string AccountId { get; set; }
    [K("profileId")] public required string ProfileId { get; set; }
    [K("version")] public string Version { get; set; } = "Prive";
    [K("items")] public Dictionary<string, FortniteItem> Items { get; set; } = new();
    [K("stats")] public FortniteStats Stats { get; set; } = new();
    [K("commandRevision")] public int CommandRevision { get; set; } = 1;

    public class FortniteStats {
        [K("attributes")] public Dictionary<string, object> Attributes { get; set; } = new();
    }
}

public class FortniteItem {
    [K("templateId")] public required string TemplateId { get; set; }
    [K("attributes")] public Dictionary<string, object> Attributes { get; set; } = new();
    [K("quantity")] public int Quantity { get; set; } = 1;
}