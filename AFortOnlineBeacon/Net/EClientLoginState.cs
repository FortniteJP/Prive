namespace AFortOnlineBeacon.Net;

public enum EClientLoginState {
    Invalid = 0,
    LoggingIn = 1,
    Welcomed = 2,
    ReceivedJoin = 3,
    CleanedUp = 4
}