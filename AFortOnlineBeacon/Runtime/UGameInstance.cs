namespace AFortOnlineBeacon.Runtime;

public class UGameInstance {
    public AGameModeBase? CreateGameModeForURL(FUrl inUrl, UWorld inWorld) {
        // TODO: World.SpawnActor
        
        var spawnInfo = new FActorSpawnParameters {
            SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod.AlwaysSpawn,
            ObjectFlags = EObjectFlags.RF_Transient
        };
        
        return inWorld.SpawnActor<AGameModeBase>(GUClassArray.StaticClass<AGameModeBase>(), spawnInfo);
    }
}