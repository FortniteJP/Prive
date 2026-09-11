using System.Text.Json;

namespace Prive.Server.Http.Xmpp;

/// <summary>
///     Who is connected, which multi-user chat rooms exist, and the handful of calls the HTTP side
///     uses to push something at a connected client.
///     <para>
///         Kestrel runs every connection concurrently, so the two collections here are guarded.
///         Sends stay outside the lock: a send only queues a frame, but holding a lock across
///         one would still serialise every broadcast in the server behind a single client.
///     </para>
/// </summary>
public static class XmppClients {
    static readonly List<XmppSession> Sessions = new();

    /// <summary>Room name to the account ids in it. Party chat and the global chat room.</summary>
    static readonly Dictionary<string, List<string>> Rooms = new();

    static readonly object Gate = new();

    public static int Count { get { lock (Gate) return Sessions.Count; } }

    public static string[] DisplayNames { get { lock (Gate) return Sessions.Select(x => x.DisplayName ?? "").ToArray(); } }

    /// <summary>False if this account is already connected, which is what refuses the duplicate.</summary>
    public static bool TryRegister(XmppSession session) {
        lock (Gate) {
            if (Sessions.Any(x => x.AccountId == session.AccountId)) return false;
            Sessions.Add(session);
            return true;
        }
    }

    public static XmppSession? Find(string accountId) {
        lock (Gate) return Sessions.FirstOrDefault(x => x.AccountId == accountId);
    }

    /// <summary>Matches on the bare JID, so a full JID with a resource on it also finds them.</summary>
    public static XmppSession? FindByBareJid(string jid) {
        lock (Gate) return Sessions.FirstOrDefault(x => x.BareJid == jid.Split('/')[0]);
    }

    /// <summary>Matches either the bare or the full JID - the client addresses both ways.</summary>
    public static XmppSession? FindByJid(string jid) {
        lock (Gate) return Sessions.FirstOrDefault(x => x.BareJid == jid || x.Jid == jid);
    }

    public static XmppSession? FindByToken(string token) {
        lock (Gate) return Sessions.FirstOrDefault(x => x.Token == token);
    }

    public static bool Online(string accountId) => Find(accountId) is not null;

    internal static (string Status, bool Away) LastPresence(XmppSession session) {
        lock (Gate) return (session.LastStatus, session.LastAway);
    }

    // ---- multi user chat --------------------------------------------------------------------

    /// <summary>Creates the room on the first join. False if the account is already in it.</summary>
    public static bool JoinRoom(string room, string accountId) {
        lock (Gate) {
            if (!Rooms.TryGetValue(room, out var members)) Rooms[room] = members = new();
            if (members.Contains(accountId)) return false;
            members.Add(accountId);
            return true;
        }
    }

    public static bool LeaveRoom(string room, string accountId) {
        lock (Gate) return Rooms.TryGetValue(room, out var members) && members.Remove(accountId);
    }

    public static bool InRoom(string room, string accountId) {
        lock (Gate) return Rooms.TryGetValue(room, out var members) && members.Contains(accountId);
    }

    /// <summary>
    ///     The connected members of a room, as a snapshot - callers iterate it while sending, and
    ///     the list itself can change underneath them.
    /// </summary>
    public static XmppSession[] RoomSessions(string room) {
        lock (Gate) {
            if (!Rooms.TryGetValue(room, out var members)) return Array.Empty<XmppSession>();
            return members.Select(id => Sessions.FirstOrDefault(x => x.AccountId == id))
                .Where(x => x is not null).Select(x => x!).ToArray();
        }
    }

    // ---- presence --------------------------------------------------------------------------

    /// <summary>
    ///     Records what this client published and forwards it to every friend of theirs who is
    ///     online. This is the whole of presence: there is no roster on the server, the friends
    ///     list in Mongo is the roster.
    /// </summary>
    public static async Task UpdatePresenceForFriends(XmppSession sender, string status, bool away, bool offline) {
        if (sender.AccountId is null) return;
        lock (Gate) {
            if (!Sessions.Contains(sender)) return;
            sender.LastStatus = status;
            sender.LastAway = away;
        }

        if (await DB.GetFriend(sender.AccountId) is not Friend friend) return;
        foreach (var entry in friend.Accepted) {
            if (Find(entry.AccountId) is not XmppSession receiver) continue;
            receiver.Send(XmppStanza.Presence(sender.Jid!, receiver.Jid!, status, away, offline));
        }
    }

    /// <summary>
    ///     Pushes one client's current presence at one other client. For the HTTP side to call
    ///     when the two become friends - until then neither is on the other's roster, so neither
    ///     would ever have been sent the other's presence.
    /// </summary>
    public static void PushPresence(string fromAccountId, string toAccountId, bool offline) {
        if (Find(fromAccountId) is not XmppSession sender) return;
        if (Find(toAccountId) is not XmppSession receiver) return;
        var (status, away) = LastPresence(sender);
        receiver.Send(XmppStanza.Presence(sender.Jid!, receiver.Jid!, status, away, offline));
    }

    // ---- pushing messages in from the HTTP side ---------------------------------------------

    /// <summary>Sends a body to one account, from the server rather than from another client.</summary>
    public static void SendToAccount(string accountId, object body) {
        if (Find(accountId) is not XmppSession receiver) return;
        receiver.Send(XmppStanza.Message($"xmpp-admin@{receiver.Domain}", receiver.Jid!, Serialize(body)));
    }

    public static void SendToAll(object body) {
        var payload = Serialize(body);
        foreach (var receiver in Snapshot())
            receiver.Send(XmppStanza.Message($"xmpp-admin@{receiver.Domain}", receiver.Jid!, payload));
    }

    /// <summary>
    ///     Drops whichever session holds this access token. Called when the token is killed, so a
    ///     signed-out client does not keep its account registered and lock itself out.
    /// </summary>
    public static void DisconnectToken(string token) => FindByToken(token)?.Disconnect();

    // ---- teardown ---------------------------------------------------------------------------

    /// <summary>
    ///     Unregisters a dropped client: tells its friends it went offline, takes it out of its
    ///     rooms, and - if its last presence said it was in a party - tells everyone else that it
    ///     left, which the client needs or the party keeps a ghost member.
    /// </summary>
    public static async Task Remove(XmppSession session) {
        string lastStatus;
        lock (Gate) {
            if (!Sessions.Contains(session)) return;
            lastStatus = session.LastStatus;
        }

        await UpdatePresenceForFriends(session, "{}", false, offline: true);

        lock (Gate) {
            Sessions.Remove(session);
            foreach (var room in session.JoinedRooms)
                if (Rooms.TryGetValue(room, out var members)) members.Remove(session.AccountId ?? "");
        }
        session.JoinedRooms.Clear();

        if (PartyIdFrom(lastStatus) is string partyId) {
            var body = Serialize(new {
                type = "com.epicgames.party.memberexited",
                payload = new {
                    partyId = partyId,
                    memberId = session.AccountId,
                    wasKicked = false
                },
                timestamp = DateTime.UtcNow.ToString(DateTimeFormat)
            });
            foreach (var receiver in Snapshot()) {
                if (receiver.AccountId == session.AccountId) continue;
                receiver.Send(XmppStanza.Message(session.Jid!, receiver.Jid!, body,
                    id: GenerateToken().ToUpperInvariant()));
            }
        }

        Console.WriteLine($"XMPP: {session.DisplayName} disconnected");
    }

    static XmppSession[] Snapshot() {
        lock (Gate) return Sessions.ToArray();
    }

    static string Serialize(object body) => body as string ?? JsonSerializer.Serialize(body);

    /// <summary>
    ///     The party this client was in, read out of the last presence it published. The client
    ///     files it under a Properties key that starts with <c>party.joininfo</c> and ends in a
    ///     build-specific suffix, so the key is matched by prefix.
    /// </summary>
    static string? PartyIdFrom(string status) {
        try {
            using var doc = JsonDocument.Parse(status);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("Properties", out var properties)) return null;
            if (properties.ValueKind != JsonValueKind.Object) return null;

            foreach (var property in properties.EnumerateObject()) {
                if (!property.Name.StartsWith("party.joininfo", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                if (property.Value.TryGetProperty("partyId", out var partyId) && partyId.ValueKind == JsonValueKind.String)
                    return partyId.GetString();
            }
        } catch (JsonException) { }
        return null;
    }
}
