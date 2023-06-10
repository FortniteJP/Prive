using SuperSimpleTcp;

namespace Prive.Server.Http;

public class CommunicateClient {
    public SimpleTcpClient Client { get; }
    
    public CommunicateClient(string serverIP, int serverPort) {
        Client = new(serverIP, serverPort);
    }

    public CommunicateClient(SimpleTcpClient client) {
        Client = client;
    }

    public Task Send(string data) {
        if (!Client.IsConnected) Client.ConnectWithRetries();
        return Client.SendAsync(data);
    }

    public Task Shutdown() => Send("shutdown");

    public Task NewBeacon() => Send($"newbeacon;{Guid.NewGuid().ToString().Replace("-", "")}");
}