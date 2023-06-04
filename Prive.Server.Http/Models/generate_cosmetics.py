import requests

cosmetics = requests.get("https://fortnite-api.com/v2/cosmetics/br").json()

with open("Cosmetics.cs", "w") as f:
    f.write("namespace Prive.Server.Http;\n")
    f.write("\n")
    f.write("public static class Cosmetics {\n")
    f.write("    public static List<CosmeticItem> CosmeticItems { get; } = new() {\n")
    for cosmetic in cosmetics["data"]:
        f.write(f"        new() {{ BackendType = \"{cosmetic['type']['backendValue']}\", Id = \"{cosmetic['id']}\" }},\n")
    f.write("    };\n")
    f.write("}\n")
    f.write("\n")
    f.write("public class CosmeticItem {\n")
    f.write("    public required string Id { get; init; }\n")
    f.write("    public required string BackendType { get; init; }\n")
    f.write("}\n")