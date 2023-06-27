using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace AFortOnlineBeacon.Net;

public record FReceivedPacket(IPEndPoint Address, byte[] Buffer, DateTimeOffset Timestamp);

public class FReceiveThreadRunnable : IAsyncDisposable {
    private readonly UIpNetDriver _Driver;
    private readonly CancellationTokenSource _Cancellation;
    private readonly ConcurrentQueue<FReceivedPacket> _ReceiveQueue;

    private Task? _ReceiveTask;

    public FReceiveThreadRunnable(UIpNetDriver driver) {
        _Driver = driver;
        _Cancellation = new CancellationTokenSource();
        _ReceiveQueue = new ConcurrentQueue<FReceivedPacket>();
    }
    
    public void Start() => _ReceiveTask = ReceiveAsync();

    public bool TryReceive([MaybeNullWhen(false)] out FReceivedPacketView result) {
        if (_ReceiveQueue.TryDequeue(out var packet)) {
            result = new FReceivedPacketView(
                new FPacketDataView(packet.Buffer, packet.Buffer.Length, ECountUnits.Bytes),
                packet.Address, 
                new FInPacketTraits());
            
            return true;
        }

        result = null;
        return false;
    }
    
    private async Task ReceiveAsync() {
        // Logger.Information("Started listening on {ServerIp}", _Driver.ServerIp);
        Console.WriteLine($"Started listening on {_Driver.ServerIp}");
        
        try {
            while (!_Cancellation.IsCancellationRequested) {
                var result = await _Driver.Socket.ReceiveAsync(_Cancellation.Token);

                if (result.Buffer.Length == 0) continue;
            
                if (result.Buffer.Length > UNetConnection.MaxPacketSize) {
                    // Logger.Warning("Received packet exceeding MaxPacketSize ({Size} > {Max}) from {Ip}", 
                    //     result.Buffer.Length, UNetConnection.MaxPacketSize, result.RemoteEndPoint);
                    continue;
                }

                _ReceiveQueue.Enqueue(new FReceivedPacket(result.RemoteEndPoint, result.Buffer, DateTimeOffset.UtcNow));
            }
        } catch (Exception e) {
            if (e is OperationCanceledException && !_Cancellation.IsCancellationRequested) {
                // Logger.Error(e, "Exception caught");
            }
        }
        
        // Logger.Information("Stopped listening");
    }

    public async ValueTask DisposeAsync() {
        _Cancellation.Cancel();
        
        if (_ReceiveTask != null) await _ReceiveTask;
        
        _Cancellation.Dispose();
    }
}