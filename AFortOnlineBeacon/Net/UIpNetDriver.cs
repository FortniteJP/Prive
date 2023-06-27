using System.Net;
using System.Net.Sockets;

namespace AFortOnlineBeacon.Net;

public class UIpNetDriver : UNetDriver {
    private bool _IsDisposed;
    
    public UIpNetDriver(IPAddress host, int port, bool server) {
        if (server) {
            ServerIp = new IPEndPoint(host, port);
            Socket = new UdpClient(ServerIp); // binds to local
        } else {
            Socket = new UdpClient(host.ToString(), port); // binds to remote, establishes local port
            ServerIp = new IPEndPoint(host, ((IPEndPoint)Socket.Client.LocalEndPoint!).Port);
        }
        ReceiveThread = new FReceiveThreadRunnable(this);
    }

    public IPEndPoint ServerIp { get; }
    public UdpClient Socket { get; }
    public FReceiveThreadRunnable ReceiveThread { get; }

    public bool InitConnect(FNetworkNotify InNotify, FUrl ConnectURL) {
        if (!Init(InNotify)) {
            // Logger.Warning("Failed to init net driver ConnectURL: %s", ConnectURL.ToString());
            return false;
        }

        // Create new connection.
        var ipConnection = new UIpConnection();
        ServerConnection = ipConnection;
        ServerConnection.InitLocalConnection(this, Socket, ConnectURL, EConnectionState.USOCK_Pending);
        int DestinationPort = ConnectURL.Port;
        // Logger.Warning("Game client on port %i, rate %i", DestinationPort, ServerConnection.CurrentNetSpeed);
        CreateInitialClientChannels();
        ReceiveThread.Start();
        return true;
    }

    public bool InitListen(FNetworkNotify notify) {
        if (!Init(notify)) return false;
        
        // Initialize connectionless packet handler.
        InitConnectionlessHandler();
        
        // Start receiving packets.
        ReceiveThread.Start();

        return true;
    }
    
    public override void TickDispatch(float deltaTime) {
        base.TickDispatch(deltaTime);
        
        while (ReceiveThread.TryReceive(out var packet)) {
            // Logger.Information("Received from {Adress} data {Buffer}", packet.Address, packet.DataView.GetData());
            Console.WriteLine($"Recv {packet.DataView.NumBytes()} {packet.Address}, {BitConverter.ToString(packet.DataView.GetData())}");

            UNetConnection? connection = ServerConnection;

            if (Equals(packet.Address, ServerIp)) {
                // TODO: Assign connection.
                throw new NotImplementedException();
            }

            if (connection == null) MappedClientConnections.TryGetValue(packet.Address, out connection);
            var bIgnorePacket = false;
            
            // If we didn't find a client connection, maybe create a new one.
            if (connection == null) {
                connection = ProcessConnectionlessPacket(packet);
                bIgnorePacket = packet.DataView.NumBytes() == 0;
            }
            
            // Send the packet to the connection for processing.
            if (connection != null && !bIgnorePacket) connection.ReceivedRawPacket(packet);
        }
    }

    private UNetConnection? ProcessConnectionlessPacket(FReceivedPacketView packet) {
        UNetConnection? returnVal = null;
        var statelessConnect = StatelessConnectComponent;
        var address = packet.Address;
        var bPassedChallenge = false;
        var bRestartedHandshake = false;
        var bIgnorePacket = true;
        
        if (ConnectionlessHandler != null && statelessConnect != null) {
            var result = ConnectionlessHandler.IncomingConnectionless(packet);
            if (result) {
                bPassedChallenge = statelessConnect.HasPassedChallenge(address, out bRestartedHandshake);

                if (bPassedChallenge) {
                    if (bRestartedHandshake) {
                        StatelessConnectHandlerComponent? curComp;
                        UIpConnection? foundConn = null;

                        foreach (var curConn in ClientConnections) {
                            curComp = curConn is not null ? curConn.StatelessConnectComponent : null;

                            if (curComp?.IsValid() ?? false) {
                                foundConn = (UIpConnection?)curConn;
                                break;
                            }
                        }

                        if (foundConn is not null) {
                            UNetConnection? removedConn = null;
                            var remoteAddr = foundConn.RemoteAddr;

                            // verify(MappedClientConnections.RemoveAndCopyValue(RemoteAddrRef, RemovedConn) && RemovedConn == FoundConn);

                            var bIsValid = false;

                            string oldAddress = remoteAddr.ToString();

                            remoteAddr.Address = address.Address;
                            remoteAddr.Port = address.Port;

                            MappedClientConnections.TryAdd(remoteAddr, foundConn);

                            // int32 recentDisconnectIdx = ...

                            returnVal = foundConn;
                        }
                    }

                    var newCountBytes = packet.DataView.NumBytes();
                    var workingData = new byte[newCountBytes];

                    if (newCountBytes > 0) {
                        Buffer.BlockCopy(packet.DataView.GetData(), 0, workingData, 0, newCountBytes);
                        bIgnorePacket = false;
                    }

                    packet.DataView = new FPacketDataView(workingData, newCountBytes, ECountUnits.Bytes);
                }
            }
        } else {
            // Logger.Warning("Invalid ConnectionlessHandler or StatelessConnectComponent, can't accept connections");
            Console.WriteLine("Invalid ConnectionlessHandler or StatelessConnectComponent, can't accept connections");    
        }

        if (bPassedChallenge) {
            if (!bRestartedHandshake) {
                // Logger.Verbose("Server accepting post-challenge connection from: {Address}", address);

                returnVal = new UIpConnection();

                if (statelessConnect != null) {
                    statelessConnect.GetChallengeSequences(out var serverSequence, out var clientSequence);

                    returnVal.InitSequence(clientSequence, serverSequence);
                }

                returnVal.InitRemoteConnection(this, Socket, World != null ? World.Url : new FUrl(), address, EConnectionState.USOCK_Open);

                if (returnVal.Handler != null) returnVal.Handler.BeginHandshaking();

                AddClientConnection(returnVal);
            }

            if (statelessConnect != null) statelessConnect.ResetChallengeData();
        }

        if (bIgnorePacket) packet.DataView = new FPacketDataView(packet.DataView.GetData(), 0, ECountUnits.Bits);
        
        return returnVal;
    }

    public override void LowLevelSend(IPEndPoint address, byte[] data, int countBits, FOutPacketTraits traits) {
        if (ConnectionlessHandler != null) {
            var processedData = ConnectionlessHandler.OutgoingConnectionless(address, data, countBits, traits);
            if (!processedData.Error) {
                data = processedData.Data;
                countBits = processedData.CountBits;
            } else countBits = 0;
        }

        if (countBits > 0) Socket.Send(data, FMath.DivideAndRoundUp(countBits, 8), address);
        Console.WriteLine($"Sent {FMath.DivideAndRoundUp(countBits, 8)} {address.ToString()}, {BitConverter.ToString(data)}");
    }

    public override bool IsNetResourceValid() => !_IsDisposed;

    public override async ValueTask DisposeAsync() {
        _IsDisposed = true;
        
        await base.DisposeAsync();
        
        Socket.Dispose();
        
        await ReceiveThread.DisposeAsync();
    }
}