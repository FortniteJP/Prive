using MongoDB.Bson;

namespace Prive.Server.Http;

public class Profile {
    public ObjectId _id { get; set; }

    public required string AccountId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AthenaProfile : Profile {
    public int Level { get; set; } = 1;
    public string Banner { get; set; } = "OtherBanner2";
    public string BannerColor { get; set; } = "defaultcolor20";
    public string CharacterId { get; set; } = "CID_001_Athena_Commando_F_Default";
    public Variant[] CharacterVariants { get; set; } = Array.Empty<Variant>();
    public string BackpackId { get; set; } = "BID_001_Default";
    public Variant[] BackpackVariants { get; set; } = Array.Empty<Variant>();
    public string PickaxeId { get; set; } = "Pickaxe_Lockjaw";
    public Variant[] PickaxeVariants { get; set; } = Array.Empty<Variant>();
    public string GliderId { get; set; } = "Glider_ID_001_BlackKnight";
    public Variant[] GliderVariants { get; set; } = Array.Empty<Variant>();
    public string SkyDiveContrailId { get; set; } = "FX_ID_001_BlackKnight";
    public Variant[] SkyDiveContrailVariants { get; set; } = Array.Empty<Variant>();
    public string LoadingScreenId { get; set; } = "LSID_001_BlackKnight";
    public string MusicPackId { get; set; } = "MusicPack01";
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