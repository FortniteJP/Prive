import requests

# Regenerates Cosmetics.cs - the "you own everything" grant that fills the athena profile.
#
# THE BACKEND TYPE IS NOT THE ONE fortnite-api.com REPORTS, and that is not a detail: a profile item's
# templateId is `<PrimaryAssetType>:<PrimaryAssetName>`, and the client resolves it through its OWN
# AssetManager. A type its AssetManager has never heard of resolves to nothing, so the item is granted,
# stored, returned in the profile - and simply does not exist as far as the UI is concerned.
#
# 10.40's FortniteGame/Config/DefaultGame.ini declares exactly ONE emote-shaped primary asset type:
#
#     PrimaryAssetType="AthenaDance", AssetBaseClass=/Script/FortniteGame.AthenaDanceItemDefinition,
#     Directories=(/Game/Athena/Items/Cosmetics/Dances,
#                  /Game/Athena/Items/Cosmetics/Sprays,
#                  /Game/Athena/Items/Cosmetics/Toys)
#
# Sprays and toys are scanned INTO AthenaDance - UAthenaSprayItemDefinition and
# UAthenaToyItemDefinition are subclasses of UAthenaDanceItemDefinition. The client's own log proves
# it names them that way: "ChangeBundleStateForPrimaryAssets: ... AthenaDance:SPID_000_TestSpray".
# The separate AthenaSpray / AthenaToy / AthenaEmoji types the API reports are from LATER seasons.
#
# That is why only dances ever appeared in the emote wheel: 575 sprays and 24 toys were granted under
# a type this client cannot resolve.
#
# Note the scan is RECURSIVE (bApplyRecursively=True), which is what puts EMOJIS in too: 10.40's 153
# of them live at /Game/Athena/Items/Cosmetics/Dances/**Emoji**/, a subfolder of Dances - not at
# Cosmetics/Emoji, which does not exist. Looking for the latter is how they got left out of the first
# version of this remap.
REMAP_TO_1040 = {
    "AthenaSpray": "AthenaDance",
    "AthenaToy": "AthenaDance",
    "AthenaEmoji": "AthenaDance",
}

cosmetics = requests.get("https://fortnite-api.com/v2/cosmetics/br").json()

with open("Cosmetics.cs", "w") as f:
    f.write("namespace Prive.Server.Http;\n")
    f.write("\n")
    f.write("public static class Cosmetics {\n")
    f.write("    public static List<CosmeticItem> CosmeticItems { get; } = new() {\n")
    for cosmetic in cosmetics["data"]:
        backend = cosmetic["type"]["backendValue"]
        backend = REMAP_TO_1040.get(backend, backend)
        f.write(f"        new() {{ BackendType = \"{backend}\", Id = \"{cosmetic['id']}\" }},\n")
    f.write("    };\n")
    f.write("}\n")
    f.write("\n")
    f.write("public class CosmeticItem {\n")
    f.write("    public required string Id { get; init; }\n")
    f.write("    public required string BackendType { get; init; }\n")
    f.write("}\n")
