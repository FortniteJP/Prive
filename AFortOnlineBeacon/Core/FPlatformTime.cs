namespace AFortOnlineBeacon.Core;

public static class FPlatformTime {
    public static float Seconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}