using AFortOnlineBeacon.Core.Math;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Where a door actually IS, and which way it faces - the half of a door interaction the client
///     never tells us.
///
///     ServerAttemptInteract carries an actor PATH and nothing else, so on its own it says which door
///     was touched but not where it stands. That is enough to open a door and not enough to open it
///     the RIGHT WAY: a door swings away from whoever opened it, and "away" is a question about which
///     side of the door's plane the player is on. See <see cref="Nearest" /> for the one wrinkle -
///     the same door name exists many times over.
/// </summary>
internal static partial class FortDoorPlacements {
    /// <summary>
    ///     The placement of <paramref name="actorName"/> nearest to <paramref name="near"/>, or null
    ///     if the map has no wall by that name.
    ///
    ///     PROXIMITY IS THE KEY, because the name is not unique and cannot be made unique. A house
    ///     kit is one sublevel placed at dozens of POIs, so 3129 of the 11,842 names are repeats - the
    ///     door the player just touched is `Rural_House_Wall_1` at fourteen different houses. Worse,
    ///     the path the client sends names the copy the ENGINE duplicated when the level streamed in
    ///     (`/Temp/Game/.../Athena_SUB_3x3_House_g2_2384084a`), and that `_2384084a` is generated at
    ///     runtime - there is nothing in the paks to match it against.
    ///
    ///     So the player's own position picks the row. This is not a heuristic in any way that
    ///     matters: the player is within interaction range (the client's PickingInteractDistance is
    ///     350 units) of the door they touched, and copies of one kit sit hundreds of metres apart.
    /// </summary>
    public static (FVector Location, float Yaw, bool Mirrored)? Nearest(string actorName, FVector near) {
        var best = float.MaxValue;
        (FVector, float, bool)? found = null;

        // A linear scan of 19,284 rows, once per door press. Deliberately not indexed: a dictionary
        // over the names would be built at startup for something that happens when a human walks up
        // to a door and presses a key, and this measures in microseconds.
        foreach (var (name, x, y, z, yaw, mirrored) in Placements) {
            if (!name.Equals(actorName, StringComparison.OrdinalIgnoreCase)) continue;

            var dx = x - near.X;
            var dy = y - near.Y;
            var dz = z - near.Z;
            var distanceSquared = dx * dx + dy * dy + dz * dz;

            if (distanceSquared >= best) continue;
            best = distanceSquared;
            found = (new FVector { X = x, Y = y, Z = z }, yaw, mirrored);
        }

        return found;
    }

    /// <summary>
    ///     Which way a door facing <paramref name="doorYaw"/> should swing for a player HEADING
    ///     <paramref name="interactorYaw"/>: +1 or -1. A door opens the way you are going.
    ///
    ///     The wall's own normal is its yaw plus 90 degrees - see <see cref="WallNormalOffset" />,
    ///     which is measured from three labelled live cases and is where the sign kept coming from
    ///     before that.
    ///
    ///     HEADING, NOT POSITION, and that change is the fix for "standing on the far side it
    ///     sometimes opens towards me". The first version compared the door's forward against the
    ///     vector from the door to the player, which is the textbook "which side are they on" test
    ///     and is genuinely unstable exactly where doors get used: a door actor's origin sits ON the
    ///     wall plane, so anyone standing in or beside the doorway - walking through, hugging the
    ///     frame - has a vector almost perpendicular to the wall's normal, and a dot product near
    ///     zero picks its sign from centimetres of stance. Which side of the wall someone is on is
    ///     also not really the question being asked: the question is which way they want to go.
    ///
    ///     The player's own facing has neither problem. Interaction is a trace from the camera, so
    ///     they are looking at the door by construction, and the dot of their forward with the wall's
    ///     forward is near +/-1 rather than near zero. It gives the same answer as the position test
    ///     in the ordinary case - someone standing in front of a door faces it - and a defined one in
    ///     the case that was failing.
    ///
    ///     A MIRRORED piece flips the answer: mirroring reverses the handedness of the local frame,
    ///     so the hinge moves to the other side and the same heading turns the other way. For a map
    ///     door that comes from the placement's scale (see the generated table); for a player-built
    ///     one it is the piece's own bMirrored, which the build tool already tracks.
    ///
    ///     WHAT IS DERIVED AND WHAT IS NOT. The heading is certain. Turning it into the sign of
    ///     DoorDesiredRotOffset.Yaw depends on which way the door mesh is hinged relative to its
    ///     actor's forward, which is a property of the ART and is the same for every door in the
    ///     game - one unknown bit, not one per door. That is the constant DOOR_OPEN_YAW's sign
    ///     flips: if doors swing INTO the player, negate it and every door is fixed at once.
    /// </summary>
    public static float SideOf(float doorYaw, float interactorYaw, bool mirrored) {
        var normal = (doorYaw + WallNormalOffset) * MathF.PI / 180f;
        var heading = interactorYaw * MathF.PI / 180f;

        // cos of the angle between them - positive when the player is heading along the wall's
        // normal, negative when heading against it. cos(a-b) expanded, so no vectors needed.
        var along = MathF.Cos(heading - normal);

        var side = along >= 0f ? 1f : -1f;
        return mirrored ? -side : side;
    }

    /// <summary>
    ///     A WALL'S NORMAL IS ITS YAW PLUS NINETY DEGREES, and this is measured rather than assumed.
    ///
    ///     Three labelled live cases on one player-built door (class PBWA_W1_DoorC, actor yaw 90,
    ///     not mirrored), each with the swing the server sent and whether it looked right:
    ///
    ///         heading 106  ->  +90 correct        cos(106-180) = +0.28  -> +1  -> +90   agrees
    ///         heading  33  ->  -90 correct        cos( 33-180) = -0.84  -> -1  -> -90   agrees
    ///         heading 288  ->  -90 correct        cos(288-180) = -0.31  -> -1  -> -90   agrees
    ///
    ///     Taking the yaw itself as the normal cannot fit them: headings 106 and 33 both give a
    ///     POSITIVE cosine against yaw 90, so it predicts the same sign for two cases whose right
    ///     answers are opposite. Only the +90 offset fits all three.
    ///
    ///     The player positions say the same thing independently. The two front-side cases sat at
    ///     X -116551.6 and -116544.9 (7 units apart) and Y -121437.0 and -121280.3 (157 apart), on a
    ///     door the player described as approached from the same face, once from the hinge side and
    ///     once from the other. Same distance from the wall, moved sideways: so the lateral axis is
    ///     Y and the normal is X, on a wall whose yaw is 90.
    ///
    ///     NOTE THE DISAGREEMENT, and do not "fix" it here: BuildingStructuralSupportSystem.BoxOf
    ///     builds a wall's collision box thin along the YAW axis, the opposite convention. That
    ///     system was measured from 370 real placements and works, so it is either right for its own
    ///     purpose or wrong in a way that cancels; either way it is not this feature's to change, and
    ///     changing it would move every player build's collision.
    ///
    ///     DOOR_WALL_NORMAL_OFFSET overrides it, for the next time a door behaves oddly.
    /// </summary>
    private static readonly float WallNormalOffset =
        float.TryParse(Environment.GetEnvironmentVariable("DOOR_WALL_NORMAL_OFFSET"), out var offset)
            ? offset
            : 90f;
}
