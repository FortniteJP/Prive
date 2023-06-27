using System.Runtime.Serialization;

namespace AFortOnlineBeacon.Exceptions;

public class UnrealException : Exception {
    public UnrealException() : base() {}
    public UnrealException(string? message) : base(message) {}
    public UnrealException(string? message, Exception? innerException) : base(message, innerException) {}
    protected UnrealException(SerializationInfo info, StreamingContext context) : base(info, context) {}
}