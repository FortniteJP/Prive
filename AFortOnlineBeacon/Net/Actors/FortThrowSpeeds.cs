namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     How fast a thrown item leaves the hand, asked of its own ability.
///
///     One constant - the frag grenade's 4000 - used to serve every throw. It is right for most of
///     them and wrong where it shows: a firework mortar throws at 2500 and a clinger at 6000, so at
///     4000 the first sailed past everything and the second fell short. Nine abilities in the game
///     override the family's number; everything else really does inherit 4000, so an item missing
///     from the table is answered rather than unanswered.
/// </summary>
internal static partial class FortThrowSpeeds {
    /// <summary>
    ///     The speed for an item, by way of its fire ability - or the family default when the ability
    ///     does not override it.
    ///
    ///     The lookup goes through the ABILITY because that is where the property lives; the item
    ///     names the ability and never carries a speed of its own.
    /// </summary>
    public static float For(string? itemName, float familyDefault) {
        if (itemName == null) return familyDefault;
        if (FortConsumables.AbilityFor(itemName) is not { } abilityPath) return familyDefault;

        // "/Game/.../GA_Athena_FireworksMortar_WithTrajectory.GA_Athena_FireworksMortar_WithTrajectory_C"
        var className = abilityPath[(abilityPath.LastIndexOf('.') + 1)..];

        foreach (var row in Rows) {
            if (row.Ability.Equals(className, StringComparison.OrdinalIgnoreCase)) return row.Min;
        }

        return familyDefault;
    }
}
