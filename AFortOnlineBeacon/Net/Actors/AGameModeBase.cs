namespace AFortOnlineBeacon.Net.Actors;

public class AGameModeBase : AInfo {
    public AGameModeBase() => OptionsString = string.Empty;
    
    /// <summary>
    ///     Save options string and parse it when needed
    /// </summary>
    public string OptionsString { get; set; }
    
    public AGameSession? GameSession { get; set; }

    public virtual void InitGame(string mapName, string options, out string errorMessage) {
        // Default error.
        errorMessage = string.Empty;
        
        // Find world.
        var world = GetWorld();
        
        // Save Options for future use
        OptionsString = options;

        var spawnInfo = new FActorSpawnParameters {
            Instigator = GetInstigator(),
            ObjectFlags = EObjectFlags.RF_Transient
        };

        GameSession = world!.SpawnActor<AGameSession>(GUClassArray.StaticClass<AGameSession>(), spawnInfo);
    }

    public virtual void InitGameState() {
        throw new NotImplementedException();
    }

    public void PreLogin(string options, string address, FUniqueNetIdRepl uniqueId, out string? errorMessage) {
        // Login unique id must match server expected unique id type OR No unique id could mean game doesn't use them
        errorMessage = null;
    }

    public APlayerController? Login(UPlayer newPlayer, ENetRole inRemoteRole, string portal, string options, FUniqueNetIdRepl uniqueId, out string errorMessage) {
        errorMessage = string.Empty;

        if (GameSession == null)
        {
            errorMessage = "Failed to spawn player controller, GameSession is null";
            return null;
        }

        errorMessage = GameSession.ApproveLogin(options);

        if (!string.IsNullOrEmpty(errorMessage)) return null;

        var world = GetWorld();
        if (world == null) {
            errorMessage = "Failed to spawn player controller, World is null";
            return null;
        }

        var spawnInfo = new FActorSpawnParameters {
            ObjectFlags = EObjectFlags.RF_Transient
        };

        var newPlayerController = world.SpawnActor<APlayerController>(GUClassArray.StaticClass<APlayerController>(), spawnInfo);
        if (newPlayerController == null) errorMessage = "Failed to spawn player controller";

        return newPlayerController;
    }

    public void PostLogin(APlayerController newPlayer) {
        var world = GetWorld();
        if (world == null) return;

        // Simplified stand-in for AGameModeBase::RestartPlayer. We don't have Fortnite's own Pawn
        // class yet (needs a real class path from SDK dumps), so this spawns the generic native
        // Pawn - enough to prove the actor-channel/NetGUID path end-to-end, but with no mesh and
        // no movement replication.
        var spawnInfo = new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient };
        var pawn = world.SpawnActor<APawn>(GUClassArray.StaticClass<APawn>(), spawnInfo);

        if (pawn != null) {
            pawn.SetRole(ENetRole.ROLE_Authority);
            pawn.SetReplicates(true);
            pawn.SetAutonomousProxy(true); // possessed by newPlayer's own connection
            newPlayer.Possess(pawn);
        }
    }
}