using System.Net;

namespace AFortOnlineBeacon.Net;

public abstract class HandlerComponent {
    private bool _bActive;
    private bool _bInitialized;
    private string _Name;

    protected HandlerComponent(PacketHandler handler, string name) {
        _Name = name;
        RequiresHandshake = false;
        RequiresReliability = false;
        _bActive = false;
        _bInitialized = false;

        Handler = handler;
        State = HandlerComponentState.UnInitialized;
    }
    
    protected PacketHandler Handler { get; }
    protected HandlerComponentState State { get; private set; }
    
    public bool RequiresHandshake { get; protected set; }
    public bool RequiresReliability { get; protected set; }
    
    /// <summary>
    ///     Maximum number of Outgoing packet bits supported (automatically calculated to factor in other HandlerComponent reserved bits)
    /// </summary>
    public uint MaxOutgoingBits { get; set; }
    
    public virtual bool IsActive() => _bActive;

    public virtual bool IsValid() => false;

    public bool IsInitialized() => _bInitialized;

    public virtual void Incoming(FBitReader packet) {}

    public virtual void Outgoing(ref FBitWriter packet, FOutPacketTraits traits) {}

    public virtual void IncomingConnectionless(FIncomingPacketRef packetRef) {}

    public virtual void OutgoingConnectionless(IPEndPoint address, FBitWriter packet, FOutPacketTraits traits) {}

    public virtual bool CanReadUnaligned() => false;

    public abstract void Initialize();

    public virtual void NotifyHandshakeBegin() {}

    public virtual void Tick(float deltaTime) {}

    public virtual void SetActive(bool active) => _bActive = active;

    public virtual int GetReservedPacketBits() => 0;

    public virtual void CountBytes(FArchive ar) {}

    protected void SetState(HandlerComponentState state) => State = state;
    
    protected void Initialized() {
        _bInitialized = true;
        Handler.HandlerComponentInitialized(this);
    }
}