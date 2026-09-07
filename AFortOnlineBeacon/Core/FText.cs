namespace AFortOnlineBeacon.Core;

/// <summary>
///     FText on the wire - the one property type this project had declared and never been able to
///     write.
///
///     `ERepPropertyKind.Text` has existed as a named kind with nothing behind it, and the vehicle
///     seat's `SeatChoiceDisplayText` is `Reserved(...)` for exactly that reason: "an FText is one
///     handle of a type nothing here can write". It is not exotic, it just had never been needed
///     badly enough to go and read the format.
///
///     WHAT IT UNLOCKS is more than a display string. Every arbitrary-text channel to the client is
///     an FText parameter: `AFortPlayerController::ClientSendMessage(FText, USoundBase*)`,
///     `ClientStartRespawnPreparation(..., FText HUDReasonText)` and
///     `ClientHideScreenWhileRespawning(FText HUDReasonText)`.
///
///     THE FORMAT, from UE 4.23's own source (Text.cpp:794, FText::SerializeText). An RPC parameter
///     of type FText has no NetSerializeItem - `UTextProperty` does not override it - so it falls
///     back to `UProperty::NetSerializeItem`, which runs the ordinary archive path below.
///
///         uint32 Flags
///         int8   HistoryType
///         bool   bHasCultureInvariantString      (32 bits - see WriteArchiveBool)
///         FString CultureInvariantString         (only when the bool is true)
///
///     A CULTURE-INVARIANT TEXT IS THE EASY CASE AND THE ONLY ONE NEEDED. `FText::SerializeText`
///     writes a history only when `!IsEmpty() && !IsCultureInvariant()`; anything built by
///     `FText::FromString` on a non-editor build is culture-invariant (Text.cpp:1033 sets
///     CultureInvariant outside the editor and InitializedFromString always), so it takes the short
///     branch: history type None, then the raw string. Nothing here has to model FTextHistory,
///     localisation namespaces or format arguments - and a server sending a literal never would.
/// </summary>
public static class FText {
    /// <summary>
    ///     ETextHistoryType::None (TextHistory.h:18). **-1, not 0** - the enum starts below zero and
    ///     Base is 0, so writing the obvious zero would claim a Base history that is not there and
    ///     the client would read the following bytes as one.
    /// </summary>
    private const sbyte HistoryTypeNone = -1;

    /// <summary>
    ///     ETextFlag::CultureInvariant (1 &lt;&lt; 1) | ETextFlag::InitializedFromString (1 &lt;&lt; 4) - what
    ///     `FText::FromString` leaves on a text outside the editor, which is what a server builds.
    /// </summary>
    private const uint FlagsFromString = (1u << 1) | (1u << 4);

    /// <summary>
    ///     Writes a literal string as an FText.
    ///
    ///     An EMPTY string is written as an empty FText rather than as a zero-length one: the source
    ///     tests `!IsEmpty() && IsCultureInvariant()` before writing the string at all, so an empty
    ///     text has no string field following the bool. Getting that wrong would desynchronise
    ///     everything after it in the same RPC.
    /// </summary>
    public static void Serialize(FArchive archive, string value) {
        archive.WriteUInt32(FlagsFromString);
        archive.WriteByte(unchecked((byte) HistoryTypeNone));

        var bHasCultureInvariantString = value.Length > 0;
        WriteArchiveBool(archive, bHasCultureInvariantString);

        if (bHasCultureInvariantString) FString.Serialize(archive, value);
    }

    /// <summary>
    ///     A bool through a plain FArchive is THIRTY-TWO BITS, not one.
    ///
    ///     `FArchive::operator&lt;&lt;(bool&)` serializes "as if it were UBOOL (legacy, 32 bit int)" - its
    ///     own comment. This is the one place an FText's encoding is likely to be got wrong from
    ///     memory, because every other bool this project writes to a bunch is a single bit: RPC
    ///     parameter bools, replicated bool properties and the presence bits are all
    ///     `WriteBit`. Those go through the RepLayout / RPC parameter path, which is bit-oriented;
    ///     this one is inside a struct being serialized by the ordinary archive operator, which is
    ///     not.
    /// </summary>
    private static void WriteArchiveBool(FArchive archive, bool value) => archive.WriteUInt32(value ? 1u : 0u);
}
