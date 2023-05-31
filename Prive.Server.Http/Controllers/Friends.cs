using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("friends")]
public class FriendsController : ControllerBase {
    [HttpGet("api/v1/{accountId}/friends")]
    public async Task<object> FriendsList() {
        var accountId = (string)Request.RouteValues["accountId"]!;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"friends:{accountId}", "READ", "friends");
        }

        var friend = await DB.GetFriend(accountId);
        if (friend is null) {
            friend = new() { AccountId = accountId };
            await DB.Friends.InsertOneAsync(friend);
        }
        return friend.Accepted.Aggregate(new List<object>(), (r, x) => {
            r.Append(new {
                accountId = x.AccountId,
                groups = new object[0],
                mutual = 0,
                alias = "",
                note = "",
                favorite = false,
                created = x.CreatedAt,
            });
            return r;
        }).ToArray();
    }

    [HttpGet("api/public/friends/{accountId}")]
    public Task<object> PublicFriends() => FriendsList(); // Same?

    [HttpGet("api/v1/{accountId}/outgoing")]
    public async Task<object> Outgoing() {
        var accountId = (string)Request.RouteValues["accountId"]!;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"friends:{accountId}", "READ", "friends");
        }

        var friend = await DB.GetFriend(accountId);
        return friend.Outgoing.Aggregate(new object[friend.Outgoing.Count], (r, x) => {
            r.Append(new {
                accountId = x.AccountId,
                groups = new object[0],
                alias = "",
                note = "",
                favorite = false,
                created = x.CreatedAt,
            });
            return r;
        }).ToArray();
    }

    [HttpGet("api/v1/{accountId}/incoming")]
    public async Task<object> Incoming() {
        var accountId = (string)Request.RouteValues["accountId"]!;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"friends:{accountId}", "READ", "friends");
        }

        var friend = await DB.GetFriend(accountId);
        return friend.Incoming.Aggregate(new object[friend.Incoming.Count], (r, x) => {
            r.Append(new {
                accountId = x.AccountId,
                groups = new object[0],
                alias = "",
                note = "",
                favorite = false,
                created = x.CreatedAt,
            });
            return r;
        }).ToArray();
    }

    [HttpGet("api/v1/{accountId}/summary")]
    public async Task<object> Summary() {
        var accountId = (string)Request.RouteValues["accountId"]!;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"friends:{accountId}", "READ", "friends");
        }

        var friend = await DB.GetFriend(accountId);
        return new {
            friends = friend.Accepted.Aggregate(new object[friend.Accepted.Count], (r, x) => {
                r.Append(new {
                    accountId = x.AccountId,
                    groups = new object[0],
                    mutual = 0,
                    alias = "",
                    note = "",
                    favorite = false,
                    created = x.CreatedAt,
                });
                return r;
            }).ToArray(),
            incoming = friend.Incoming.Aggregate(new object[friend.Incoming.Count], (r, x) => {
                r.Append(new {
                    accountId = x.AccountId,
                    favorite = false,
                });
                return r;
            }).ToArray(),
            outgoing = friend.Outgoing.Aggregate(new object[friend.Outgoing.Count], (r, x) => {
                r.Append(new {
                    accountId = x.AccountId,
                    favorite = false,
                });
                return r;
            }).ToArray(),
            suggested = new object[0],
            blocklist = new object[0],
            settings = new {
                acceptInvites = "public",
            }
        };
    }

    [HttpGet("api/v1/{accountId}/friends/{friendId}")]
    public object FriendsGet() {
        Response.StatusCode = 501;
        return EpicError.Create(
            "errors.unknown", 0,
            "Not Implemented",
            "fortnite", "prod-live"
        );
    }

    [HttpPost("api/v1/{accountId}/friends/{friendId}")]
    public object FriendsPost() {
        Response.StatusCode = 501;
        return EpicError.Create(
            "errors.unknown", 0,
            "Not Implemented",
            "fortnite", "prod-live"
        );
    }

    [HttpDelete("api/v1/{accountId}/friends/{friendId}")]
    public object FriendsDelete() {
        Response.StatusCode = 501;
        return EpicError.Create(
            "errors.unknown", 0,
            "Not Implemented",
            "fortnite", "prod-live"
        );
    }

    [HttpGet("api/v1/{accountId}/blocklist")]
    public object BlockList() {
        var accountId = (string)Request.RouteValues["accountId"]!;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"friends:{accountId}", "READ", "friends");
        }

        return new object[0];
    }

    [HttpGet("api/v1/{accountId}/settings")]
    public object Settings() {
        var accountId = (string)Request.RouteValues["accountId"]!;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"friends:{accountId}", "READ", "friends");
        }

        return new {
            acceptInvites = "public",
        };
    }

    [HttpGet("api/v1/{accountId}/recent/Fortnite")]
    public object RecentFortnite() {
        return new object[0];
    }
}