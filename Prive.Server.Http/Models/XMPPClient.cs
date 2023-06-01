using System.Net.WebSockets;
using System.Text;

namespace Prive.Server.Http;

public class XMPPClient : IDisposable {
    private bool IsDisposed;

    public WebSocket Socket { get; }
    public TaskCompletionSource<object?> TCS { get; }

    public XMPPClient(WebSocket socket, TaskCompletionSource<object?> tcs) {
        Socket = socket;
        TCS = tcs;
    }

    public static void Handle(WebSocket socket, TaskCompletionSource<object?> tcs) {
        var client = new XMPPClient(socket, tcs);
        XMPPClients.Add(client);
        client.ReceiveLoop();
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken = default) {
        var buffer = Encoding.UTF8.GetBytes(message);
        await Socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
    }

    public async void ReceiveLoop() {
        var buffer = new byte[1024];
        while (Socket.State == WebSocketState.Open) {
            var result = await Socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            Console.WriteLine($"Received: {Encoding.UTF8.GetString(buffer)}");
            if (result.MessageType == WebSocketMessageType.Close) {
                await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                break;
            }
        }
    }

    protected async void Dispose(bool disposing) {
        if (IsDisposed) return;
        if (disposing) {
            if (Socket.State == WebSocketState.Open) await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
            Socket.Dispose();
            TCS.TrySetResult(null);
        }
        IsDisposed = true;
    }

    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}