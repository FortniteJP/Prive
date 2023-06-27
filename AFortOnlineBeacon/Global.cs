global using AFortOnlineBeacon.Runtime;
global using AFortOnlineBeacon.Core;
global using AFortOnlineBeacon.Core.Math;
global using AFortOnlineBeacon.Core.Names;
global using AFortOnlineBeacon.Core.Objects;
global using AFortOnlineBeacon.Core.Properties;
global using AFortOnlineBeacon.Exceptions;
global using AFortOnlineBeacon.Net;
global using AFortOnlineBeacon.Net.Actors;
global using AFortOnlineBeacon.Net.Channels;
global using AFortOnlineBeacon.Net.Channels.Actor;
global using AFortOnlineBeacon.Net.Channels.Control;
global using AFortOnlineBeacon.Net.Channels.Voice;
global using AFortOnlineBeacon.Net.Packets.Bunch;
global using AFortOnlineBeacon.Net.Packets.Control;
global using AFortOnlineBeacon.Net.Packets.Header;
global using AFortOnlineBeacon.Net.Packets.Header.Sequence;
global using AFortOnlineBeacon.Serialization;

global using static Global;

public static class Global {
    public static void ShowStack() {
        var stackTrace = new System.Diagnostics.StackTrace();
        Console.WriteLine("ShowStack Start");
        foreach(var stackFrame in stackTrace.GetFrames()[..^2]) if (stackFrame.GetMethod()?.DeclaringType?.FullName is var t && (t?.StartsWith("AFortOnlineBeacon") ?? false)) Console.WriteLine($"{t}.{stackFrame.GetMethod()?.Name ?? "NULL"}");
    }
}