using System.Runtime.Serialization;

namespace AFortOnlineBeacon.Exceptions;

public class UnrealSerializationException : Exception {
    public UnrealSerializationException() : base() {}
    public UnrealSerializationException(string? message) : base(message) {}
    public UnrealSerializationException(string? message, Exception? innerException) : base(message, innerException) {}
    protected UnrealSerializationException(SerializationInfo info, StreamingContext context) : base(info, context) {}
}