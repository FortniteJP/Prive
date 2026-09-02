using System.Text;

namespace AFortOnlineBeacon.Core;

public static class FString {
    private const int MaxSerializeSize = 1024;

    /// <summary>
    ///     FString::operator&lt;&lt; - a length then the characters, where the SIGN of the length picks the
    ///     encoding: positive means one byte per character (ANSICHAR), negative means UCS2CHAR, i.e.
    ///     UTF-16 code units. Either way the count INCLUDES the null terminator.
    ///
    ///     The unicode branch used to take its length from the UTF-8 BYTE count and then write UTF-8
    ///     into a buffer sized for UTF-16 - wrong twice over, and mangling any value that was not pure
    ///     ASCII. <see cref="Deserialize"/> reads that same field back as UTF-16 (Encoding.Unicode,
    ///     saveNum * 2 bytes), so the two halves of this file did not even agree with each other; the
    ///     ASCII branch is the only one that had ever been exercised.
    ///
    ///     UCS2 counts UTF-16 CODE UNITS, which is exactly what C#'s string.Length is - surrogate
    ///     pairs counting as two - and exactly what UE writes for a TCHAR string.
    /// </summary>
    public static void Serialize(FArchive archive, string value) {
        var bSaveUnicodeChar = archive.IsForcingUnicode() || Encoding.UTF8.GetByteCount(value) != value.Length;
        if (bSaveUnicodeChar) {
            var num = value.Length + 1;   // UTF-16 code units, null terminator included
            archive.WriteInt32(-num);

            var valueBytesSize = num * 2;
            var valueBytes = valueBytesSize > 128 ? new byte[valueBytesSize] : stackalloc byte[valueBytesSize];

            Encoding.Unicode.GetBytes(value, valueBytes);   // the trailing two bytes stay zero

            if (archive.IsByteSwapping()) throw new NotImplementedException();
            archive.Serialize(valueBytes, valueBytesSize);
        } else {
            var num = value.Length;
            if (num != 0) {
                // Add null terminator.
                num += 1;
                
                var valueBytes = num > 128 ? new byte[num] : stackalloc byte[num];
            
                Encoding.ASCII.GetBytes(value, valueBytes);
            
                archive.WriteInt32(num);
                archive.Serialize(valueBytes, num);
            } else archive.WriteInt32(0);
        }
    }
    
    public static string Deserialize(FArchive archive) {
        // > 0 for ANSICHAR, < 0 for UCS2CHAR serialization

        string? result = null;

        var saveNum = archive.ReadInt32();
        var loadUcs2Char = saveNum < 0;
        if (loadUcs2Char) saveNum = -saveNum;

        // If SaveNum is still less than 0, they must have passed in MIN_INT. Archive is corrupted.
        if (saveNum < 0) throw new Exception("Archive is corrupted");

        // Protect against network packets allocating too much memory
        if (MaxSerializeSize > 0 && saveNum > MaxSerializeSize) throw new Exception("String is too large");

        if (saveNum != 0) {
            if (loadUcs2Char) {
                var bytes = archive.ReadBytes(saveNum * 2);

                // -2 to remove unicode null terminator.
                result = Encoding.Unicode.GetString(bytes, 0, bytes.Length - 2);
            } else {
                var bytes = archive.ReadBytes(saveNum);

                // -1 to remove null terminator.
                result = Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1);
            }

            // Throw away empty string.
            if (saveNum == 1) result = string.Empty;
        }

        return result ?? string.Empty;
    }
}