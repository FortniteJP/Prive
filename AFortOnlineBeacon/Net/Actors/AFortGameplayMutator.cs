namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     AFortGameplayMutator : AInfo - one actor per gameplay modifier the playlist turns on.
///
///     WHERE THE LIST COMES FROM, and why it is not copied out of the capture. The PR3.0 capture
///     spawns three (Mutator_SpecialEvent_C, Mutator_PinkDeimosEncounter_C, ContextTutorial_Mutator_C
///     - packets #318/#319, channels 8-10) and it would have been easy to hard-code those three. Two
///     of them are wrong for this server: they come from whatever event configuration that session
///     was running, not from its playlist. The mutators a playlist spawns are a property OF the
///     playlist, so they are read from the playlist this server actually runs:
///
///         Playlist_DefaultSolo.ModifierList = [ MaterialDropOnElim_Default,
///                                               ContextTutorial_AthenaContextTutorial ]
///         MaterialDropOnElim_Default.Mutators           = [ Showdown_DropItemsOnDeath_C ]
///         ContextTutorial_AthenaContextTutorial.Mutators = [ ContextTutorial_Mutator_C ]
///
///     (read with `pakreader props`/`exports` - a UFortGameplayModifierItemDefinition is a level of
///     indirection between the playlist and the mutator class). ContextTutorial_Mutator_C is the one
///     the capture and this derivation agree on, which is the check that the derivation is right.
///
///     WHAT REACHES THE CLIENT. Role and RemoteRole, and nothing else - byte for byte the real
///     server's 53-bit blocks. bMutatorActive (handle 16, RepNotify) is deliberately NOT sent:
///     ContextTutorial_Mutator_C's CDO already has it True, and a RepLayout only carries what differs
///     from the archetype, so sending it would be MORE than the real server does, not less. The
///     client constructs the actor from its own CDO and the mutator is active on arrival.
///
///     WHAT THIS DOES AND DOES NOT BUY. The mutator's behaviour lives in its Blueprint, and a
///     Blueprint runs where the actor exists. ContextTutorial_Mutator_C is a client-facing one - it
///     owns the 30 contextual tutorial tips, their 20-second cooldown and the sound cue that plays
///     with them - so the client half really does start working once the actor arrives.
///     Showdown_DropItemsOnDeath_C is the opposite: it is server logic (drop these three items when
///     a player dies), and this server cannot run its graph. It is spawned anyway because the
///     playlist says it exists and the client's own mutator list should match the server's; the
///     dropping itself would have to be reimplemented in FortDamageSystem to actually happen.
///
///     One C# type covers every mutator class, so instances are created with
///     GUClassArray.StaticClassForPath - the same split the weapons already use, where the C# type
///     picks the RepLayout and the path picks what the client spawns.
/// </summary>
public class AFortGameplayMutator : AInfo {

    /// <summary>
    ///     Referenced from the GameState in real Fortnite (through the playlist's modifier list), so
    ///     every client needs it - and unlike a pickup there is no position to cull on.
    /// </summary>
    public AFortGameplayMutator() => bAlwaysRelevant = true;

    /// <summary>The Blueprint class path this instance was spawned as - for logging only.</summary>
    public string MutatorClassPath { get; init; } = string.Empty;

    /// <summary>
    ///     Playlist name -> the mutator classes that playlist turns on, read out of the paks by the
    ///     chain in this class's doc comment.
    ///
    ///     A table rather than a runtime pak read, matching every other generated table in this
    ///     project (FortWarmupStarts, FortLootTables, FortHarvestResources): the paks are 60 GB and
    ///     what is needed from them is a handful of constants. One playlist is in it because one
    ///     playlist is what this server runs; PLAYLIST_ASSET pointing somewhere else falls through
    ///     to no mutators, which is the honest answer rather than the wrong one.
    /// </summary>
    private static readonly Dictionary<string, string[]> PlaylistMutators = new() {
        ["Playlist_DefaultSolo"] = new[] {
            // From MaterialDropOnElim_Default. Server-side logic this project cannot run - see the
            // class comment - spawned so the client's mutator list matches the server's.
            "/Game/Athena/Playlists/Showdown/Showdown_DropItemsOnDeath.Showdown_DropItemsOnDeath_C",
            // From ContextTutorial_AthenaContextTutorial. Client-side, and the one the PR3.0 capture
            // independently confirms.
            "/Game/Athena/Playlists/ContextTutorial/Mutator/ContextTutorial_Mutator.ContextTutorial_Mutator_C"
        }
    };

    /// <summary>
    ///     The mutator class paths for a playlist asset path (as PLAYLIST_ASSET gives it), or none.
    ///     PLAYLIST_MUTATORS overrides with a comma-separated list; an empty string spawns none,
    ///     which is what makes this switchable off without a rebuild if it ever misbehaves.
    /// </summary>
    public static IReadOnlyList<string> ForPlaylist(string playlistAssetPath) {
        if (Environment.GetEnvironmentVariable("PLAYLIST_MUTATORS") is { } raw)
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // "/Game/Athena/Playlists/Playlist_DefaultSolo.Playlist_DefaultSolo" -> "Playlist_DefaultSolo"
        var name = playlistAssetPath;
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];

        return PlaylistMutators.TryGetValue(name, out var mutators) ? mutators : Array.Empty<string>();
    }
}
