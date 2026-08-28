namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     GameplayAbilities.FServerAbilityRPCBatch (GameplayAbilityTypes.h:443) - one activation, its
///     target data and its end, folded into a single RPC by FScopedServerAbilityRPCBatcher.
///
///     This is the shape Fortnite's ranged fire ability actually uses: a weapon that batches never
///     sends ServerTryActivateAbility or ServerSetReplicatedTargetData at all, which a live capture
///     of this project's own server showed plainly (41 ServerAbilityRPCBatch and 40 ServerEndAbility
///     against 2 loose activations and zero loose target data).
///
///     The struct is ONE RPC parameter, so it gets ONE leading "send" bit - not one per member.
///     Inside, FRepLayout::SerializeProperties_r walks the cmds it built for the struct back to back
///     with no handles and no per-member presence bits:
///
///         int32   AbilitySpecHandle    (FGameplayAbilitySpecHandle is not a NetSerialize struct,
///                                       so it flattens to its single member)
///         ...     PredictionKey        (conditional - see FPredictionKey)
///         ...     TargetData           (see FGameplayAbilityTargetDataHandle)
///         1 bit   InputPressed
///         1 bit   Ended
///
///     Started is UPROPERTY(NotReplicated) and never reaches the wire.
/// </summary>
public class FServerAbilityRPCBatch {
    public int AbilitySpecHandle;
    public FPredictionKey PredictionKey = new();
    public FGameplayAbilityTargetDataHandle TargetData = new();
    public bool InputPressed;
    public bool Ended;

    /// <summary>Set when the members after the handle could not be read; the handle itself is still good.</summary>
    public bool bTailDecodeFailed;

    public static FServerAbilityRPCBatch NetSerializeRead(FArchive ar,
                                                          Func<uint, string?>? resolveByGuid = null) {
        var batch = new FServerAbilityRPCBatch {
            AbilitySpecHandle = ar.ReadInt32()
        };

        if (ar.IsError()) return batch;

        // AbilitySpecHandle comes first and is the ONLY member this server currently acts on: it is
        // what spends a round and what tells a reload from a shot. Everything after it is new and
        // derived rather than probed, so it is fenced off - a bad read here must not be able to cost
        // the caller a working trigger. The field's own bit count resynchronises the bunch either
        // way, so failing softly costs nothing beyond this one call's target data.
        try {
            batch.PredictionKey = FPredictionKey.NetSerializeRead(ar);
            batch.TargetData = FGameplayAbilityTargetDataHandle.NetSerializeRead(ar, resolveByGuid);

            // Reading these after a target data decode that stopped early would be reading someone
            // else's bits. The bools are worth far less than a wrong answer.
            if (ar.IsError() || batch.TargetData.bStoppedEarly) return batch;

            batch.InputPressed = ar.ReadBit();
            batch.Ended = ar.ReadBit();
        } catch (Exception ex) {
            batch.bTailDecodeFailed = true;
            Console.WriteLine($"FServerAbilityRPCBatch: handle {batch.AbilitySpecHandle} read, but the rest of the " +
                              $"batch did not decode: {ex.Message}");
        }

        return batch;
    }

    public override string ToString() =>
        $"Handle={AbilitySpecHandle} PredictionKey=[{PredictionKey}] InputPressed={InputPressed} " +
        $"Ended={Ended} TargetData=[{TargetData}]";
}
