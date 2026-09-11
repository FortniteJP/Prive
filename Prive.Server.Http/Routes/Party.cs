using System.Text.Json;

namespace Prive.Server.Http.Routes;

public static class PartyRoutes {
    public static void Map(IEndpointRouteBuilder app) {
        var party = app.MapGroup("/party");

        party.MapGet("/api/v1/Fortnite/user/{accountId}", FortniteUser);
        party.MapPost("/api/v1/Fortnite/parties", (Delegate)CreateParty);
    }

    static object FortniteUser(string accountId) => new {
        current = Parties.Where(x => x.Members.Any(y => y.AccountId == accountId)).ToArray(),
        invites = new object[0],
        pending = new object[0],
        pings = new object[0]
    };

    /// <summary>
    ///     Creates the party the client asks for and answers with it.
    ///     <para>
    ///         This used to answer <c>{}</c>, which the client rejected with "Unable to deserialize
    ///         object FPartyConfigInfo because the input is not a JSON object literal" - it reads
    ///         <c>config</c> as a required field and there was none. Nothing else in the party
    ///         system can start until this call succeeds.
    ///     </para>
    ///     <para>
    ///         The request's own <c>config</c> and <c>meta</c> are echoed back verbatim rather than
    ///         rebuilt. That is deliberate: the client's config carries enums this server has no
    ///         reason to have an opinion about (the log shows Publish(Noone), Invite(Noone),
    ///         JoinRequestAction(Manual)), and handing back what was asked for cannot get them wrong.
    ///     </para>
    /// </summary>
    static async Task<object> CreateParty(HttpContext ctx) {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        Console.WriteLine($"CreateParty: {body}");

        var accountId = (ctx.Items["AuthToken"] as AuthToken)?.AccountId;
        if (accountId is null) {
            ctx.Response.StatusCode = 401;
            return EpicError.Create(
                "errors.com.epicgames.common.authentication_failed", 1032,
                "Authentication failed for /party/api/v1/Fortnite/parties",
                "com.epicgames.social.party", "prod"
            );
        }

        JsonElement? request = null;
        try {
            if (body.Length > 0) request = JsonDocument.Parse(body).RootElement.Clone();
        } catch (JsonException e) {
            Console.WriteLine($"CreateParty: could not parse the request body - {e.Message}");
        }

        var party = new Party() {
            Id = GenerateToken(),
            // Fall back to the defaults only when the client sent nothing usable, so a malformed
            // request still gets a party rather than a failed login.
            Config = Sub(request, "config") ?? (object)new PartyConfig(),
            Meta = Sub(request, "meta") ?? (object)new Dictionary<string, string>(),
            Revision = 0
        };

        var member = new PartyMember() {
            AccountId = accountId,
            Role = "CAPTAIN", // whoever creates it leads it
            Meta = Sub(request, "meta") ?? (object)new Dictionary<string, string>()
        };

        // join_info carries the creator's XMPP connection - the party is addressed over XMPP, so
        // without it the client has a party it cannot be reached in.
        if (Sub(request, "join_info") is JsonElement joinInfo
            && joinInfo.ValueKind == JsonValueKind.Object
            && joinInfo.TryGetProperty("connection", out var connection)
            && connection.ValueKind == JsonValueKind.Object
            && connection.TryGetProperty("id", out var connectionId)
            && connectionId.ValueKind == JsonValueKind.String) {
            member.Connections.Add(new() {
                Id = connectionId.GetString()!,
                Meta = connection.TryGetProperty("meta", out var connectionMeta)
                    ? connectionMeta.Clone()
                    : (object)new Dictionary<string, string>()
            });
        }

        party.Members.Add(member);

        lock (Parties) {
            // One party per player: a second create replaces whatever they were in, which is what
            // the client assumes when it retries after a failed login.
            Parties.RemoveAll(x => x.Members.Any(y => y.AccountId == accountId));
            Parties.Add(party);
        }

        return party;
    }

    /// <summary>A sub-object of the request, kept as raw JSON so it round-trips unchanged.</summary>
    static JsonElement? Sub(JsonElement? request, string name)
        => request is JsonElement element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
                ? value.Clone()
                : null;
}
