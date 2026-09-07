using AFortOnlineBeacon.Net;

namespace AFortOnlineBeacon.Core;

/// <summary>
///     Checks FText's wire layout byte for byte.
///
///     WHY IT EXISTS: this encoding has never been sent to a real client, and if it is wrong the
///     failure is silent in the worst way - the FText is the FIRST parameter of ClientSendMessage,
///     so a wrong length there does not corrupt a string, it desynchronises everything after it in
///     the same RPC. There is no error to read, only a message that does not appear.
///
///     The single most likely mistake is the bool. Every other bool this project writes into a bunch
///     is ONE BIT; this one is inside a struct going through the ordinary archive operator, where
///     `FArchive::operator&lt;&lt;(bool&)` serializes it "as if it were UBOOL (legacy, 32 bit int)". The
///     expected bytes below are what pin that down.
/// </summary>
public static class FTextSelfTest {
    public static bool RunSelfTest() {
        var failures = 0;

        // "Hi" is ASCII, so FString takes the positive-length branch: 3 (two characters plus the
        // null terminator), then the bytes.
        //
        //   12 00 00 00   Flags = CultureInvariant | InitializedFromString
        //   FF            HistoryType = None (-1)
        //   01 00 00 00   bHasCultureInvariantString, as a 32-BIT archive bool
        //   03 00 00 00   FString length, null terminator included
        //   48 69 00      'H' 'i' '\0'
        failures += Check("Hi", "120000 00FF01 00000003 000000486900");

        // EMPTY IS NOT A ZERO-LENGTH STRING. FText::SerializeText only writes the string when
        // `!IsEmpty() && IsCultureInvariant()`, so an empty text ends after the bool - no length
        // field at all. Writing a 0 length here would leave four bytes the client reads as the next
        // parameter.
        failures += Check("", "1200000 0FF00000000");

        Console.WriteLine(failures == 0
            ? "FTextSelfTest: flags, the -1 history type, the 32-bit archive bool and the trailing " +
              "FString all match UE 4.23's FText::SerializeText. OK."
            : $"FTextSelfTest: {failures} FAILURE(S).");

        return failures == 0;
    }

    private static int Check(string value, string expectedHex) {
        var expected = expectedHex.Replace(" ", string.Empty).ToUpperInvariant();

        var writer = new FNetBitWriter(null!, 1024);
        FText.Serialize(writer, value);

        var actual = Convert.ToHexString(writer.GetData()[..(int) ((writer.GetNumBits() + 7) / 8)]);

        if (actual == expected) return 0;

        Console.WriteLine($"FTextSelfTest: FText(\"{value}\") serialised as {actual}, expected {expected}.");
        return 1;
    }
}
