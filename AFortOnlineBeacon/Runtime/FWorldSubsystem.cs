namespace AFortOnlineBeacon.Runtime;

/// <summary>
///     Per-world state for a system that used to keep it in statics - the counterpart of UE's own
///     UWorldSubsystem, and the reason one process can run several worlds.
///
///     THE PATTERN. A system such as FortProjectileSystem stays a static class of FUNCTIONS, but owns
///     no data: everything that belongs to a match - the grenades in flight, the structural support
///     grid, the safe zone's phase - lives in a subclass of this, obtained with
///     <c>world.GetSubsystem&lt;T&gt;()</c>. Two worlds therefore get two of everything, and a world
///     that is disposed takes its state with it. Before this, the PROCESS boundary was the only thing
///     that ever reset any of it: even a single in-process world would have begun its second match
///     with the first match's grenades still in the air.
///
///     KNOBS BELONG HERE TOO. A tunable is read out of <see cref="Options" /> - this world's, not the
///     process's - and cached on the subsystem, which is created on first use and therefore always
///     after the world's init-only Options were set.
///
///     Created lazily and exactly once per world; see UWorld.GetSubsystem.
/// </summary>
public abstract class FWorldSubsystem {
    /// <summary>The world this belongs to. Set before <see cref="Initialize" /> runs.</summary>
    public UWorld World { get; internal set; } = null!;

    /// <summary>This world's tunables - see <see cref="FBeaconOptions" />.</summary>
    protected FBeaconOptions Options => World.Options;

    /// <summary>UWorldSubsystem::Initialize - runs once, with World already set.</summary>
    protected internal virtual void Initialize() {}
}
