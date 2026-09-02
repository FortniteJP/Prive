using AFortOnlineBeacon.Core.Math;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     The storm circle - AFortSafeZoneIndicator, spawned as
///     `/Game/Athena/SafeZone/SafeZoneIndicator.SafeZoneIndicator_C`.
///
///     EVERY HANDLE AND EVERY STARTING VALUE BELOW IS CONFIRMED AGAINST A REAL 10.40 SERVER, not
///     derived and hoped for. `Tools/RepHandles` produced the 32-handle layout, and packet #16837 of
///     the PR3.0 capture (PriveDev/PacketProxy/decoded_new.txt) was then hand-decoded bit by bit
///     against it - the payload parsed cleanly all the way to its handle-0 terminator, which it could
///     not have done if a single handle number or value width were wrong. What that real payload
///     contained:
///
///         16 LastRadius                          185000
///         17 NextRadius                          185000
///         18 NextNextRadius                       80000
///         19 LastCenter          (32256, -25600, 1536)
///         20 NextCenter          (32256, -25600, 1536)
///         21 NextNextCenter  (95371.1, -26509.3, 1536)
///         22 SafeZoneStartShrinkTime          260.7586
///         23 SafeZoneFinishShrinkTime         260.7586
///         29 MegaStormDelayTimeBeforeDestruction   0.75
///         32 Radius                            185000
///
///     Note what is NOT in it: handles 24-28 and 30-31 never appear, because they were still at their
///     class defaults. That is the whole set a real server actually sends, so it is the set this one
///     sends too.
///
///     THE CLIENT DRAWS AND INTERPOLATES THE CIRCLE. Between SafeZoneStartShrinkTime and
///     SafeZoneFinishShrinkTime it lerps Last->Next for both radius and centre; outside that window it
///     just holds. So a phase is expressed by writing the NEXT pair and the two times, not by moving
///     Radius every tick - and Radius (32) is the authoritative current value the server also keeps
///     for its own damage test.
/// </summary>
public class AFortSafeZoneIndicator : AActor {
    /// <summary>Wire handle 16 - the radius the current shrink starts from.</summary>
    public float LastRadius { get; set; }

    /// <summary>Wire handle 17 - the radius the current shrink ends at.</summary>
    public float NextRadius { get; set; }

    /// <summary>Wire handle 18 - the radius AFTER the current one, which is what the map's preview circle draws.</summary>
    public float NextNextRadius { get; set; }

    /// <summary>Wire handle 19 - FVector_NetQuantize100.</summary>
    public FVector LastCenter { get; set; } = new();

    /// <summary>Wire handle 20 - FVector_NetQuantize100.</summary>
    public FVector NextCenter { get; set; } = new();

    /// <summary>Wire handle 21 - FVector_NetQuantize100.</summary>
    public FVector NextNextCenter { get; set; } = new();

    /// <summary>Wire handle 22, in the same match-clock seconds as AGameState.ReplicatedWorldTimeSeconds.</summary>
    public float SafeZoneStartShrinkTime { get; set; }

    /// <summary>Wire handle 23. Equal to the start time while the circle is holding still.</summary>
    public float SafeZoneFinishShrinkTime { get; set; }

    /// <summary>Wire handle 29. The real server sent 0.75; kept because it is one of the few it sends at all.</summary>
    public float MegaStormDelayTimeBeforeDestruction { get; set; } = 0.75f;

    /// <summary>Wire handle 32 - the current radius, and what this server's own damage test uses.</summary>
    public float Radius { get; set; }

    /// <summary>Puts the circle in a holding state at one radius and centre - no shrink in progress.</summary>
    public void HoldAt(FVector centre, float radius, float now) {
        LastCenter = Copy(centre);
        NextCenter = Copy(centre);
        LastRadius = radius;
        NextRadius = radius;
        Radius = radius;
        SafeZoneStartShrinkTime = now;
        SafeZoneFinishShrinkTime = now;
    }

    /// <summary>
    ///     Starts a shrink. The client does the interpolation from here; this server only needs to keep
    ///     <see cref="Radius"/> in step for its own out-of-zone test, which FortSafeZoneSystem does.
    /// </summary>
    public void BeginShrink(FVector toCentre, float toRadius, float startTime, float finishTime) {
        LastCenter = Copy(NextCenter);
        LastRadius = NextRadius;
        NextCenter = Copy(toCentre);
        NextRadius = toRadius;
        SafeZoneStartShrinkTime = startTime;
        SafeZoneFinishShrinkTime = finishTime;
    }

    /// <summary>
    ///     Where the circle is right now, by the same lerp the client runs. Kept server-side purely so
    ///     the damage test agrees with what the player can see.
    /// </summary>
    public void UpdateCurrentRadius(float now) {
        var span = SafeZoneFinishShrinkTime - SafeZoneStartShrinkTime;
        if (span <= 0f || now <= SafeZoneStartShrinkTime) {
            Radius = LastRadius;
            return;
        }

        if (now >= SafeZoneFinishShrinkTime) {
            Radius = NextRadius;
            return;
        }

        var t = (now - SafeZoneStartShrinkTime) / span;
        Radius = LastRadius + (NextRadius - LastRadius) * t;
    }

    /// <summary>The centre right now, lerped the same way - the circle MOVES as well as shrinking.</summary>
    public FVector CurrentCentre(float now) {
        var span = SafeZoneFinishShrinkTime - SafeZoneStartShrinkTime;
        if (span <= 0f || now <= SafeZoneStartShrinkTime) return Copy(LastCenter);
        if (now >= SafeZoneFinishShrinkTime) return Copy(NextCenter);

        var t = (now - SafeZoneStartShrinkTime) / span;
        return new FVector {
            X = LastCenter.X + (NextCenter.X - LastCenter.X) * t,
            Y = LastCenter.Y + (NextCenter.Y - LastCenter.Y) * t,
            Z = LastCenter.Z + (NextCenter.Z - LastCenter.Z) * t
        };
    }

    /// <summary>FVector is a reference type here, so every assignment above has to copy or two fields alias.</summary>
    private static FVector Copy(FVector v) => new() { X = v.X, Y = v.Y, Z = v.Z };
}
