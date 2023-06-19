using MongoDB.Bson;

namespace Prive.Server.Http;

public class User {
    public ObjectId _id { get; set; }
    public required string AccountId { get; set; }
    public required string DisplayName { get; set; }
    public required string Email { get; set; }
    public required string Password { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? DiscordAccountId { get; set; }
}