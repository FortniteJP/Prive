using System.Text;
using System.Xml.Linq;

namespace Prive.Server.Http.Xmpp;

/// <summary>
///     One client's connection, and the login handshake it has to walk through:
///     <c>open</c> - <c>auth</c> - <c>open</c> - <c>iq bind</c> - <c>iq session</c>, after which
///     presence, chat and the client's own party protocol flow over the same socket.
///     <para>
///         Frames are handled one at a time, in order - the connection's read loop awaits each
///         <see cref="Handle"/> before receiving the next. The login sequence depends on that.
///     </para>
/// </summary>
public sealed class XmppSession {
    /// <summary>
    ///     Used only if the client's own <c>open</c> does not name a domain. Normally the domain
    ///     comes from the client, which takes it from the <c>Domain</c> key DefaultEngine.ini sets
    ///     - echoing it back is what keeps the two ends agreeing on what JIDs look like.
    /// </summary>
    public const string DefaultDomain = "prod.ol.epicgames.com";

    readonly Action<string> _send;
    readonly Action _disconnect;

    public string Domain { get; private set; } = DefaultDomain;
    public string? StreamId { get; private set; }
    public string? AccountId { get; private set; }
    public string? DisplayName { get; private set; }
    public string? Token { get; private set; }
    public string? Resource { get; private set; }
    public string? Jid { get; private set; }
    public bool Authenticated { get; private set; }
    public bool Registered { get; private set; }

    bool _closed;

    /// <summary>
    ///     The last presence this client published. Read by other clients' threads, so every
    ///     access goes through <see cref="XmppClients"/> under its lock.
    /// </summary>
    internal string LastStatus = "{}";
    internal bool LastAway;

    /// <summary>Rooms this connection joined, so a disconnect can take it back out of them.</summary>
    internal readonly List<string> JoinedRooms = new();

    public XmppSession(Action<string> send, Action disconnect) {
        _send = send;
        _disconnect = disconnect;
    }

    public string BareJid => Jid?.Split('/')[0] ?? "";

    /// <summary>The nick a MUC occupant is known by: display name, account id and resource.</summary>
    internal string MucNick => $"{Uri.EscapeDataString(DisplayName ?? "")}:{AccountId}:{Resource}";

    internal string MucJid(string room) => $"{room}@muc.{Domain}/{MucNick}";

    public void Send(string frame) {
        try {
            _send(frame);
        } catch {
            // The socket is already gone. The connection's own teardown does the cleanup, so
            // there is nothing to do here, and throwing would take down whoever was
            // broadcasting to us.
        }
    }

    public async Task Handle(string frame) {
        if (_closed) return;

        var stanza = XmppStanza.Parse(frame);
        if (stanza is null) { Reject($"unparseable frame: {Truncate(frame)}"); return; }

        switch (stanza.Name.LocalName) {
            case "open": HandleOpen(stanza); break;
            case "auth": await HandleAuth(stanza); break;
            case "iq": await HandleIq(stanza); break;
            case "message": HandleMessage(stanza); break;
            case "presence": await HandlePresence(stanza); break;
            case "close": Disconnect(); return;
        }

        // Registration happens here rather than inside the bind handler, because the client is only
        // usable once every piece of the handshake has landed. _xmpp_session1 arrives in the frame
        // after bind, so by then it already sees a registered client.
        if (_closed || Registered) return;
        if (AccountId is null || DisplayName is null || Token is null
            || Jid is null || StreamId is null || Resource is null || !Authenticated) return;

        Registered = XmppClients.TryRegister(this);
        if (Registered) Console.WriteLine($"XMPP: {DisplayName} connected as {Jid}");
        else Reject($"{AccountId} is already connected");
    }

    /// <summary>Called by the connection when the socket drops.</summary>
    public async Task Closed() {
        _closed = true;
        if (Registered) await XmppClients.Remove(this);
        Registered = false;
    }

    /// <summary>
    ///     Ends the stream and drops the socket. The framing close goes out first, or the client
    ///     reports a transport error instead of a clean stream end - but the socket has to follow,
    ///     because a client that ignores the close would otherwise stay registered and lock its
    ///     own account out.
    /// </summary>
    public void Disconnect() {
        if (_closed) return;
        _closed = true;
        Send(XmppStanza.Close());
        _disconnect();
    }

    /// <summary>
    ///     Hangs up on a client that did something the handshake does not allow, saying why. A
    ///     silent drop here is indistinguishable from the server not being there at all.
    /// </summary>
    void Reject(string reason) {
        if (_closed) return;
        Console.WriteLine($"XMPP: refused {DisplayName ?? AccountId ?? "an unauthenticated client"} - {reason}");
        Disconnect();
    }

    void HandleOpen(XElement stanza) {
        if (stanza.Attr("to") is string to && to.Length > 0) Domain = to;
        StreamId ??= GenerateToken();
        Send(XmppStanza.Open(Domain, StreamId));
        Send(XmppStanza.Features(Authenticated));
    }

    /// <summary>
    ///     SASL PLAIN, whose payload is <c>authzid \0 authcid \0 password</c>. The password is an
    ///     access token from <see cref="Global.AuthTokens"/> - the same one the HTTP side issued -
    ///     so logging in over XMPP needs no separate credential.
    /// </summary>
    async Task HandleAuth(XElement stanza) {
        if (StreamId is null) return;
        if (AccountId is not null) return;

        string decoded;
        try {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(stanza.Value));
        } catch (FormatException) {
            Reject("SASL payload is not base64");
            return;
        }

        var parts = decoded.Split('\0');
        // Three fields, NUL separated: an empty authzid, the authcid, then the password.
        if (parts.Length != 3) { Reject($"SASL PLAIN payload has {parts.Length} fields, expected 3"); return; }

        var token = AuthTokens.GetValueOrDefault(parts[2]);
        if (token is null) { Reject("the password is not a known access token"); return; }

        // One connection per account. A stale session would otherwise hold the account forever and
        // the client could never get back in.
        if (XmppClients.Find(token.AccountId) is not null) { Reject($"{token.AccountId} is already connected"); return; }

        var user = await DB.GetUser(token.AccountId);
        if (user is null) { Reject($"no account in mongo for {token.AccountId}"); return; }

        AccountId = user.AccountId;
        DisplayName = user.DisplayName;
        Token = token.TokenString;
        Authenticated = true;

        Send(XmppStanza.SaslSuccess());
    }

    async Task HandleIq(XElement stanza) {
        if (StreamId is null) return;
        var id = stanza.Attr("id");

        switch (id) {
            case "_xmpp_bind1":
                if (Resource is not null || AccountId is null) return;
                if (stanza.Child("bind")?.Child("resource")?.Value is not string resource || resource.Length == 0) return;
                if (XmppClients.Find(AccountId) is not null) { Reject($"{AccountId} is already connected"); return; }

                Resource = resource;
                Jid = $"{AccountId}@{Domain}/{Resource}";
                Send(XmppStanza.BindResult(Jid));
                return;

            case "_xmpp_session1":
                if (!Registered) { Reject("session started before the handshake finished"); return; }
                Send(XmppStanza.IqResult(Jid!, Domain, "_xmpp_session1"));
                await SendFriendsPresence();
                return;

            default:
                // Everything else - the client's keepalive pings, mostly - gets an empty result.
                if (!Registered) { Reject($"iq {id} sent before the handshake finished"); return; }
                Send(XmppStanza.IqResult(Jid!, Domain, id ?? ""));
                return;
        }
    }

    void HandleMessage(XElement stanza) {
        if (!Registered) { Reject("message sent before the handshake finished"); return; }
        if (stanza.Child("body")?.Value is not string body || body.Length == 0) return;
        var to = stanza.Attr("to");

        switch (stanza.Attr("type")) {
            case "chat": {
                if (to is null || body.Length >= 300) return;
                var receiver = XmppClients.FindByBareJid(to);
                if (receiver is null || receiver.AccountId == AccountId) return;
                receiver.Send(XmppStanza.Message(Jid!, receiver.Jid!, body, "chat"));
                return;
            }

            case "groupchat": {
                if (to is null || body.Length >= 300) return;
                var room = to.Split('@')[0];
                if (!XmppClients.InRoom(room, AccountId!)) return;
                foreach (var member in XmppClients.RoomSessions(room))
                    member.Send(XmppStanza.Message(MucJid(room), member.Jid!, body, "groupchat"));
                return;
            }
        }

        // No type, but a JSON object body: the client's own party and social protocol, which is
        // routed verbatim to whoever it is addressed to.
        if (to is null || stanza.Attr("id") is not string messageId) return;
        if (!IsJsonObject(body)) return;
        if (XmppClients.FindByJid(to) is not XmppSession addressed) return;
        addressed.Send(XmppStanza.Message(Jid!, addressed.Jid!, body, id: messageId));
    }

    async Task HandlePresence(XElement stanza) {
        if (!Registered) { Reject("presence sent before the handshake finished"); return; }
        var to = stanza.Attr("to");

        if (stanza.Attr("type") == "unavailable") {
            if (to is null) return;
            // Only a MUC address means "leave the room"; an unavailable aimed anywhere else is an
            // ordinary presence update and falls through to the status handling below.
            var mucDomain = $"@muc.{Domain}";
            if (to.EndsWith(mucDomain) || to.Split('/')[0].EndsWith(mucDomain)) {
                if (!to.StartsWith("party-", StringComparison.OrdinalIgnoreCase)) return;

                var room = to.Split('@')[0];
                if (!XmppClients.LeaveRoom(room, AccountId!)) return;
                JoinedRooms.Remove(room);

                Send(XmppStanza.MucPresence(MucJid(room), Jid!, MucNick, Jid!, "none", self: true, leaving: true));
                return;
            }
        } else if (stanza.Child("x") is not null) {
            // A muc x marker means "join". The client sends it as either x or muc:x.
            if (to is null) return;

            var room = to.Split('@')[0];
            if (!XmppClients.JoinRoom(room, AccountId!)) return;
            JoinedRooms.Add(room);

            Send(XmppStanza.MucPresence(MucJid(room), Jid!, MucNick, Jid!, "participant", self: true, leaving: false));

            foreach (var member in XmppClients.RoomSessions(room)) {
                Send(XmppStanza.MucPresence(member.MucJid(room), Jid!, member.MucNick, member.Jid!, "participant", self: false, leaving: false));
                if (member.AccountId == AccountId) continue;
                member.Send(XmppStanza.MucPresence(MucJid(room), member.Jid!, MucNick, Jid!, "participant", self: false, leaving: false));
            }
            return;
        }

        if (stanza.Child("status")?.Value is not string status || !IsJsonObject(status)) return;
        await XmppClients.UpdatePresenceForFriends(this, status, stanza.Child("show") is not null, offline: false);
    }

    /// <summary>
    ///     Answers the session iq with the presence of every friend already online, which is how
    ///     the client's friends list comes up populated instead of everyone showing as offline.
    /// </summary>
    async Task SendFriendsPresence() {
        if (await DB.GetFriend(AccountId!) is not Friend friend) return;
        foreach (var entry in friend.Accepted) {
            if (XmppClients.Find(entry.AccountId) is not XmppSession online) continue;
            var (status, away) = XmppClients.LastPresence(online);
            Send(XmppStanza.Presence(online.Jid!, Jid!, status, away, offline: false));
        }
    }

    static string Truncate(string frame)
        => frame.Length <= 200 ? frame : frame[..200] + $"... (+{frame.Length - 200})";

    internal static bool IsJsonObject(string text) {
        try {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        } catch (System.Text.Json.JsonException) {
            return false;
        }
    }
}
