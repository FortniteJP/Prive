using MongoDB.Bson;

namespace Prive.Server.Http;

public class Friend {
    public ObjectId _id { get; set; }

    public required string AccountId { get; set; }
    public List<FriendElement> Incoming { get; set; } = new();
    public List<FriendElement> Outgoing { get; set; } = new();
    public List<FriendElement> Accepted { get; set; } = new();
}

public class FriendElement {
    public required string AccountId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}