using MongoDB.Driver;
using Prive.Server.Http.Xmpp;
namespace Prive.Server.Http.Routes;

public static class FriendsRoutes {
    public static void Map(IEndpointRouteBuilder app) {
        var friends = app.MapGroup("/friends");

        friends.MapGet("/api/v1/{accountId}/friends", FriendsList);

        // The old "public" API, which is the one 10.40 actually calls - the packet capture shows it
        // asking for public/friends, public/blocklist and public/list/.../recentPlayers, and only
        // going to v1 for settings. Its list has a different shape from the v1 one: status and
        // direction instead of groups, which is how the client tells accepted from pending.
        friends.MapGet("/api/public/friends/{accountId}", PublicFriendsList);
        friends.MapPost("/api/public/friends/{accountId}/{friendId}", AddFriend);
        friends.MapDelete("/api/public/friends/{accountId}/{friendId}", RemoveFriend);
        friends.MapGet("/api/v1/{accountId}/outgoing", Outgoing);
        friends.MapGet("/api/v1/{accountId}/incoming", Incoming);
        friends.MapGet("/api/v1/{accountId}/summary", Summary);
        friends.MapGet("/api/v1/{accountId}/friends/{friendId}", NotImplemented);
        friends.MapPost("/api/v1/{accountId}/friends/{friendId}", NotImplemented);
        friends.MapDelete("/api/v1/{accountId}/friends/{friendId}", NotImplemented);
        friends.MapGet("/api/v1/{accountId}/blocklist", BlockList);
        friends.MapGet("/api/v1/{accountId}/settings", Settings);
        friends.MapGet("/api/v1/{accountId}/recent/Fortnite", RecentFortnite);
        friends.MapGet("/api/public/blocklist/{accountId}", PublicBlockList);
    }

    static async Task<object> FriendsList(HttpContext ctx, string accountId) {
        if (Denied(ctx, accountId) is EpicError denied) return denied;
        var friend = await GetOrCreateFriend(accountId);
        return friend.Accepted.Select(Accepted).ToArray();
    }

    static async Task<object> Outgoing(HttpContext ctx, string accountId) {
        if (Denied(ctx, accountId) is EpicError denied) return denied;
        var friend = await GetOrCreateFriend(accountId);
        return friend.Outgoing.Select(Pending).ToArray();
    }

    static async Task<object> Incoming(HttpContext ctx, string accountId) {
        if (Denied(ctx, accountId) is EpicError denied) return denied;
        var friend = await GetOrCreateFriend(accountId);
        return friend.Incoming.Select(Pending).ToArray();
    }

    static async Task<object> Summary(HttpContext ctx, string accountId) {
        if (Denied(ctx, accountId) is EpicError denied) return denied;
        var friend = await GetOrCreateFriend(accountId);
        return new {
            friends = friend.Accepted.Select(Accepted).ToArray(),
            incoming = friend.Incoming.Select(Brief).ToArray(),
            outgoing = friend.Outgoing.Select(Brief).ToArray(),
            suggested = new object[0],
            blocklist = new object[0],
            settings = new {
                acceptInvites = "public",
            }
        };
    }

    /// <summary>
    ///     The old list: accepted friends plus, unless <c>includePending=false</c>, the requests
    ///     still waiting in either direction. The client needs the pending ones to have anything to
    ///     accept or cancel.
    ///     <para>
    ///         Accepted entries all report OUTBOUND. Epic reports whichever side sent the original
    ///         request, but once accepted this server no longer records who did.
    ///     </para>
    /// </summary>
    static async Task<object> PublicFriendsList(HttpContext ctx, string accountId) {
        if (Denied(ctx, accountId) is EpicError denied) return denied;
        var friend = await GetOrCreateFriend(accountId);

        var entries = friend.Accepted.Select(x => PublicEntry(x, "ACCEPTED", "OUTBOUND")).ToList();
        if (ctx.Request.Query["includePending"].ToString() != "false") {
            entries.AddRange(friend.Incoming.Select(x => PublicEntry(x, "PENDING", "INBOUND")));
            entries.AddRange(friend.Outgoing.Select(x => PublicEntry(x, "PENDING", "OUTBOUND")));
        }
        return entries.ToArray();
    }

    /// <summary>
    ///     Accept an incoming request, or send an outgoing one. The client uses the same POST for
    ///     both, and which one it means depends on whether the other side has already asked.
    ///     <para>
    ///         The body carries a parental-controls <c>pin</c> when the account has them
    ///         configured. Parental controls are not implemented here, so the body is ignored.
    ///     </para>
    /// </summary>
    static async Task<object> AddFriend(HttpContext ctx, string accountId, string friendId) {
        if (Denied(ctx, accountId) is EpicError denied) return denied;
        if (accountId == friendId) return Results.StatusCode(403);
        if (await DB.GetUser(friendId) is null) return Results.StatusCode(403);

        var self = await GetOrCreateFriend(accountId);
        var other = await GetOrCreateFriend(friendId);

        if (self.Accepted.Any(x => x.AccountId == friendId)) return Results.NoContent();

        if (self.Incoming.RemoveAll(x => x.AccountId == friendId) > 0) {
            // They asked first, so this POST is the acceptance.
            other.Outgoing.RemoveAll(x => x.AccountId == accountId);
            var mine = new FriendElement() { AccountId = friendId };
            var theirs = new FriendElement() { AccountId = accountId };
            self.Accepted.Add(mine);
            other.Accepted.Add(theirs);
            await Save(self);
            await Save(other);

            // Both sides go from PENDING to ACCEPTED, each seeing the other as OUTBOUND.
            NotifyFriend(accountId, mine, "ACCEPTED", "OUTBOUND");
            NotifyFriend(friendId, theirs, "ACCEPTED", "OUTBOUND");

            // Neither was on the other's roster until now, so neither has ever been sent the
            // other's presence - without this both show as offline until the next login.
            XmppClients.PushPresence(accountId, friendId, offline: false);
            XmppClients.PushPresence(friendId, accountId, offline: false);
            return Results.NoContent();
        }

        if (!self.Outgoing.Any(x => x.AccountId == friendId)) {
            var mine = new FriendElement() { AccountId = friendId };
            var theirs = new FriendElement() { AccountId = accountId };
            self.Outgoing.Add(mine);
            other.Incoming.Add(theirs);
            await Save(self);
            await Save(other);

            // The sender sees an outgoing request, the target an incoming one.
            NotifyFriend(accountId, mine, "PENDING", "OUTBOUND");
            NotifyFriend(friendId, theirs, "PENDING", "INBOUND");
        }
        return Results.NoContent();
    }

    /// <summary>
    ///     Decline an incoming request, cancel an outgoing one, or unfriend - the client uses the
    ///     same DELETE for all three, so which it means is just whichever list the pair are in.
    /// </summary>
    static async Task<object> RemoveFriend(HttpContext ctx, string accountId, string friendId) {
        if (Denied(ctx, accountId) is EpicError denied) return denied;
        if (await DB.GetUser(friendId) is null) return Results.StatusCode(403);

        var self = await GetOrCreateFriend(accountId);
        var other = await GetOrCreateFriend(friendId);

        // Both sides are cleared even if only one of them still had the entry, so a half-written
        // relationship cannot survive a removal.
        var clearedSelf = Forget(self, friendId);
        var clearedOther = Forget(other, accountId);
        if (!clearedSelf && !clearedOther) return Results.NoContent();

        await Save(self);
        await Save(other);

        NotifyRemoval(accountId, friendId);
        NotifyRemoval(friendId, accountId);

        // They are off each other's rosters now, so no further presence will ever reach them -
        // without one last unavailable each would keep showing the other as online forever.
        XmppClients.PushPresence(accountId, friendId, offline: true);
        XmppClients.PushPresence(friendId, accountId, offline: true);
        return Results.NoContent();
    }

    /// <summary>Drops every trace of one account from a friends document.</summary>
    static bool Forget(Friend friend, string accountId)
        => friend.Accepted.RemoveAll(x => x.AccountId == accountId)
         + friend.Incoming.RemoveAll(x => x.AccountId == accountId)
         + friend.Outgoing.RemoveAll(x => x.AccountId == accountId) > 0;

    static object NotImplemented(HttpContext ctx) {
        ctx.Response.StatusCode = 501;
        return EpicError.Create(
            "errors.unknown", 0,
            "Not Implemented",
            "fortnite", "prod-live"
        );
    }

    static object BlockList(HttpContext ctx, string accountId)
        => Denied(ctx, accountId) ?? (object)new object[0];

    static object Settings(HttpContext ctx, string accountId)
        => Denied(ctx, accountId) ?? (object)new {
            acceptInvites = "public",
        };

    static object RecentFortnite() => new object[0];

    static object PublicBlockList() => new object[0];

    /// <summary>
    ///     The permission error to answer with, or null when the caller owns this account. It
    ///     deliberately leaves the status code alone, so the error goes out with a 200 - that is
    ///     what this server has always done and the client accepts it.
    /// </summary>
    static EpicError? Denied(HttpContext ctx, string accountId)
        => ctx.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId
            ? EpicError.Permission($"friends:{accountId}", "READ", "friends")
            : null;

    /// <summary>
    ///     The client asks for outgoing/incoming/summary before it has ever had a friend, and Mongo
    ///     holds no document until something writes one - so create it on first read. Only the
    ///     friends list used to do this, which made the other three throw for a fresh account.
    /// </summary>
    static async Task<Friend> GetOrCreateFriend(string accountId) {
        if (await DB.GetFriend(accountId) is Friend existing) return existing;
        var friend = new Friend() { AccountId = accountId };
        await DB.Friends.InsertOneAsync(friend);
        return friend;
    }

    /// <summary>An established friend. Carries <c>mutual</c>; a pending one does not.</summary>
    static object Accepted(FriendElement x) => new {
        accountId = x.AccountId,
        groups = new object[0],
        mutual = 0,
        alias = "",
        note = "",
        favorite = false,
        created = x.CreatedAt,
    };

    /// <summary>An outgoing or incoming request.</summary>
    static object Pending(FriendElement x) => new {
        accountId = x.AccountId,
        groups = new object[0],
        alias = "",
        note = "",
        favorite = false,
        created = x.CreatedAt,
    };

    /// <summary>
    ///     Tells one client that one entry of its friends list changed. Without it the client only
    ///     finds out by asking again, so an incoming request does not appear until the friends menu
    ///     is reopened.
    ///     <para>
    ///         The payload is the same object the REST list returns, wrapped in the envelope the
    ///         client's notification handler expects. The type name and the PENDING / ACCEPTED /
    ///         INBOUND / OUTBOUND literals all sit together in the client's .rdata right after
    ///         "com.epicgames.friends.core.apiobjects.Friend", which is that handler.
    ///     </para>
    /// </summary>
    static void NotifyFriend(string toAccountId, FriendElement entry, string status, string direction)
        => XmppClients.SendToAccount(toAccountId, new {
            payload = new {
                accountId = entry.AccountId,
                status = status,
                direction = direction,
                created = entry.CreatedAt.ToString(DateTimeFormat),
                favorite = false
            },
            type = "com.epicgames.friends.core.apiobjects.Friend",
            timestamp = DateTime.UtcNow.ToString(DateTimeFormat)
        });

    /// <summary>
    ///     Tells one client the pair are no longer related at all, whichever list they were in.
    ///     <c>DELETED</c> is the only reason this server ever sends.
    /// </summary>
    static void NotifyRemoval(string toAccountId, string aboutAccountId)
        => XmppClients.SendToAccount(toAccountId, new {
            payload = new {
                accountId = aboutAccountId,
                reason = "DELETED"
            },
            type = "com.epicgames.friends.core.apiobjects.FriendRemoval",
            timestamp = DateTime.UtcNow.ToString(DateTimeFormat)
        });

    static Task Save(Friend friend)
        => DB.Friends.ReplaceOneAsync(Builders<Friend>.Filter.Eq("AccountId", friend.AccountId), friend);

    /// <summary>An entry in the old public list, which reports status and direction.</summary>
    static object PublicEntry(FriendElement x, string status, string direction) => new {
        accountId = x.AccountId,
        status = status,
        direction = direction,
        created = x.CreatedAt,
        favorite = false,
    };

    /// <summary>The short form the summary uses for its incoming/outgoing lists.</summary>
    static object Brief(FriendElement x) => new {
        accountId = x.AccountId,
        favorite = false,
    };
}
