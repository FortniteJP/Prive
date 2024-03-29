using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using System.Text.Json;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("fortnite")]
public class MCPController : ControllerBase {
    public static object CreateResponse(object changes, string profileId, int rvn = 0) => new {
        profileRevision = rvn,
        profileId = profileId,
        profileChangesBaseRevision = rvn,
        profileChanges = changes,
        profileCommandRevision = rvn,
        serverTime = DateTimeOffset.UtcNow.ToString(DateTimeFormat),
        responseVersion = 1
    };

    [HttpPost("api/game/v2/profile/{accountId}/client/QueryProfile")]
    public async Task<object> QueryProfile() {
        var accountId = (string)Request.RouteValues["accountId"]!;
        var profileId = Request.Query["profileId"].First()!;
        var rvn = int.Parse(Request.Query["rvn"].First() ?? "0") + 1;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"fortnite:profile:{accountId}:commands", "ALL", "fortnite");
        }

        switch (profileId) {
            case "athena":
                var athenaProfile = await DB.GetAthenaProfile(accountId);
                if (athenaProfile is null) {
                    athenaProfile = new() { AccountId = accountId };
                    await DB.AthenaProfiles.InsertOneAsync(athenaProfile);
                }
                var athenaChange = CreateProfileChange(athenaProfile);
                athenaChange.Profile.Revision = rvn;
                athenaChange.Profile.CommandRevision = rvn;
                return CreateResponse(new[] { athenaChange }, profileId, rvn);
            case "profile0":
                var profile0Profile = await DB.GetAthenaProfile(accountId);
                var profile0Change = CreateProfileChange(profile0Profile);
                profile0Change.Profile.Revision = rvn;
                profile0Change.Profile.CommandRevision = rvn;
                return CreateResponse(new[] { profile0Change }, profileId, rvn);
            case "common_core":
                var commonCoreProfile = await DB.GetCommonCoreProfile(accountId);
                if (commonCoreProfile is null) {
                    commonCoreProfile = new() { AccountId = accountId };
                    await DB.CommonCoreProfiles.InsertOneAsync(commonCoreProfile);
                }
                var commonCoreChange = CreateProfileChange(commonCoreProfile);
                commonCoreChange.Profile.Revision = rvn;
                commonCoreChange.Profile.CommandRevision = rvn;
                return CreateResponse(new[] { commonCoreChange }, profileId, rvn);
            case "creative":
            case "common_public":
            case "collection_book_schematics0":
            case "collection_book_people0":
            case "metadata":
            case "theater0":
            case "outpost0":
                return CreateResponse(new object[0], profileId, rvn);
            default:
                Response.StatusCode = 400;
                return EpicError.Create(
                    "errors.com.epicgames.modules.profiles.operation_forbidden", 12813,
                    $"Unable to find template configuration for profile {profileId}",
                    "fortnite", "prod-live", new[] { profileId }
                );
        }
    }

    [HttpPost("api/game/v2/profile/{accountId}/client/ClientQuestLogin")]
    public Task<object> ClientQuestLogin() => QueryProfile();

    [HttpPost("api/game/v2/profile/{accountId}/client/SetMtxPlatform")]
    public async Task<object> SetMtxPlatform() {
        var accountId = (string)Request.RouteValues["accountId"]!;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"fortnite:profile:{accountId}:commands", "ALL", "fortnite");
        }
        using var bodyReader = new StreamReader(Request.Body);
        var platformValue = JsonSerializer.Deserialize<Dictionary<string, string>>(await bodyReader.ReadToEndAsync())?.GetValueOrDefault("platform");

        return CreateResponse(new object[] { new {
            changeType =  "statModified",
            name = "current_mtx_platform",
            value = platformValue ?? "EpicPC"
        }}, "common_core", int.Parse(Request.Query["rvn"].First() ?? "0"));
    }

    [HttpPost("api/game/v2/profile/{accountId}/client/SetCosmeticLockerSlot")]
    public async Task<object> SetCosmeticLockerSlot() {
        var accountId = (string)Request.RouteValues["accountId"]!;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"fortnite:profile:{accountId}:commands", "ALL", "fortnite");
        }

        using var bodyReader = new StreamReader(Request.Body);
        var setCosmeticLockerSlotRequest = JsonSerializer.Deserialize<SetCosmeticLockerSlotRequest>(await bodyReader.ReadToEndAsync())!;

        if (setCosmeticLockerSlotRequest.Category is null || setCosmeticLockerSlotRequest.ItemToSlot is null || setCosmeticLockerSlotRequest.LockerItem is null) {
            Response.StatusCode = 400;
            return EpicError.Create(
                "errors.com.epicgames.validation.validation_failed", 1040,
                "Validation Failed.", // Invalid fields were []
                "fortnite", "prod-live", new string[0]
            );
        }

        var validFields = new string[] {
            "Backpack",
            "VictoryPose",
            "LoadingScreen",
            "Character",
            "Glider",
            "Dance",
            "CallingCard",
            "ConsumableEmote",
            "MapMarker",
            "Charm",
            "SkyDiveContrail",
            "Hat",
            "PetSkin",
            "ItemWrap",
            "MusicPack",
            "BattleBus",
            "Pickaxe",
            "VehicleDecoration",
        };

        if (!validFields.Contains(setCosmeticLockerSlotRequest.Category)) {
            Response.StatusCode = 400;
            return EpicError.Create(
                "errors.com.epicgames.modules.profiles.invalid_payload", 12806,
                "Unable to parse command com.epicgames.fortnite.core.game.commands.cosmetics.SetCosmeticLockerSlot. Value not one of declared Enum instance names", // [{string.join(", ", validFields)}]
                "fortnite", "prod-live", new string[] { "Unable to parse command com.epicgames.fortnite.core.game.commands.cosmetics.SetCosmeticLockerSlot. Value not one of declared Enum instance names" }
            );
        }

        var filter = Builders<AthenaProfile>.Filter.Eq("AccountId", accountId);
        switch (setCosmeticLockerSlotRequest.Category) {
            case "Dance":
            case "ItemWrap":
                if (setCosmeticLockerSlotRequest.SlotIndex == -1) {
                    var items = new List<string>();
                    var n = setCosmeticLockerSlotRequest.Category == "Dance" ? 6 : 7;
                    for (var i = 0; i < n; i++) {
                        var x = setCosmeticLockerSlotRequest.ItemToSlot.Split(':');
                        items.Add($"{x[0]}:{x[1].ToLowerInvariant()}");
                    }
                    await DB.AthenaProfiles.UpdateOneAsync(filter, Builders<AthenaProfile>.Update.Set(setCosmeticLockerSlotRequest.Category + "s", items));
                } else {
                    await DB.AthenaProfiles.UpdateOneAsync(filter, Builders<AthenaProfile>.Update.Set($"{setCosmeticLockerSlotRequest.Category}s.$[{setCosmeticLockerSlotRequest.SlotIndex}]", setCosmeticLockerSlotRequest.ItemToSlot));
                }
                break;
            default:
                await DB.AthenaProfiles.UpdateOneAsync(filter, Builders<AthenaProfile>.Update.Set(setCosmeticLockerSlotRequest.Category, setCosmeticLockerSlotRequest.ItemToSlot));
                break;
        }

        if (setCosmeticLockerSlotRequest.VariantUpdates.Count > 0) await DB.AthenaProfiles.UpdateOneAsync(filter, Builders<AthenaProfile>.Update.Set($"{setCosmeticLockerSlotRequest.Category}Variants", setCosmeticLockerSlotRequest.VariantUpdates));

        var athenaProfile = await DB.GetAthenaProfile(accountId);
        return CreateResponse(new[] { new {
            changeType = "itemAttrChanged",
            itemId = setCosmeticLockerSlotRequest.LockerItem,
            attributeName = "locker_slots_data",
            attributeValue = new {
                slots = new {
                    Glider = new { items = new string[] { athenaProfile.GliderId } },
                    Dance = new { items = athenaProfile.Dances },
                    SkyDiveContrail = new { items = new string[] { athenaProfile.SkyDiveContrailId } },
                    LoadingScreen = new { items = new string[] { athenaProfile.LoadingScreenId } },
                    Pickaxe = new {
                        items = new string[] { athenaProfile.PickaxeId },
                        activeVariants = new object?[] {
                            athenaProfile.PickaxeVariants.Length > 0 ? new {
                                variants = athenaProfile.PickaxeVariants
                            } : null
                        }
                    },
                    ItemWrap = new { items = athenaProfile.ItemWraps },
                    MusicPack = new { items = new string[] { athenaProfile.MusicPackId } },
                    Character = new {
                        items = new string[] { athenaProfile.CharacterId },
                        activeVariants = new object?[] {
                            athenaProfile.CharacterVariants.Length > 0 ? new {
                                variants = athenaProfile.CharacterVariants
                            } : null
                        }
                    },
                    Backpack = new {
                        items = new string[] { athenaProfile.BackpackId },
                        activeVariants = new object?[] {
                            athenaProfile.BackpackVariants.Length > 0 ? new {
                                variants = athenaProfile.BackpackVariants
                            } : null
                        }
                    },
                }
            }
        } }, "athena", int.Parse(Request.Query["rvn"].FirstOrDefault() ?? "0"));
    }

    [HttpPost("api/game/v2/profile/{accountId}/client/EquipBattleRoyaleCustomization")]
    public async Task<object> EquipBattleRoyaleCustomization() {
        var accountId = (string)Request.RouteValues["accountId"]!;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"fortnite:profile:{accountId}:commands", "ALL", "fortnite");
        }

        using var bodyReader = new StreamReader(Request.Body);
        var equipBattleRoyaleCustomizationRequest = JsonSerializer.Deserialize<EquipBattleRoyaleCustomizationRequest>(await bodyReader.ReadToEndAsync())!;

        if (equipBattleRoyaleCustomizationRequest.SlotName is null || equipBattleRoyaleCustomizationRequest.ItemToSlot is null) {
            Response.StatusCode = 400;
            return EpicError.Create(
                "errors.com.epicgames.validation.validation_failed", 1040,
                "Validation Failed.",
                "fortnite", "prod-live", new string[0]
            );
        }

        var validFields = new string[] {
            "Backpack",
            "VictoryPose",
            "LoadingScreen",
            "Character",
            "Glider",
            "Dance",
            "CallingCard",
            "ConsumableEmote",
            "MapMarker",
            "Charm",
            "SkyDiveContrail",
            "Hat",
            "PetSkin",
            "ItemWrap",
            "MusicPack",
            "BattleBus",
            "Pickaxe",
            "VehicleDecoration",
        };
        if (!validFields.Contains(equipBattleRoyaleCustomizationRequest.SlotName)) {
            Response.StatusCode = 400;
            return EpicError.Create(
                "errors.com.epicgames.modules.profiles.invalid_payload", 12806,
                "Unable to parse command com.epicgames.fortnite.core.game.commands.cosmetics.EquipBattleRoyaleCustomization. Value not one of declared Enum instance names", // [{string.join(", ", validFields)}]
                "fortnite", "prod-live", new string[] { "Unable to parse command com.epicgames.fortnite.core.game.commands.cosmetics.EquipBattleRoyaleCustomization. Value not one of declared Enum instance names" }
            );
        }

        switch (equipBattleRoyaleCustomizationRequest.SlotName) {
            case "ItemWrap":
            case "Dance":
                if (equipBattleRoyaleCustomizationRequest.IndexWithinSlot == -1) {
                    var max = equipBattleRoyaleCustomizationRequest.SlotName == "Dance" ? 6 : 7;
                    var r = new List<string>();
                    for (var i = 0; i < max; i++) r.Add(equipBattleRoyaleCustomizationRequest.ItemToSlot);
                    await DB.AthenaProfiles.UpdateOneAsync(Builders<AthenaProfile>.Filter.Eq("AccountId", accountId), Builders<AthenaProfile>.Update.Set(equipBattleRoyaleCustomizationRequest.SlotName + "s", r));
                } else {
                    await DB.AthenaProfiles.UpdateOneAsync(Builders<AthenaProfile>.Filter.Eq("AccountId", accountId), Builders<AthenaProfile>.Update.Set($"{equipBattleRoyaleCustomizationRequest.SlotName}s.{equipBattleRoyaleCustomizationRequest.IndexWithinSlot}", equipBattleRoyaleCustomizationRequest.ItemToSlot));
                }
                break;
            default:
                await DB.AthenaProfiles.UpdateOneAsync(Builders<AthenaProfile>.Filter.Eq("AccountId", accountId), Builders<AthenaProfile>.Update.Set($"{equipBattleRoyaleCustomizationRequest.SlotName}Id", equipBattleRoyaleCustomizationRequest.ItemToSlot));
                break;
        }

        var profile = await DB.GetAthenaProfile(accountId);
        if (equipBattleRoyaleCustomizationRequest.SlotName == "ItemWrap" || equipBattleRoyaleCustomizationRequest.SlotName == "Dance") {
            return CreateResponse(new object[] {
                new {
                    changeType = "statModified",
                    name = $"favorite_{equipBattleRoyaleCustomizationRequest.SlotName}",
                    value = profile.GetType().GetProperty($"{equipBattleRoyaleCustomizationRequest.SlotName}s")!.GetValue(profile)
                }
            }, "athena", int.Parse(Request.Query["rvn"].FirstOrDefault() ?? "0") + 1);
        } else {
            return CreateResponse(new object[] {
                new {
                    changeType = "statModified",
                    name = $"favorite_{equipBattleRoyaleCustomizationRequest.SlotName}",
                    value = equipBattleRoyaleCustomizationRequest.ItemToSlot
                }
                // variants
                // new {
                //     changeType = "itemAttrChanged",
                //     itemId = profile.GetType().GetProperty($"{equipBattleRoyaleCustomizationRequest.SlotName}Id")!.GetValue(profile),
                //     attributeName = "variants",
                //     attributeValue = profile.GetType().GetProperty($"{equipBattleRoyaleCustomizationRequest.SlotName}Variants")!.GetValue(profile)
                // }
            }, "athena", int.Parse(Request.Query["rvn"].FirstOrDefault() ?? "0") + 1);
        }
    }
}

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