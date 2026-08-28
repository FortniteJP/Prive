namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     GameplayAbilities.PredictionKey - the client's handle on a locally predicted action, so the
///     server's eventual answer can be matched back to the prediction the client already played.
///
///     Wire format is FPredictionKey::NetSerialize (GameplayPrediction.h), and it is conditional
///     rather than fixed:
///
///         1 bit  ValidKeyForConnection
///         1 bit  HasBaseKey          (only when ValidKeyForConnection)
///         1 bit  bIsServerInitiated
///         int16  Current             (only when ValidKeyForConnection)
///         int16  Base                (only when HasBaseKey)
///
///     Derived on paper and then confirmed against a real Project-Reboot-3.0 capture, where
///     ServerTryActivateAbility measured exactly 54 bits and successive shots carried
///     Current = 1, 2, 3 - an incrementing per-client counter, which is precisely what a prediction
///     key is.
/// </summary>
public sealed class FPredictionKey {
    public short Current { get; set; }
    public short Base { get; set; }
    public bool bIsServerInitiated { get; set; }

    /// <summary>False means the sender had no key to offer - Current/Base carry nothing on the wire.</summary>
    public bool bValidKeyForConnection { get; set; }

    /// <summary>
    ///     The load half. Note the middle bit is CONDITIONAL: HasBaseKey is only on the wire when the
    ///     key is valid for this connection, so reading it unconditionally shifts everything after
    ///     it - and in an FServerAbilityRPCBatch what follows is the target data.
    /// </summary>
    public static FPredictionKey NetSerializeRead(FArchive ar) {
        var key = new FPredictionKey { bValidKeyForConnection = ar.ReadBit() };

        var hasBaseKey = false;
        if (key.bValidKeyForConnection) hasBaseKey = ar.ReadBit();

        key.bIsServerInitiated = ar.ReadBit();

        if (key.bValidKeyForConnection) key.Current = (short) ar.ReadUInt16();
        if (hasBaseKey) key.Base = (short) ar.ReadUInt16();

        return key;
    }

    /// <summary>
    ///     The save half of FPredictionKey::NetSerialize. Conditional in the same places the read
    ///     side is: HasBaseKey and Current only exist when the key is valid for this connection.
    ///
    ///     A server echoing a client's key back must keep bIsServerInitiated FALSE - the key belongs
    ///     to the client's prediction window, and claiming the server started it would make the
    ///     client treat it as a different key entirely.
    /// </summary>
    public static void Write(FNetBitWriter writer, FPredictionKey key) {
        writer.WriteBit(key.bValidKeyForConnection);

        var hasBaseKey = key.bValidKeyForConnection && key.Base > 0;
        if (key.bValidKeyForConnection) writer.WriteBit(hasBaseKey);

        writer.WriteBit(key.bIsServerInitiated);

        if (key.bValidKeyForConnection) writer.WriteUInt16((ushort) key.Current);
        if (hasBaseKey) writer.WriteUInt16((ushort) key.Base);
    }

    public override string ToString() =>
        bValidKeyForConnection ? $"Current={Current} Base={Base} ServerInitiated={bIsServerInitiated}" : "(no key)";
}
