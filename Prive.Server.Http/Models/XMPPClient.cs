using WebSocketSharp;
using WebSocketSharp.Server;

namespace Prive.Server.Http;

public class XMPPClient : WebSocketBehavior {
    public static Task? PresenceLoop { get; set; }

    public static void SendPresence() {}

    protected override void OnOpen() {
        Console.WriteLine("New XMPP Connection");
        XMPPClients.Add(this);
    }

    protected override void OnMessage(MessageEventArgs e) {
        Console.WriteLine($"XMPP Message Received: {e.Data}");
    }

    protected override void OnClose(CloseEventArgs e) {
        Console.WriteLine("XMPP Connection Closed");
        XMPPClients.Remove(this);
    }
}