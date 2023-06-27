using AFortOnlineBeacon.Core;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Test;

class Program {
    private const float TickRate = (1000.0f / 60.0f) / 1000.0f;
    private static readonly PeriodicTimer Tick = new PeriodicTimer(TimeSpan.FromSeconds(TickRate));

    static async Task<int> Main(string[] args) {
        var worldUrl = new FUrl {
            Map = "/Game/Athena/Maps/Athena_Terrain",
            Port = 20000
        };
        
        await using (var world = new FortniteWorld()) {
            world.SetGameInstance(new UGameInstance());
            world.SetGameMode(worldUrl);
            world.InitializeActorsForPlay(worldUrl, true);
            world.Listen();
        
            while (await Tick.WaitForNextTickAsync()) world.Tick(TickRate);
        }
        return 0;
    }
}
