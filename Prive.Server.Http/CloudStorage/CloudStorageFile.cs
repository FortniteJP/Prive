using System.Security.Cryptography;
using System.Text;

namespace Prive.Server.Http.CloudStorage;

public abstract class CloudStorageFile {
    public abstract string Filename { get; }
    public virtual byte[] Data { get {
        if (_Data is null) _Data = Encoding.UTF8.GetBytes(string.Join("\n\n", Elements.Select(x => x.Serialize())));
        return _Data;
    } }
    private byte[]? _Data = null;
    public virtual List<IniElementSection> Elements { get; } = new();
    public virtual long Length => Data.Length;
    public virtual DateTime LastModified { get; } = DateTime.UtcNow;

    public string ComputeSHA1() {
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(Data));
    }

    public string ComputeSHA256() {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(Data));
    }
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
}

public enum IniElementOption {
    None,
    AddIfMissing,
    RemoveExact,
    AddNew,
    RemoveIfExisting
}

public class IniElementKeyValue : IniElement {
    public string Key { get; init; }
    public string? Value { get; init; }

    protected override string SerializeProperty() => $"{Key}={Value}";

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
    public required IniTextReplacementArguments TextReplacements { get; init; }
    protected override string SerializeProperty() => $"TextReplacements=(Category=\"{TextReplacements.Category}\", bIsMinimalPatch={TextReplacements.bIsMinimalPatch}, Namespace=\"{TextReplacements.Namespace}\", Key=\"{TextReplacements.Key}\", NativeString=\"{TextReplacements.NativeString}\", LocalizedStrings=({string.Join(",", TextReplacements.LocalizedStrings.Select(x => $"(\"{x.Key}\",\"{x.Value}\")"))}))";

    public class IniTextReplacementArguments {
        public string Category { get; init; } = "Game";
        public bool bIsMinimalPatch { get; init; } = true;
        public required string Namespace { get; init; }
        public required string Key { get; init; }
        public required string NativeString { get; init; }
        public Dictionary<string, string> LocalizedStrings { get; init; } = new();
    }
}