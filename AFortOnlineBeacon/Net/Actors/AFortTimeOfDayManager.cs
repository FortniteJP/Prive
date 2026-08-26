namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     AFortTimeOfDayManager - the actor Athena's loading screen is waiting for.
///
///     "Waiting for time of day manager" is one of the reasons Fortnite's loading-screen manager
///     refuses to drop the screen (the full list was read out of the client binary's .rdata around
///     "Garbage Collecting before dropping load screen"). Evidence it is the one that applies here:
///
///       - LogFortDayNight only ever fires for the Frontend world
///         ("AFortTimeOfDayManager::PostInitializeComponents: World is "Frontend", this is
///         "TODM_Disabled_C_2147482431""). The Athena match world never gets one.
///       - AFortGameStateBase has exactly two Net properties, and one of them is
///         FortTimeOfDayManager (0x02A0, Net + RepNotify) - so the server is expected to provide it.
///       - No TODM actor is placed in ANY map: not in the 866 .umap under Athena/Maps, nor the 2051
///         under Content/Maps (checked with Tools/MapActorDump). It is spawned at runtime.
///       - The client cannot make its own: the binary carries
///         "SetTimeOfDayManager: Called on non-authority, which cannot set the ToDM."
///
///     It derives from AInfo, i.e. an ordinary replicated actor with no transform of its own, so it
///     needs nothing but its own actor channel plus an ObjectRef at GameState handle 22 - the same
///     shape as AFortInventory. It has no replicated properties of its own that we need to send;
///     merely existing and being pointed at is the whole job.
/// </summary>
public class AFortTimeOfDayManager : AInfo {
}
