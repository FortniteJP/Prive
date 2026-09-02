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

    /// <summary>
    ///     Holds the circle still at one radius and centre WHILE ALREADY ADVERTISING WHEN THE NEXT
    ///     SHRINK STARTS.
    ///
    ///     BOTH TIMES GET THE SAME VALUE, and that is not a shortcut - it is exactly what the real
    ///     server sent. The captured hold above carries
    ///     `SafeZoneStartShrinkTime = SafeZoneFinishShrinkTime = 260.7586` while the circle is still
    ///     sitting at 185000 with nothing shrinking. So a hold is a ZERO-LENGTH window placed at the
    ///     moment the next shrink begins: it tells the client what to count down to and simultaneously
    ///     says nothing is shrinking, and the real duration only arrives when the shrink actually
    ///     starts and the server rewrites Finish.
    ///
    ///     Two earlier versions of this got it wrong in opposite directions. Setting both to `now`
    ///     left the client counting down to a time in the past for the whole of every hold. Setting
    ///     Finish to Start + ShrinkTime looked more informative but is a window the real server never
    ///     sends, and a client that reads Finish &gt; Start as "a shrink is scheduled or running" would
    ///     be told the storm is already moving through the entire hold.
    ///
    ///     Last and Next are BOTH the current circle, so the client's Last-&gt;Next lerp produces no
    ///     movement whatever the times say - again exactly as in the capture, where Last and Next were
    ///     the same 185000 and the upcoming 80000 sat in NextNextRadius.
    /// </summary>
    public void HoldUntil(FVector centre, float radius, float nextShrinkStart) {
        LastCenter = Copy(centre);
        NextCenter = Copy(centre);
        LastRadius = radius;
        NextRadius = radius;
        Radius = radius;
        SafeZoneStartShrinkTime = nextShrinkStart;
        SafeZoneFinishShrinkTime = nextShrinkStart;
    }

    /// <summary>Holds with nothing further scheduled - the end of the plan.</summary>
    public void HoldAt(FVector centre, float radius, float now) => HoldUntil(centre, radius, now);

    /// <summary>
    ///     PUTS THE FORECAST CIRCLE UP: promotes NextNext into Next, WITHOUT touching the times.
    ///
    ///     This is the step this server was missing entirely, and it is the whole of why the map
    ///     never showed a circle to move towards. It only exists because of how the client draws
    ///     things, which the dump settles rather than leaves to guesswork - the indicator's own
    ///     update at 0x1412B3E0F does:
    ///
    ///         now = GameState-&gt;GetServerWorldTimeSeconds()
    ///         if (now &lt; SafeZoneStartShrinkTime)  -&gt; SetSafeZoneRadiusAndCenter(LastRadius, LastCenter)
    ///         else if (now &gt;= SafeZoneFinishShrinkTime) -&gt; ...(NextRadius, NextCenter)
    ///         else                                  -&gt; ...(lerp Last-&gt;Next)
    ///
    ///     So BEFORE the window opens the storm is pinned to LAST no matter what Next says. Next is
    ///     therefore free to hold the upcoming circle for the whole of the wait, and that is exactly
    ///     what the map draws as the white circle. Promoting it only when the shrink begins - which
    ///     is what this server did - makes the circle appear at the instant the storm starts moving,
    ///     i.e. never as a forecast at all. That is the reported symptom precisely.
    ///
    ///     It also explains the captured spawn state, which had Last == Next == 185000 with the
    ///     upcoming 80000 still parked in NextNext: that capture is the indicator's FIRST replication,
    ///     at match start, BEFORE the first circle has been announced. Which is right - in a real
    ///     match there is no white circle for the first minute either. What announces it is
    ///     AFortGameStateAthena::SafeZonesStartTime arriving, one Default.SafeZone.StartDelay later.
    /// </summary>
    public void AnnounceNext() {
        LastCenter = Copy(NextCenter);
        LastRadius = NextRadius;
        NextCenter = Copy(NextNextCenter);
        NextRadius = NextNextRadius;
    }

    /// <summary>
    ///     Opens (or re-schedules) the shrink window. Last and Next are NOT touched - by the time this
    ///     is called <see cref="AnnounceNext"/> has already put the target in Next and the client has
    ///     been drawing it for the whole wait. A zero-length window (start == finish) is a hold; see
    ///     <see cref="HoldUntil"/> for why that is the real server's own shape.
    /// </summary>
    public void SetShrinkWindow(float startTime, float finishTime) {
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
