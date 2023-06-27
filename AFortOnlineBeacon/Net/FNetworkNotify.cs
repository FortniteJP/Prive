namespace AFortOnlineBeacon.Net;

public interface FNetworkNotify {
    EAcceptConnection NotifyAcceptingConnection();
    void NotifyAcceptedConnection(UNetConnection connection);
    bool NotifyAcceptingChannel(UChannel channel);
    void NotifyControlMessage(UNetConnection connection, NMT messageType, FInBunch bunch);
}