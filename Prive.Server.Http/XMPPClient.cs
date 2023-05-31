using System.Net.WebSockets;

namespace Prive.Server.Http;

public class XMPPClient : IDisposable {
    private bool IsDisposed;

    public WebSocket Socket { get; }
    public TaskCompletionSource<object?> TCS { get; }

    public XMPPClient(WebSocket socket, TaskCompletionSource<object?> tcs) {
        Socket = socket;
        TCS = tcs;
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