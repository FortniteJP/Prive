using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Prive.Server.Http.Xmpp;

/// <summary>
///     The transport. XMPP gets its own port - one above HTTP - but it is the same Kestrel, so
///     everything arriving on that port is claimed here and never reaches the Epic routes.
///     <para>
///         This used to be a WebSocketSharp listener. That package is built for .NET Framework and
///         its accept path calls <c>Delegate.BeginInvoke</c>, which throws
///         <see cref="PlatformNotSupportedException"/> on .NET - but only when a stanza arrives
///         before the accept finishes, so it dropped exactly the clients that were quickest to
///         send their stream header.
///     </para>
/// </summary>
public static class XmppEndpoint {
    public static IApplicationBuilder UseXmpp(this IApplicationBuilder app)
        => app.Use(async (HttpContext ctx, Func<Task> next) => {
            if (ctx.Connection.LocalPort != XmppPort) {
                await next();
                return;
            }

            var peer = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

            if (!ctx.WebSockets.IsWebSocketRequest) {
                // Not an upgrade. Answering with who is connected makes the port easy to check
                // from a browser or curl - but log it, because a client that ends up here instead
                // of upgrading is a client that is not going to connect.
                Console.WriteLine($"XMPP: {ctx.Request.Method} {ctx.Request.Path} from {peer} is not a WebSocket upgrade");
                await ctx.Response.WriteAsJsonAsync(new {
                    amount = XmppClients.Count,
                    clients = XmppClients.DisplayNames
                });
                return;
            }

            // RFC 7395 names the subprotocol "xmpp" and the client offers it, so it has to be
            // echoed or the client fails the connection. Kestrel refuses a subprotocol the client
            // did not ask for, so it is matched against what actually came in.
            var subProtocol = ctx.WebSockets.WebSocketRequestedProtocols
                .FirstOrDefault(x => x.Equals("xmpp", StringComparison.OrdinalIgnoreCase));

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext {
                SubProtocol = subProtocol,
                KeepAliveInterval = TimeSpan.FromSeconds(30)
            });

            Console.WriteLine($"XMPP: socket open from {peer}, subprotocol {subProtocol ?? "(none offered)"}");

            await new XmppConnection(socket).Run(ctx.RequestAborted);
        });
}

/// <summary>
///     One socket: a read loop that hands stanzas to an <see cref="XmppSession"/> one at a time,
///     and a write loop fed by a queue.
/// </summary>
sealed class XmppConnection {
    readonly WebSocket _socket;
    readonly XmppSession _session;

    /// <summary>
    ///     Outgoing frames are queued, not written straight to the socket: other clients call
    ///     <see cref="XmppSession.Send"/> from their own threads while broadcasting,
    ///     <see cref="WebSocket.SendAsync"/> is not safe to call concurrently, and none of those
    ///     callers should be left waiting on this socket.
    /// </summary>
    readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions {
        SingleReader = true
    });

    /// <summary>Cancelled once the last frame is out, to stop the read loop waiting.</summary>
    readonly CancellationTokenSource _done = new();

    public XmppConnection(WebSocket socket) {
        _socket = socket;
        _session = new XmppSession(
            frame => _outbound.Writer.TryWrite(frame),
            () => _outbound.Writer.TryComplete()
        );
    }

    public async Task Run(CancellationToken cancel) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _done.Token);
        var writer = WriteLoop(linked.Token);
        try {
            await ReadLoop(linked.Token);
        } finally {
            _outbound.Writer.TryComplete();
            await writer;
            await _session.Closed();
            _done.Dispose();
        }
    }

    /// <summary>
    ///     Stanzas are handled in order by construction - one loop, awaited - and the login
    ///     handshake depends on that.
    /// </summary>
    async Task ReadLoop(CancellationToken cancel) {
        var buffer = new byte[16 * 1024];
        var pending = new MemoryStream();

        while (!cancel.IsCancellationRequested && _socket.State == WebSocketState.Open) {
            WebSocketReceiveResult result;
            try {
                result = await _socket.ReceiveAsync(buffer, cancel);
            } catch (WebSocketException) {
                return;
            } catch (OperationCanceledException) {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close) return;
            pending.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue; // a stanza split across frames

            var frame = Encoding.UTF8.GetString(pending.ToArray());
            pending.SetLength(0);

            try {
                await _session.Handle(frame);
            } catch (Exception e) {
                Console.WriteLine($"XMPP: failed to handle a stanza from {_session.DisplayName ?? "an unauthenticated client"} - {e.Message}");
            }
        }
    }

    /// <summary>
    ///     Drains the queue, then closes. Draining first is what lets a framing close reach the
    ///     client before the socket goes away.
    /// </summary>
    async Task WriteLoop(CancellationToken cancel) {
        try {
            await foreach (var frame in _outbound.Reader.ReadAllAsync(cancel))
                await _socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, cancel);
        } catch (Exception) {
            // The socket is gone, or the request was aborted. Either way there is nothing left to
            // write and the read loop is about to finish too.
        }

        try {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        } catch (Exception) { }

        // Closing the output does not end the read side, and a client that never sends its own
        // close would leave the read loop waiting forever - with the session still registered.
        // One connection per account, so that locks the account out of its own next login.
        _done.Cancel();
    }
}
