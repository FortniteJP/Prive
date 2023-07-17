/* using SuperSimpleTcp;

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

    public void AddDataReceivedCallback(Action<object?, DataReceivedEventArgs> callback) {
        Client.Events.DataReceived += (s, e) => callback(s, e);
    }

    public Task Shutdown() => Send("shutdown");

    public Task SetPort(int port) => Send($"setport;{port}");

    public Task NewBeacon() => Send($"newbeacon;{Guid.NewGuid().ToString().Replace("-", "")}");

    public Task StartBus() => Send("startbus;");

    public Task SendOutfit(string id, string cid) => Send($"outfit;{id};{cid};");
} */