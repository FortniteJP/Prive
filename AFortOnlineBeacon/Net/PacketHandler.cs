using System.Net;

namespace AFortOnlineBeacon.Net;

public record FIncomingPacketRef(FBitReader Packet, IPEndPoint Address, FInPacketTraits Traits);

public record ProcessedPacket(byte[] Data, int CountBits, bool Error = false);

public class PacketHandler {
    private static readonly IPEndPoint EmptyAddress = new IPEndPoint(IPAddress.None, 0);
    
    private readonly List<HandlerComponent> _HandlerComponents;
    
    private bool _bConnectionlessHandler;
    private bool _bRawSend;
    
    /// <summary>
    ///     The maximum supported packet size (reflects UNetConnection::MaxPacket)
    /// </summary>
    private uint _MaxPacketBits;
    
    private HandlerState _State;
    private ReliabilityHandlerComponent? _ReliabilityComponent;

    private FBitWriter _OutgoingPacket;
    private FBitReader _IncomingPacket;

    private bool _bBeganHandshaking;
    private Action HandshakeCompleteAction;

    public PacketHandler() {
        _bConnectionlessHandler = false;
        _State = HandlerState.Uninitialized;
        _HandlerComponents = new List<HandlerComponent>();
        _ReliabilityComponent = null;
        _OutgoingPacket = new FBitWriter(0, true, false);
        _OutgoingPacket.AllowAppend(1);
        _IncomingPacket = new FBitReader(Array.Empty<byte>());
    }

    public HandlerMode Mode { get; private set; }

    public void Tick(float deltaTime) {
        foreach (var component in _HandlerComponents) component.Tick(deltaTime);
    }

    public void Initialize(HandlerMode mode, uint inMaxPacketBits, bool bConnectionlessOnly) {
        Mode = mode;
        _MaxPacketBits = inMaxPacketBits;
        _bConnectionlessHandler = bConnectionlessOnly;

        if (!_bConnectionlessHandler) {
            // TODO: Load from .ini file (GEngineIni)
            //  %s PacketHandlerProfileConfig (Driver..)
            //  Components
            
            // If no matches, load from PacketHandlerComponents / Components in GEngineIni
        }
        
        // TODO: FEncryptionComponent.
        
        // TODO: ReliabilityHandlerComponent.
    }

    public void InitializeComponents() {
        if (_State == HandlerState.Uninitialized) {
            if (_HandlerComponents.Count > 0) SetState(HandlerState.InitializingComponents);
            else HandlerInitialized();
        }

        foreach (var component in _HandlerComponents) {
            if (component.IsValid() && !component.IsInitialized()) component.Initialize();
        }
        
        // Called early, to ensure that all handlers report a valid reserved packet bits value (triggers an assert if not)
        GetTotalReservedPacketBits();
    }

    public bool IncomingConnectionless(FReceivedPacketView packetView) {
        packetView.Traits.ConnectionlessPacket = true;

        return Incoming_Internal(packetView);
    }

    public ProcessedPacket OutgoingConnectionless(IPEndPoint address, byte[] packet, int countBits, FOutPacketTraits traits) => Outgoing_Internal(packet, countBits, traits, true, address);

    public ProcessedPacket Outgoing(byte[] packet, int countBits, FOutPacketTraits traits) => Outgoing_Internal(packet, countBits, traits, false, EmptyAddress);

    public void BeginHandshaking(Action handshakeComplete = null) {
        _bBeganHandshaking = true;

        HandshakeCompleteAction = handshakeComplete;

        foreach (var component in _HandlerComponents) {
            if (component.RequiresHandshake && !component.IsInitialized()) component.NotifyHandshakeBegin();
        }
    }

    public HandlerComponent AddHandler<T>() where T : HandlerComponent {
        var result = (HandlerComponent) Activator.CreateInstance(typeof(T), this)!;

        _HandlerComponents.Add(result);
        
        return result;
    }

    public bool Incoming(FReceivedPacketView packetView) => Incoming_Internal(packetView);

    public void IncomingHigh(FBitReader reader) {
        // NO-OP
    }

    public void OutgoingHigh(FBitWriter writer) {
        // NO-OP
    }

    private bool Incoming_Internal(FReceivedPacketView packetView) {
        var returnVal = true;
        var dataView = packetView.DataView;
        var countBits = dataView.NumBits();

        if (_HandlerComponents.Count > 0) {
            var data = dataView.GetData();
            var lastByte = data[dataView.NumBytes() - 1];
            if (lastByte != 0) {
                countBits--;

                while ((lastByte & 0x80) == 0) {
                    lastByte *= 2;
                    countBits--;
                }
            } else returnVal = false;
        }

        if (returnVal) {
            var processPacketReader = new FBitReader(dataView.GetData(), countBits);
            var packetRef = new FIncomingPacketRef(processPacketReader, packetView.Address, packetView.Traits);
            
            if (_State == HandlerState.Uninitialized) UpdateInitialState();
            
            foreach (var component in _HandlerComponents) {
                if (processPacketReader.GetPosBits() != 0 && !component.CanReadUnaligned()) RealignPacket(processPacketReader);

                if (packetView.Traits.ConnectionlessPacket) component.IncomingConnectionless(packetRef);
                else component.Incoming(processPacketReader);
            }

            if (!processPacketReader.IsError()) {
                ReplaceIncomingPacket(processPacketReader);

                // packetView.DataView = new FPacketDataView(_IncomingPacket.GetBuffer(), _IncomingPacket.GetBitsLeft(), ECountUnits.Bits);
            }
            else returnVal = false;
        }

        return returnVal;
    }

    private ProcessedPacket Outgoing_Internal(byte[] packet, int countBits, FOutPacketTraits traits, bool bConnectionLess, IPEndPoint address) {
        if (!_bRawSend) {
            _OutgoingPacket.Reset();

            if (_State == HandlerState.Uninitialized) UpdateInitialState();

            if (_State == HandlerState.Initialized) {
                _OutgoingPacket.SerializeBits(packet, countBits);

                foreach (var component in _HandlerComponents) {
                    if (component.IsActive()) {
                        if (_OutgoingPacket.GetNumBits() <= component.MaxOutgoingBits) {
                            if (bConnectionLess) component.OutgoingConnectionless(address, _OutgoingPacket, traits);
                            else component.Outgoing(ref _OutgoingPacket, traits);
                        } else {
                            _OutgoingPacket.SetError();
                            // Logger.Error("Packet exceeded HandlerComponents 'MaxOutgoingBits' value: {A} vs {B}", _OutgoingPacket.GetNumBits(), component.MaxOutgoingBits);
                            break;
                        }
                    }
                }
                
                // Add a termination bit, the same as the UNetConnection code does, if appropriate
                if (_HandlerComponents.Count > 0 && _OutgoingPacket.GetNumBits() > 0) _OutgoingPacket.WriteBit(true);

                if (!bConnectionLess && _ReliabilityComponent != null && _OutgoingPacket.GetNumBits() > 0) {
                    // Let the reliability handler know about all processed packets, so it can record them for resending if needed
                    throw new NotImplementedException();
                }
            }
            // Buffer any packets being sent from game code until processors are initialized
            else if (_State == HandlerState.InitializingComponents && countBits > 0) {
                throw new NotImplementedException();
            }

            if (!_OutgoingPacket.IsError()) return new(_OutgoingPacket.GetData(), (int)_OutgoingPacket.GetNumBits());

            return new(packet, countBits, true);
        }

        return new(packet, countBits);
    }

    private void SetState(HandlerState state) {
        if (state == _State) {
            // Logger.Fatal("Set PacketHandler state to the state it is currently in ({State})", state);
        } else _State = state;
    }

    private void UpdateInitialState() {
        if (_State == HandlerState.Uninitialized) {
            if (_HandlerComponents.Count > 0) InitializeComponents();
            else HandlerInitialized();
        }
    }

    private void HandlerInitialized() {
        if (_ReliabilityComponent != null) {
            throw new NotImplementedException();
        }
        
        SetState(HandlerState.Initialized);

        if (_bBeganHandshaking) HandshakeCompleteAction();
    }

    public bool IsFullyInitialized() => _State == HandlerState.Initialized;

    private void ReplaceIncomingPacket(FBitReader replacementPacket) {
        // Console.WriteLine($"GetPosBits({replacementPacket.Pos}) => {replacementPacket.GetPosBits()}, GetBitsLeft({replacementPacket.GetNumBits()} - {replacementPacket.Pos}) => {replacementPacket.GetBitsLeft()}");
        if (replacementPacket.GetPosBits() == 0 || replacementPacket.GetBitsLeft() == 0) _IncomingPacket = replacementPacket;
        else {
            var tempPacketData = new byte[replacementPacket.GetBytesLeft()];
            var newPacketSizeBits = replacementPacket.GetBitsLeft();
            
            replacementPacket.SerializeBits(tempPacketData, newPacketSizeBits);
            
            // new FBitReader(tempPacketData, newPacketSizeBits); // this causes error
            _IncomingPacket = new FBitReader(null, newPacketSizeBits); // temporary fix 💀
        }
    }

    private void RealignPacket(FBitReader packet) {
        // Logger.Warning("Realigning packet, which is untested");
        
        if (packet.GetPosBits() != 0)
        {
            var bitsLeft = packet.GetBitsLeft();
            if (bitsLeft > 0) {
                var tempPacketData = new byte[packet.GetBytesLeft()];

                packet.SerializeBits(tempPacketData, bitsLeft);
                packet.SetData(tempPacketData, bitsLeft);
            }
        }
    }

    public void SetRawSend(bool enabled) => _bRawSend = enabled;

    public bool GetRawSend() => _bRawSend;

    public int GetTotalReservedPacketBits() {
        var returnVal = 0;
        var curMaxOutgoingBits = _MaxPacketBits;

        for (int i = _HandlerComponents.Count - 1; i >= 0; i--) {
            var curComponent = _HandlerComponents[i];
            var curReservedBits = curComponent.GetReservedPacketBits();
            
            // Specifying the reserved packet bits is mandatory, even if zero (as accidentally forgetting, leads to hard to trace issues).
            if (curReservedBits == -1) {
                throw new UnrealNetException("Handler returned invalid 'GetReservedPacketBits' value");
            }

            curComponent.MaxOutgoingBits = curMaxOutgoingBits;
            curMaxOutgoingBits -= (uint) curReservedBits;

            returnVal += curReservedBits;
        }
        
        // Reserve space for the termination bit
        if (_HandlerComponents.Count > 0) returnVal++;

        return returnVal;
    }

    public void HandlerComponentInitialized(HandlerComponent inComponent) {
        // Check if all handlers are initialized
        if (_State != HandlerState.Initialized) {
            var bAllInitialized = true;
            var bEncounteredComponent = false;
            var bPassedHandshakeNotify = false;

            for (int i = _HandlerComponents.Count - 1; i >= 0; --i) {
                var curComponent = _HandlerComponents[i];

                if (!curComponent.IsInitialized()) bAllInitialized = false;

                if (bEncounteredComponent) {
                    // If the initialized component required a handshake, pass on notification to the next handshaking component
                    // (components closer to the Socket, perform their handshake first)
                    if (_bBeganHandshaking && !curComponent.IsInitialized() && inComponent.RequiresHandshake && !bPassedHandshakeNotify && curComponent.RequiresHandshake) {
                        curComponent.NotifyHandshakeBegin();
                        bPassedHandshakeNotify = true;
                    }
                } else bEncounteredComponent = curComponent == inComponent;
            }

            if (bAllInitialized) HandlerInitialized();
        }
    }
}