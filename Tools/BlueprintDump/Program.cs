using System.Reflection;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;

// Disassembles a Blueprint's compiled event graph (Kismet bytecode) into a readable, indented
// listing - the counterpart to Tools/BinXref for the HALF of Fortnite's logic that lives as
// interpreted UFunction bytecode rather than native x86. Native code review (memberxref/vtable/
// dumpxref) hits a wall the moment the call chain crosses into a Blueprint: there is no native
// exec thunk to follow, because there is no native implementation - CUE4Parse already parses a
// UFunction's raw Script byte array into a KismetExpression tree (UStruct.ScriptBytecode), so this
// tool only has to walk and print that tree, not write its own bytecode parser.
//
// Concretely for AFortOnlineBeacon: the client-side gate that shows a building ghost/preview has
// no native BlueprintNativeEvent to chase (unlike jump's CanJumpInternal), and the SDK's only
// build-preview-adjacent function (AFortPlayerController::LocalOverrideBuildMode) looks like a
// debug/cheat override, not the normal trigger. Whatever decides "start showing the preview" is
// either a Blueprint event graph this tool can now read, or pure native code with no reflection at
// all (which would need a live debugger instead).

if (args.Length < 3) {
    Console.WriteLine("Usage: BlueprintDump <PaksDirectory> <AesKeyHex> <AssetPath> [FunctionNameFilter] [EGame]");
    Console.WriteLine();
    Console.WriteLine("  AssetPath          a .uasset path (e.g. FortniteGame/Content/Athena/Athena_PlayerController.uasset)");
    Console.WriteLine("  FunctionNameFilter case-insensitive substring of the UFunction's name; default \"\" (every function");
    Console.WriteLine("                     with bytecode, including ExecuteUbergraph_* - the compiled event graph)");
    Console.WriteLine("  EGame              default GAME_UE4_23");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("""  BlueprintDump "C:\Fortnite\FortniteGame\Content\Paks" 0x3ff2... FortniteGame/Content/Athena/Athena_PlayerController.uasset""");
    return 1;
}

var paksDir = args[0];
var aesKey = args[1];
var assetPath = args[2];
var functionFilter = args.Length > 3 ? args[3] : string.Empty;
var gameVersion = args.Length > 4 ? Enum.Parse<EGame>(args[4]) : EGame.GAME_UE4_23;

if (!Directory.Exists(paksDir)) {
    Console.WriteLine($"PAK directory not found: {paksDir}");
    return 1;
}

var provider = new DefaultFileProvider(paksDir, SearchOption.TopDirectoryOnly, new VersionContainer(gameVersion));
// Off by default (most CUE4Parse consumers only want Properties). UStruct.Deserialize checks
// Ar.Owner.Provider.ReadScriptData before it will parse a UFunction's raw Script bytes into
// KismetExpression objects at all - without this, ScriptBytecode is silently null for every
// function, which looks exactly like "this Blueprint has no event graph" and is not that.
provider.ReadScriptData = true;
provider.Initialize();

foreach (var vfs in provider.UnloadedVfs.ToList()) {
    provider.SubmitKey(vfs.EncryptionKeyGuid, new FAesKey(aesKey));
}

Console.WriteLine($"Mounted files: {provider.Files.Count}");

List<CUE4Parse.UE4.Assets.Exports.UObject> exports;
try {
    exports = provider.LoadPackage(assetPath).GetExports().ToList();
} catch (Exception ex) {
    Console.WriteLine($"Failed to load '{assetPath}': {ex.GetType().Name}: {ex.Message}");
    return 1;
}

Console.WriteLine($"{exports.Count} export(s) in '{assetPath}'");

if (Environment.GetEnvironmentVariable("BPDUMP_DEBUG_TYPES") == "1") {
    foreach (var export in exports) {
        Console.WriteLine($"  {export.ExportType,-30} {export.GetType().FullName,-70} {export.Name}");
    }
}

var found = 0;

foreach (var export in exports) {
    // A UFunction export deserializes as the reflection-oriented CUE4Parse.UE4.Objects.UObject.
    // UFunction (a UStruct), NOT the generic Exports.UObject the rest of this project's tools use -
    // that is the one CUE4Parse type whose Deserialize path actually populates ScriptBytecode.
    if (export is not UFunction func) continue;

    if (Environment.GetEnvironmentVariable("BPDUMP_DEBUG_TYPES") == "1") {
        Console.WriteLine($"  UFunction {export.Name}: ScriptBytecode={(func.ScriptBytecode == null ? "null" : func.ScriptBytecode.Length.ToString())}");
    }

    if (func.ScriptBytecode is not { Length: > 0 } bytecode) continue;

    if (functionFilter.Length > 0 && !export.Name.Contains(functionFilter, StringComparison.OrdinalIgnoreCase)) continue;

    found++;
    Console.WriteLine();
    Console.WriteLine($"=== {export.Name} ({bytecode.Length} statement(s), flags={func.FunctionFlags}) ===");
    foreach (var stmt in bytecode) KismetPrinter.Print(stmt, 0);
}

Console.WriteLine();
Console.WriteLine(functionFilter.Length > 0
    ? $"{found} function(s) matched '{functionFilter}' and had bytecode."
    : $"{found} function(s) had bytecode.");

return 0;

/// <summary>
///     Reflection-driven printer for a CUE4Parse KismetExpression tree. There are 113 KismetExpression
///     subclasses (one per EExprToken) - rather than hand-writing a case for each, this walks whatever
///     public instance fields a concrete subclass DECLARES (reflection, not a switch), recursing into
///     any field that is itself a KismetExpression/KismetExpression[], and resolving the two reference
///     kinds that would otherwise print as opaque objects: FPackageIndex (an import/export index - this
///     is how a call to another function, or a reference to a class/property, is spelled in the
///     bytecode) via its already-resolved .Name, and FKismetPropertyPointer (a property reference,
///     e.g. what EX_Let/EX_Context assign into or read from) via its own ToString().
/// </summary>
internal static class KismetPrinter {
    public static void Print(object? expr, int depth) {
        if (expr == null) {
            Console.WriteLine($"{Indent(depth)}null");
            return;
        }

        if (expr is System.Collections.IEnumerable enumerable and not string) {
            var i = 0;
            foreach (var item in enumerable) {
                Console.WriteLine($"{Indent(depth)}[{i}]");
                Print(item, depth + 1);
                i++;
            }
            return;
        }

        var type = expr.GetType();

        // KismetExpression<T> (EX_IntConst, EX_ObjectConst, EX_NameConst, EX_ByteConst, ...) carries
        // its literal in .Value instead of a declared field - handle it before the generic field walk
        // below, since the generic type's own DeclaredOnly fields are empty.
        var valueProp = type.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        object? value = null;
        var hasValue = type.Name == "KismetExpression`1" && valueProp != null && TryGet(valueProp, expr, out value);

        Console.WriteLine($"{Indent(depth)}{type.Name}{Describe(hasValue, value)}");

        // NOT DeclaredOnly: EX_LocalVariable/EX_InstanceVariable's only field (Variable) is declared
        // on their shared base EX_VariableBase, so DeclaredOnly silently printed every variable
        // reference as a bare "EX_LocalVariable" with no name at all.
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
            var fv = field.GetValue(expr);
            if (fv == null) continue;

            var label = $"{Indent(depth + 1)}.{field.Name} = ";

            if (IsKismetExpression(field.FieldType) || IsKismetExpressionArray(field.FieldType)) {
                Console.WriteLine(label.TrimEnd());
                Print(fv, depth + 2);
                continue;
            }

            Console.WriteLine(label + FormatScalar(fv));
        }
    }

    private static bool TryGet(PropertyInfo prop, object instance, out object? value) {
        try {
            value = prop.GetValue(instance);
            return true;
        } catch {
            value = null;
            return false;
        }
    }

    private static string Describe(bool hasValue, object? value) => hasValue ? $" = {FormatScalar(value)}" : string.Empty;

    private static bool IsKismetExpression(Type t) => typeof(KismetExpression).IsAssignableFrom(t);

    private static bool IsKismetExpressionArray(Type t) =>
        t.IsArray && typeof(KismetExpression).IsAssignableFrom(t.GetElementType()!);

    /// <summary>
    ///     FPackageIndex.Name is the whole reason this tool is useful rather than a wall of numeric
    ///     indices: it resolves an import/export slot to the actual function/class/property NAME it
    ///     points at (e.g. a EX_FinalFunction.StackNode naming "GetBuildPreviewMarker"), the same
    ///     resolution CUE4Parse already does for every other tool in this project.
    /// </summary>
    private static string FormatScalar(object? v) {
        if (v == null) return "null";

        var t = v.GetType();
        if (t.Name == "FPackageIndex") {
            var name = t.GetProperty("Name")?.GetValue(v) as string;
            return string.IsNullOrEmpty(name) ? v.ToString() ?? "?" : name;
        }

        return v.ToString() ?? "?";
    }

    private static string Indent(int depth) => new string(' ', depth * 4);
}
