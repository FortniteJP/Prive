using System.Runtime.CompilerServices;

namespace AFortOnlineBeacon.Core.Names;

internal static class FNameHelper {
    public const int NAME_NO_NUMBER_INTERNAL = 0;

    public const int NAME_NO_NUMBER = NAME_NO_NUMBER_INTERNAL - 1;

    public const int NAME_SIZE = 1024;
    
    public static FName MakeDetectNumber(string name, EFindName findType) {
        if (string.IsNullOrEmpty(name)) return new FName();

        var nameLen = name.Length;
        var internalNumber = ParseNumber(name, ref nameLen);
        return MakeWithNumber(name.Substring(0, nameLen), findType, (int)internalNumber);
    }

    private static uint ParseNumber(ReadOnlySpan<char> name, ref int nameLength) {
        var len = nameLength;
        var digits = 0;
        
        for (var i = len - 1; i >= 0 && name[i] >= '0' && name[i] <= '9'; --i) ++digits;

        var firstDigit = len - digits;
        const int maxDigitsInt32 = 10;
        if (digits != 0 && digits < len && name[firstDigit - 1] == '_' && digits <= maxDigitsInt32) {
            if (digits == 1 || name[firstDigit] != '0') {
                var number = long.Parse(name.Slice(len - digits, digits));
                if (number < int.MaxValue) {
                    nameLength -= 1 + digits;
                    return NAME_EXTERNAL_TO_INTERNAL((uint)number);
                }
            }
        }

        return NAME_NO_NUMBER_INTERNAL;
    }

    public static FName MakeWithNumber(string name, EFindName findType, int internalNumber) {
        if (name.Length == 0) return new FName();

        return Make(name, findType, internalNumber);
    }

    private static FName Make(string name, EFindName findType, int internalNumber) {
        if (name.Length >= NAME_SIZE) return new FName("ERROR_NAME_SIZE_EXCEEDED");

        FNameEntryId index;
        
        if (findType == EFindName.FNAME_Add) index = FNamePool.Store(name);
        else if (findType == EFindName.FNAME_Find) index = FNamePool.Find(name);
        else throw new NotImplementedException();

        return new FName(index, internalNumber);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint NAME_EXTERNAL_TO_INTERNAL(uint number) => number + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NAME_EXTERNAL_TO_INTERNAL(int number) => number + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint NAME_INTERNAL_TO_EXTERNAL(uint number) => number - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NAME_INTERNAL_TO_EXTERNAL(int number) => number - 1;
}