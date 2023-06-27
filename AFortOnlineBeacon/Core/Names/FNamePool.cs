namespace AFortOnlineBeacon.Core.Names;

public static class FNamePool {
    private static readonly object NamesLock = new object();
    
    private static readonly Dictionary<EName, FNameEntryId> HardcodedNames = new Dictionary<EName, FNameEntryId>();
    private static readonly Dictionary<FNameEntryId, EName> HardcodedNamesReverse = new Dictionary<FNameEntryId, EName>();

    private static readonly Dictionary<string, FNameEntryId> Names = new Dictionary<string, FNameEntryId>();
    private static readonly Dictionary<FNameEntryId, string> NamesReverse = new Dictionary<FNameEntryId, string>();

    private static uint _Counter;
    
    static FNamePool() {
        foreach (var (key, value) in UnrealNames.Names) {
            var index = Store(value);
            HardcodedNames[key] = index;
            HardcodedNamesReverse[index] = key;
        }
    }

    public static EName? FindEName(FNameEntryId index) {
        if (HardcodedNamesReverse.TryGetValue(index, out var result)) return result;

        return null;
    }

    public static FNameEntryId Find(string name) {
        if (Names.TryGetValue(name, out var result)) return result;

        return new FNameEntryId((uint)EName.None);
    }
    
    public static FNameEntryId Find(EName name) => HardcodedNames[name];

    public static string Resolve(FNameEntryId index) => NamesReverse[index];

    public static FNameEntryId Store(ReadOnlySpan<char> valueSpan) {
        var value = valueSpan.ToString();
        
        if (Names.TryGetValue(value, out var result)) return result;

        lock (NamesLock) {
            if (Names.TryGetValue(value, out result)) return result;
            
            result = new FNameEntryId(_Counter++);

            Names[value] = result;
            NamesReverse[result] = value;
        }

        return result;
    }
}