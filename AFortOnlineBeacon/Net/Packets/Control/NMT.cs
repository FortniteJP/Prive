namespace AFortOnlineBeacon.Net.Packets.Control;

public enum NMT {
    Hello = 0,
    Welcome = 1,
    Upgrade = 2,
    Challenge = 3,
    Netspeed = 4,
    Login = 5,
    Failure = 6,
    Join = 9,
    JoinSplit = 10,
    Skip = 12,
    Abort = 13,
    PCSwap = 15,
    ActorChannelFailure = 16,
    DebugText = 17,
    NetGUIDAssign = 18,
    SecurityViolation = 19,
    GameSpecific = 20,
    EncryptionAck = 21,
    DestructionInfo = 22,
    BeaconWelcome = 25,
    BeaconJoin = 26,
    BeaconAssignGUID = 27,
    BeaconNetGUIDAck = 28
}