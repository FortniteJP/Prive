using System.Security.Cryptography;
using System.Text;

namespace Prive.Server.Http.CloudStorage;

public abstract class CloudStorageFile {
    public abstract string Filename { get; }
    public virtual byte[] Data { get {
        _Data ??= Encoding.UTF8.GetBytes(string.Join("\n\n", Elements.Select(x => x.Serialize())));
        return _Data;
    } }
    private byte[]? _Data = null;
    public virtual List<IniElementSection> Elements { get; } = new();
    public virtual long Length => Data.Length;
    public virtual DateTime LastModified { get; } = DateTime.UtcNow;

    public string ComputeSHA1() => Convert.ToHexString(SHA1.HashData(Data));

    public string ComputeSHA256() => Convert.ToHexString(SHA256.HashData(Data));
}

public class IniElementSection {
    public required string Section { get; init; }
    public List<IniElement> Elements { get; init; } = new();

    public string Serialize() => $"[{Section}]\n{string.Join("\n", Elements.Select(x => x.Serialize()))}";
}

public abstract class IniElement {
    public IniElementOption Option { get; init; } = IniElementOption.None;
    protected abstract string SerializeProperty();
    public virtual string Serialize() => $"{(GetOptionChar() is var c && c == ' ' ? "" : c)}{SerializeProperty()}";

    private char GetOptionChar() => Option switch {
        IniElementOption.AddIfMissing => '+',
        IniElementOption.RemoveExact => '-',
        IniElementOption.AddNew => '.',
        IniElementOption.RemoveIfExisting => '!',
        _ => ' '
    };
    public static string Escape(string str) => str.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    public static string ToLowerBool(bool b) => b ? "true" : "false";
}

public enum IniElementOption {
    None,
    AddIfMissing, // +
    RemoveExact, // =
    AddNew, // .
    RemoveIfExisting // !
}

public class IniElementKeyValue : IniElement {
    public string Key { get; init; }
    public string? Value { get; init; }

    protected override string SerializeProperty() => $"{Key}={Quote(Value ?? "")}";

    /// <summary>
    ///     Quotes a value on the same rule the engine's own ini writer uses
    ///     (FConfigFile::ShouldExportQuotedString). The one that matters here: <b>an unquoted
    ///     <c>//</c> in a value is a COMMENT to UE's ini parser</b> - ConfigCacheIni.cpp stops the
    ///     value at it - so <c>ServerAddr=ws://127.0.0.1</c> reached the client as <c>ws:</c> and
    ///     the XMPP login had no host to connect to. Quoting is what LawinServer's hand-written ini
    ///     was doing all along.
    /// </summary>
    static string Quote(string value) => NeedsQuotes(value) ? $"\"{value}\"" : value;

    static bool NeedsQuotes(string value) {
        if (value.Length == 0) return false;
        // Already a quoted string - a struct literal or a deliberately quoted URL. Wrapping it
        // again would nest the quotes and change the value.
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return false;

        if (value[0] == ' ' || value[^1] == ' ') return true;   // stripped on import
        if (value[0] == '"') return true;                       // read as a quoted string
        if (value[^1] == '\\') return true;                     // read as a line continuation

        // Braces and // only matter outside quotes: a value like +TextReplacements=(..., "http://x")
        // carries its own quoting, and the engine's scan tracks that too.
        var inQuotes = false;
        for (var i = 0; i < value.Length; i++) {
            if (value[i] == '"') inQuotes = !inQuotes;
            else if (inQuotes) continue;
            else if (value[i] == '{' || value[i] == '}') return true;
            else if (value[i] == '/' && i + 1 < value.Length && value[i + 1] == '/') return true;
        }
        return false;
    }

    public IniElementKeyValue() {
        if (Key is null) throw new ArgumentNullException(nameof(Key));
    }

    public IniElementKeyValue(string key, string? value = null) {
        Key = key;
        Value = value;
    }
}

public class IniElementFunc : IniElement {
    public required string Name { get; init; }
    public Dictionary<string, string> Properties { get; } = new();

    protected override string SerializeProperty() => $"{Name}=({string.Join(",", Properties.Select(x => $"{x.Key}={x.Value}"))})";

    public IniElementFunc() {}

    public IniElementFunc(string name) {
        Name = name;
    }
}

public class IniTextReplacements : IniElement {
    public required IniTextReplacementArguments TextReplacement { get; init; }
    protected override string SerializeProperty() => $"TextReplacements=(Category=\"{TextReplacement.Category}\", bIsMinimalPatch={ToLowerBool(TextReplacement.bIsMinimalPatch)}, Namespace=\"{TextReplacement.Namespace}\", Key=\"{TextReplacement.Key}\", NativeString=\"{Escape(TextReplacement.NativeString)}\", LocalizedStrings=({string.Join(",", TextReplacement.LocalizedStrings.Select(x => $"(\"{x.Key}\",\"{Escape(x.Value)}\")"))}))";

    public IniTextReplacements() {
        Option = IniElementOption.AddIfMissing;
    }
    
    public class IniTextReplacementArguments {
        public string Category { get; init; } = "Game";
        public bool bIsMinimalPatch { get; init; } = true;
        public required string Namespace { get; init; }
        public required string Key { get; init; }
        public required string NativeString { get; init; }
        public Dictionary<string, string> LocalizedStrings { get; init; } = new();
    }
}

public class IniFrontEndPlaylistData : IniElement {
    public required IniFrontEndPlaylistDataArguments FrontEndPlaylistData { get; init; }
    public static bool bUseS10Style { get; set; } = true;
    protected override string SerializeProperty() => bUseS10Style
        ? $"FrontEndPlaylistData=(PlaylistName=\"{FrontEndPlaylistData.PlaylistName}\", PlaylistAccess=(bIsDefaultPlaylist={ToLowerBool(FrontEndPlaylistData.PlaylistAccess.bIsDefaultPlaylist)}, bDisplayAsLimitedTime={ToLowerBool(FrontEndPlaylistData.PlaylistAccess.bDisplayAsLimitedTime)}, AdvertiseType={(FrontEndPlaylistData.PlaylistAccess.bDisplayAsNew ? "EPlaylistAdvertisementType::New" : FrontEndPlaylistData.PlaylistAccess.bDisplayAsLimitedTime ? "EPlaylistAdvertisementType::Updated" : "EPlaylistAdvertisementType::None")}, bForcePlaylistOff=false, bEnabled={ToLowerBool(FrontEndPlaylistData.PlaylistAccess.bEnabled)}, bVisibleWhenDisabled={ToLowerBool(FrontEndPlaylistData.PlaylistAccess.bVisibleWhenDisabled)}, CategoryIndex={FrontEndPlaylistData.PlaylistAccess.CategoryIndex}, DisplayPriority={FrontEndPlaylistData.PlaylistAccess.DisplayPriority}))"
        : $"FrontEndPlaylistData=(PlaylistName=\"{FrontEndPlaylistData.PlaylistName}\", PlaylistAccess=({string.Join(",", FrontEndPlaylistData.PlaylistAccess.GetType().GetProperties().Select(x => $"{x.Name}={x.GetValue(FrontEndPlaylistData.PlaylistAccess)}"))}))";

    public class IniFrontEndPlaylistDataArguments {
        public required string PlaylistName { get; init; }
        public IniPlaylistAccessArguments PlaylistAccess { get; init; } = new();

        public class IniPlaylistAccessArguments {
            public bool bEnabled { get; init; } = true;
            public bool bIsDefaultPlaylist { get; init; } = false;
            public bool bVisibleWhenDisabled { get; init; } = true;
            public bool bDisplayAsNew { get; init; } = false;
            public int CategoryIndex { get; init; } = 0;
            public bool bDisplayAsLimitedTime { get; init; } = false;
            public int DisplayPriority { get; init; } = 0;
        }
    }
}

public class IniRegionDefinitions : IniElement {
    public required IniRegionDefinitionArguments RegionDefinition { get; init; }
    protected override string SerializeProperty() => $"RegionDefinitions=(DisplayName=\"{Escape(RegionDefinition.DisplayName)}\", RegionId=\"{RegionDefinition.RegionId}\", bEnabled={RegionDefinition.bEnabled}, bVisible={RegionDefinition.bVisible}, bAutoAssignable={RegionDefinition.bAutoAssignable})";

    public IniRegionDefinitions() {
        Option = IniElementOption.AddIfMissing;
    }

    public class IniRegionDefinitionArguments {
        public required string DisplayName { get; init; }
        public required string RegionId { get; init; }
        public bool bEnabled { get; init; } = true;
        public bool bVisible { get; init; } = true;
        public bool bAutoAssignable { get; init; } = true;
    }
}

public class IniDisabledFrontendNavigationTabs : IniElement {
    public required IniDisabledFrontendNavigationTabArguments DisabledFrontendNavigationTab { get; init; }
    protected override string SerializeProperty() => $"DisabledFrontendNavigationTabs=(TabName=\"{DisabledFrontendNavigationTab.TabName}\", TabState={DisabledFrontendNavigationTab.TabState})";

    public IniDisabledFrontendNavigationTabs() {
        Option = IniElementOption.AddIfMissing;
    }

    public class IniDisabledFrontendNavigationTabArguments {
        public required string TabName { get; init; }
        public string TabState { get; init; } = "EFortRuntimeOptionTabState::Hidden";
    }
}