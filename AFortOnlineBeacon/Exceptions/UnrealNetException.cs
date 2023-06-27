using System.Runtime.Serialization;

namespace AFortOnlineBeacon.Exceptions;

public class UnrealNetException : UnrealException {
    public UnrealNetException() : base() {}
    public UnrealNetException(string? message) : base(message) {}
    public UnrealNetException(string? message, Exception? innerException) : base(message, innerException) {}
    protected UnrealNetException(SerializationInfo info, StreamingContext context) : base(info, context) {}
}