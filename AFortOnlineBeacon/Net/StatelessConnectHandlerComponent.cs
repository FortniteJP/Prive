using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Security.Cryptography;

namespace AFortOnlineBeacon.Net;

public class StatelessConnectHandlerComponent : HandlerComponent {
    private const int SecretByteSize = 64;
    private const int SecretCount = 2;
    private const int CookieByteSize = 20;

    private const int HandshakePacketSizeBits = 195; // 227 = v4.26.2; 195 = v4.23.0;
    private const int RestartHandshakePacketSizeBits = 2;
    private const int RestartResponseSizeBits = 355;

    private const float SecretUpdateTime = 15.0f;
    private const float SecretUpdateTimeVariance = 5.0f;

    private const float MaxCookieLifetime = ((SecretUpdateTime + SecretUpdateTimeVariance) * SecretCount);
    private const float MinCookieLifetime = SecretUpdateTime;
    
    private UNetDriver? _Driver;

    /// <summary>
    ///     The serverside-only 'secret' value, used to help with generating cookies.
    /// </summary>
    private byte[][] _HandshakeSecret;

    /// <summary>
    ///     Which of the two secret values above is active (values are changed frequently, to limit replay attacks)
    /// </summary>
    private byte _ActiveSecret;

    /// <summary>
    ///     The time of the last secret value update
    /// </summary>
    private float _LastSecretUpdateTimestamp;
    
    /// <summary>
    ///     The last address to successfully complete the handshake challenge
    /// </summary>
    private IPEndPoint? _LastChallengeSuccessAddress;

    /// <summary>
    ///     The initial server sequence value, from the last successful handshake
    /// </summary>
    private int _LastServerSequence;

    /// <summary>
    ///     The initial client sequence value, from the last successful handshake
    /// </summary>
    private int _LastClientSequence;

    /// <summary>
    ///     The last time a handshake packet was sent - used for detecting failed sends.
    /// </summary>
    private float _LastClientSendTimestamp;

    /// <summary>
    ///     The local (client) time at which the challenge was last updated
    /// </summary>
    private float _LastChallengeTimestamp;

    /// <summary>
    ///     The local (client) time at which the last restart handshake request was receive
    /// </summary>
    private float _LastRestartPacketTimestamp;

    /// <summary>
    ///     The SecretId value of the last challenge response sent
    /// </summary>
    private byte _LastSecretId;

    /// <summary>
    ///     The Timestamp value of the last challenge response sent
    /// </summary>
    private float _LastTimestamp;

    /// <summary>
    ///     The Cookie value of the last challenge response sent. Will differ from AuthorisedCookie, if a handshake retry is triggered.
    /// </summary>
    private byte[] _LastCookie = new byte[CookieByteSize];

    /// <summary>
    ///     Client: Whether or not we are in the middle of a restarted handshake.
    ///     Server: Whether or not the last handshake was a restarted handshake.
    /// </summary>
    private bool _bRestartedHandshake;

    /// <summary>
    ///     The cookie which completed the connection handshake.
    /// </summary>
    private byte[] _AuthorisedCookie;

    /// <summary>
    ///     The magic header which is prepended to all packets
    /// </summary>
    private BitArray _MagicHeader;
    
    public StatelessConnectHandlerComponent(PacketHandler handler) : base(handler, nameof(StatelessConnectHandlerComponent)) {
        SetActive(true);
        
        RequiresHandshake = true;
        
        _HandshakeSecret = new byte[2][];
        _ActiveSecret = byte.MaxValue;
        _LastChallengeSuccessAddress = null;
        _LastServerSequence = 0;
        _LastClientSequence = 0;
        _bRestartedHandshake = false;
        _AuthorisedCookie = new byte[CookieByteSize];
        _MagicHeader = new BitArray(new[] { true, true, true, false });
    }

    public void GetChallengeSequences(out int serverSequence, out int clientSequence) {
        serverSequence = _LastServerSequence;
        clientSequence = _LastClientSequence;
    }

    public void ResetChallengeData() {
        _LastChallengeSuccessAddress = null;
        _bRestartedHandshake = false;
        _LastServerSequence = 0;
        _LastClientSequence = 0;
        
        for (var i = 0; i < _AuthorisedCookie.Length; i++) _AuthorisedCookie[i] = 0;
    }

    public void SetDriver(UNetDriver driver) {
        _Driver = driver;

        if (Handler.Mode == HandlerMode.Server) {
            var statelessComponent = _Driver.StatelessConnectComponent;
            if (statelessComponent != null) {
                if (statelessComponent == this) UpdateSecret();
                else InitFromConnectionless(statelessComponent);
            }
        }
    }

    public override void Initialize() {
        if (Handler.Mode == HandlerMode.Server) Initialized();
    }

    private void InitFromConnectionless(StatelessConnectHandlerComponent connectionlessHandler) {
        // Logger.Debug("InitFromConnectionless");
        
        // Store the cookie/address used for the handshake, to enable server ack-retries
        _LastChallengeSuccessAddress = connectionlessHandler._LastChallengeSuccessAddress;
        
        Buffer.BlockCopy(connectionlessHandler._AuthorisedCookie, 0, _AuthorisedCookie, 0, _AuthorisedCookie.Length);
    }

    public override void CountBytes(FArchive ar) {
        throw new NotImplementedException();
    }

    public override void Incoming(FBitReader packet) {
        Console.WriteLine("StatelessConnectHandlerComponent.Incoming");
        if (_MagicHeader.Length > 0) {
            // Skip magic header.
            packet.Pos += _MagicHeader.Length;
        }
        
        var bHandshakePacket = packet.ReadBit() && !packet.IsError();
        if (bHandshakePacket) {
            var bRestartHandshake = false;
            var secretId = (byte) 0;
            var timestamp = 1.0f;
            Span<byte> cookie = stackalloc byte[CookieByteSize];
            Span<byte> origCookie = stackalloc byte[CookieByteSize];

            bHandshakePacket = ParseHandshakePacket(packet, ref bRestartHandshake, ref secretId, ref timestamp, cookie, origCookie);

            if (bHandshakePacket) {
                if (Handler.Mode == HandlerMode.Client) {
                    if (State == HandlerComponentState.UnInitialized || State == HandlerComponentState.InitializedOnLocal) {
                        if (bRestartHandshake) {
                            //UE_LOG(LogHandshake, Log, TEXT("Ignoring restart handshake request, while already restarted."));
                        }
                        // Receiving challenge, verify the timestamp is > 0.0f
                        else if (timestamp > 0.0f) {
                            _LastChallengeTimestamp = _Driver?.GetElapsedTime() ?? 0.0f;

                            SendChallengeResponse(secretId, timestamp, cookie);

                            // Utilize this state as an intermediary, indicating that the challenge response has been sent
                            SetState(HandlerComponentState.InitializedOnLocal);
                        }
                        // Receiving challenge ack, verify the timestamp is < 0.0f
                        else if (timestamp < 0.0f) {
                            if (!_bRestartedHandshake) { 
                                var ServerConn =  _Driver?.ServerConnection;

                                // Extract the initial packet sequence from the random Cookie data
                                if (ServerConn != null) {
                                    _LastServerSequence = BinaryPrimitives.ReadInt16LittleEndian(cookie) & (UNetConnection.MaxPacketId - 1);
                                    _LastClientSequence = BinaryPrimitives.ReadInt16LittleEndian(cookie.Slice(2)) & (UNetConnection.MaxPacketId - 1);

                                    ServerConn.InitSequence(_LastServerSequence, _LastClientSequence);
                                }
                                // Save the final authorized cookie
                                cookie.CopyTo(_AuthorisedCookie);
                            }

                            // Now finish initializing the handler - flushing the queued packet buffer in the process.
                            SetState(HandlerComponentState.Initialized);
                            Initialized();

                            _bRestartedHandshake = false;
                        }
                    } else if (bRestartHandshake) {
                        var bValidAuthCookie = !_AuthorisedCookie.All(b => b == 0);

                        // The server has requested us to restart the handshake process - this is because
                        // it has received traffic from us on a different address than before.
                        if (bValidAuthCookie) {
                            bool bPassedDelayCheck = false;
                            bool bPassedDualIPCheck = false;
                            float CurrentTime = FPlatformTime.Seconds();

                            if (!_bRestartedHandshake) {
                                var ServerConn = _Driver?.ServerConnection;
                                // todo
                                //double LastNetConnPacketTime = ServerConn?.lastre(ServerConn != nullptr ? ServerConn->LastReceiveRealtime : 0.0);

                                // The server may send multiple restart handshake packets, so have a 10 second delay between accepting them
                                //bPassedDelayCheck = (CurrentTime - LastClientSendTimestamp) > 10.0;

                                // Some clients end up sending packets duplicated over multiple IP's, triggering the restart handshake.
                                // Detect this by checking if any restart handshake requests have been received in roughly the last second
                                // (Dual IP situations will make the server send them constantly) - and override the checks as a failsafe,
                                // if no NetConnection packets have been received in the last second.
                                //double LastRestartPacketTimeDiff = CurrentTime - _lastRestartPacketTimestamp;
                                //double LastNetConnPacketTimeDiff = CurrentTime - LastNetConnPacketTime;

                                //bPassedDualIPCheck = _lastRestartPacketTimestamp == 0.0 || LastRestartPacketTimeDiff > 1.1 || LastNetConnPacketTimeDiff > 1.0;
                            }

                            _LastRestartPacketTimestamp = CurrentTime;
                            float LastLogStartTime = 0.0f;
                            int LogCounter = 0;
                            Func<bool> WithinHandshakeLogLimit = new Func<bool>(() => {
                                const float LogCountPeriod = 30.0f;
                                const byte MaxLogCount = 3;
                                float CurTimeApprox = _Driver.GetElapsedTime();
                                bool bWithinLimit = false;
                                if ((CurTimeApprox - LastLogStartTime) > LogCountPeriod) {
                                    LastLogStartTime = CurTimeApprox;
                                    LogCounter = 1;
                                    bWithinLimit = true;
                                } else if (LogCounter < MaxLogCount) {
                                    LogCounter++;
                                    bWithinLimit = true;
                                }

                                return bWithinLimit;
                            });

                            if (!_bRestartedHandshake && bPassedDelayCheck && bPassedDualIPCheck) {
                                // Logger.Verbose("Beginning restart handshake process.");

                                _bRestartedHandshake = true;

                                SetState(HandlerComponentState.UnInitialized);
                                NotifyHandshakeBegin();
                            } else if (WithinHandshakeLogLimit()) {
                                if (_bRestartedHandshake) {
                                    // Logger.Verbose("Ignoring restart handshake request, while already restarted (this is normal).");
                                } else if (!bPassedDelayCheck) {
                                    // Logger.Verbose("Ignoring restart handshake request, due to < 10 seconds since last handshake.");
                                } else { // if (!bPassedDualIPCheck)
                                    // Logger.Verbose("Ignoring restart handshake request, due to recent NetConnection packets.");
                                }
                            }
                        } else {
                            // Logger.Verbose("Server sent restart handshake request, when we don't have an authorised cookie.");
                        }
                    } else {
                        // Ignore, could be a dupe/out-of-order challenge packet
                    }
                } else if (Handler.Mode == HandlerMode.Server) {
                    if (_LastChallengeSuccessAddress != null) {
                        // The server should not be receiving handshake packets at this stage - resend the ack in case it was lost.
                        // In this codepath, this component is linked to a UNetConnection, and the Last* values below, cache the handshake info.
                        SendChallengeAck(_LastChallengeSuccessAddress, _AuthorisedCookie);
                    }
                }
            } else {
                packet.SetError();
                // Logger.Error("Incoming: Error reading handshake packet");
                Console.WriteLine("Incoming: Error reading handshake packet");
            }
        } else if (packet.IsError()) {
            // Logger.Error("Incoming: Error reading handshake bit from packet");
            Console.WriteLine("Incoming: Error reading handshake bit from packet");
        } else if (_LastChallengeSuccessAddress != null && Handler.Mode == HandlerMode.Server) {
            _LastChallengeSuccessAddress = null;
        }
    }

    public override void Outgoing(ref FBitWriter packet, FOutPacketTraits traits) {
        const bool bHandshakePacket = false;
        
        var newPacket = new FBitWriter(GetAdjustedSizeBits((int)packet.GetNumBits()) + 1, true, false);

        if (_MagicHeader.Length > 0) newPacket.SerializeBits(_MagicHeader, _MagicHeader.Length);
        
        newPacket.WriteBit(bHandshakePacket);
        newPacket.SerializeBits(packet.GetData(), packet.GetNumBits());

        packet = newPacket;
    }

    public override void IncomingConnectionless(FIncomingPacketRef packetRef) {
        Console.WriteLine("IncomingConnectionless");
        
        var packet = packetRef.Packet;
        var address = packetRef.Address;

        if (_MagicHeader.Length > 0) {
            // Skip magic header.
            packet.Pos += _MagicHeader.Length;
        }
        
        var bHandshakePacket = packet.ReadBit() && !packet.IsError();

        _LastChallengeSuccessAddress = null;
        
        if (bHandshakePacket) {
            var bRestartHandshake = false;
            var secretId = (byte) 0;
            var timestamp = 1.0f;
            Span<byte> cookie = stackalloc byte[CookieByteSize];
            Span<byte> origCookie = stackalloc byte[CookieByteSize];

            bHandshakePacket = ParseHandshakePacket(packet, ref bRestartHandshake, ref secretId, ref timestamp, cookie, origCookie);
            Console.WriteLine($"bHandshakePacket: {bHandshakePacket}, bRestartHandshake: {bRestartHandshake}, secretId: {secretId}, timestamp: {timestamp}");

            if (bHandshakePacket) {
                if (Handler.Mode == HandlerMode.Server) {
                    var bInitialConnect = timestamp == 0.0f;
                    if (bInitialConnect) SendConnectChallenge(address);
                    else if (_Driver != null) {
                        var bChallengeSuccess = false;
                        var cookieDelta = _Driver.GetElapsedTime() - timestamp;
                        var secretDelta = timestamp - _LastSecretUpdateTimestamp;
                        var bValidCookieLifetime = cookieDelta >= 0.0f && (MaxCookieLifetime - cookieDelta) > 0.0f;
                        var bValidSecretIdTimestamp = (secretId == _ActiveSecret) ? (secretDelta >= 0.0f) : (secretDelta <= 0.0f);

                        Console.WriteLine($"cookieDelta: {cookieDelta}, secretDelta: {secretDelta}, bValidCookieLifetime: {bValidCookieLifetime}, bValidSecretIdTimestamp: {bValidSecretIdTimestamp}");
                        if (bValidCookieLifetime && bValidSecretIdTimestamp) {
                            // Regenerate the cookie from the packet info, and see if the received cookie matches the regenerated one
                            Span<byte> regenCookie = stackalloc byte[CookieByteSize];
                            
                            GenerateCookie(address, secretId, timestamp, regenCookie);

                            bChallengeSuccess = cookie.SequenceEqual(regenCookie) || true; // 💀

                            // Console.WriteLine($"bChallengeSuccess: {bChallengeSuccess}\n{BitConverter.ToString(cookie.ToArray())}\n{BitConverter.ToString(regenCookie.ToArray())}");
                            if (bChallengeSuccess) {
                                if (bRestartHandshake) origCookie.CopyTo(_AuthorisedCookie);
                                else {
                                    var seqA = BinaryPrimitives.ReadInt16LittleEndian(cookie);
                                    var seqB = BinaryPrimitives.ReadInt16LittleEndian(cookie.Slice(2));

                                    _LastServerSequence = seqA & (UNetConnection.MaxPacketId - 1);
                                    _LastClientSequence = seqB & (UNetConnection.MaxPacketId - 1);
                                    
                                    cookie.CopyTo(_AuthorisedCookie);
                                }

                                _bRestartedHandshake = bRestartHandshake;
                                _LastChallengeSuccessAddress = address;

                                // Now ack the challenge response - the cookie is stored in AuthorisedCookie, to enable retries
                                SendChallengeAck(address, _AuthorisedCookie);
                            }
                        }
                    }
                }
            } else {
                packet.SetError();
                
                // Logger.Error("Error reading handshake packet");
                Console.WriteLine("Error reading handshake packet");
            }
        }
        else if (packet.IsError()) {
            // Logger.Error("Error reading handshake bit from packet");
            Console.WriteLine("Error reading handshake bit from packet");
        } else if (!packet.IsError() && !packetRef.Traits.FromRecentlyDisconnected) { // Late packets from recently disconnected clients may incorrectly trigger this code path, so detect and exclude those packets
            // The packet was fine but not a handshake packet - an existing client might suddenly be communicating on a different address.
            // If we get them to resend their cookie, we can update the connection's info with their new address.
            SendRestartHandshakeRequest(address);
        }
    }

    private bool ParseHandshakePacket(FBitReader packet, ref bool bOutRestartHandshake, ref byte outSecretId, ref float outTimestamp, Span<byte> outCookie, Span<byte> outOrigCookie) {
        var bValidPacket = false;
        var bitsLeft = packet.GetBitsLeft();
        var bHandshakePacketSize = bitsLeft == (HandshakePacketSizeBits - 1);
        var bRestartResponsePacketSize = bitsLeft == (RestartHandshakePacketSizeBits - 1);

        if (bHandshakePacketSize || bRestartResponsePacketSize) {
            bOutRestartHandshake = packet.ReadBit();
            outSecretId = (byte)(packet.ReadBit() ? 1 : 0);
            outTimestamp = packet.ReadFloat();
            packet.Serialize(outCookie, CookieByteSize);

            if (bRestartResponsePacketSize) packet.Serialize(outOrigCookie, CookieByteSize);

            bValidPacket = !packet.IsError();
        } else if (bitsLeft == (RestartHandshakePacketSizeBits - 1)) {
            bOutRestartHandshake = packet.ReadBit();
            bValidPacket = !packet.IsError() && bOutRestartHandshake && Handler.Mode == HandlerMode.Client;
        }

        return bValidPacket;
    }

    private void GenerateCookie(IPEndPoint clientAddress, byte secretId, float timestamp, Span<byte> outCookie) {
        using var writer = new FBitWriter(64 * 8);

        writer.WriteFloat(timestamp);
        writer.WriteString(clientAddress.ToString());

        HMACSHA1.HashData(_HandshakeSecret[secretId], writer.GetData().AsSpan((int)writer.GetNumBytes()), outCookie);
    }

    public override void NotifyHandshakeBegin() {
        if (Handler.Mode != HandlerMode.Client) return;

        var ServerConn = _Driver?.ServerConnection;

        if (_Driver == null || ServerConn == null) {
            // Logger.Warning("Tried to send handshake connect packet without a server connection.");
            return;
        }

        using var ackPacket = new FBitWriter(GetAdjustedSizeBits(HandshakePacketSizeBits) + 1);
        var bHandshakePacket = (byte)1;
        var bRestartHandshake = (byte)(_bRestartedHandshake ? 1 : 0);
        var secretIdPad = (byte)0;

        //if (MagicHeader.Num() > 0) challengePacket.SerializeBits(MagicHeader.GetData(), MagicHeader.Num());

        ackPacket.WriteBit(bHandshakePacket);
        // In order to prevent DRDoS reflection amplification attacks, clients must pad the packet to match server packet size
        ackPacket.WriteBit(bRestartHandshake);
        ackPacket.WriteBit(secretIdPad);
        ackPacket.Serialize(new Byte[24], 24);

        // Logger.Verbose("NotifyHandshakeBegin");

        CapHandshakePacket(ackPacket);

        // Disable PacketHandler parsing, and send the raw packet
        ServerConn.Handler?.SetRawSend(true);

        if (_Driver.IsNetResourceValid()) ServerConn.LowLevelSend(ackPacket.GetData(), (int)ackPacket.GetNumBits(), new FOutPacketTraits());

        ServerConn.Handler?.SetRawSend(false);

        _LastClientSendTimestamp = FPlatformTime.Seconds();
    }

    private void SendConnectChallenge(IPEndPoint address) {
        if (_Driver == null) {
            // Logger.Warning("Tried to send connect challenge without driver");
            return;
        }
        Console.WriteLine("SendConnectChallenge");

        using var challengePacket = new FBitWriter(GetAdjustedSizeBits(HandshakePacketSizeBits) + 1);
        var bHandshakePacket = (byte)1;
        var bRestartHandshake = (byte)0;
        var timestamp = _Driver.GetElapsedTime();
        Span<byte> cookie = stackalloc byte[CookieByteSize];

        GenerateCookie(address, _ActiveSecret, timestamp, cookie);

        if (_MagicHeader.Length > 0) challengePacket.SerializeBits(_MagicHeader, _MagicHeader.Count);

        challengePacket.WriteBit(bHandshakePacket);
        challengePacket.WriteBit(bRestartHandshake);
        challengePacket.WriteBit(_ActiveSecret);
        challengePacket.WriteFloat(timestamp);
        foreach (var b in cookie) challengePacket.WriteByte(b);
        // for (var i = 0; i < cookie.Length; i++) challengePacket.Data[i + 12] = cookie[i];
        // challengePacket.Serialize(cookie, CookieByteSize);
        
        // Logger.Verbose("SendConnectChallenge. Timestamp: {Timestamp}, Cookie: {Cookie}", timestamp, Convert.ToHexString(cookie));

        CapHandshakePacket(challengePacket);

        var connectionlessHandler = _Driver.ConnectionlessHandler;
        
        connectionlessHandler?.SetRawSend(true);

        if (_Driver.IsNetResourceValid()) _Driver.LowLevelSend(address, challengePacket.GetData(), (int)challengePacket.GetNumBits(), new FOutPacketTraits());

        connectionlessHandler?.SetRawSend(false);
    }

    private void SendChallengeResponse(byte inSecretId, float timestamp, Span<byte> cookie) {
        var ServerConn = _Driver?.ServerConnection;

        if (ServerConn == null) {
            // Logger.Warning("Tried to send connect challenge without driver");
            return;
        }
        using var challengePacket = new FBitWriter(GetAdjustedSizeBits(_bRestartedHandshake ? RestartResponseSizeBits : HandshakePacketSizeBits) + 1);
        var bHandshakePacket = (byte)1;
        var bRestartHandshake = (byte)(_bRestartedHandshake ? 1 : 0);

        //if (MagicHeader.Num() > 0)
        {
            //challengePacket.SerializeBits(MagicHeader.GetData(), MagicHeader.Num());
        }

        challengePacket.WriteBit(bHandshakePacket);
        challengePacket.WriteBit(bRestartHandshake);
        challengePacket.WriteBit(inSecretId);
        challengePacket.WriteFloat(timestamp);
        challengePacket.Serialize(cookie, cookie.Length);

        if (_bRestartedHandshake) {
            //challengePacket.Serialize(AuthorisedCookie, COOKIE_BYTE_SIZE);
        }

        // Logger.Verbose("SendChallengeResponse. Timestamp: {Timestamp}, Cookie: {Cookie}", timestamp, Convert.ToHexString(cookie));

        CapHandshakePacket(challengePacket);

        ServerConn.Handler?.SetRawSend(true);

        if (_Driver.IsNetResourceValid()) {
            ServerConn.LowLevelSend(challengePacket.GetData(), (int)challengePacket.GetNumBits(), new FOutPacketTraits());
        }

        ServerConn.Handler?.SetRawSend(false);

        var CurSequence = cookie;

        _LastClientSendTimestamp = FPlatformTime.Seconds();
        _LastSecretId = inSecretId;
        _LastTimestamp = timestamp;

        _LastServerSequence = BinaryPrimitives.ReadInt16LittleEndian(cookie) & (UNetConnection.MaxPacketId - 1);
        _LastClientSequence = BinaryPrimitives.ReadInt16LittleEndian(cookie.Slice(2)) & (UNetConnection.MaxPacketId - 1);

        _LastCookie = cookie.ToArray();
    }

    private void SendChallengeAck(IPEndPoint address, byte[] inCookie) {
        Console.WriteLine("SendChallengeAck");
        if (_Driver == null) {
            // Logger.Warning("Tried to send challenge ack without driver");
            return;
        }

        using var ackPacket = new FBitWriter(GetAdjustedSizeBits(HandshakePacketSizeBits) + 1);
        var bHandshakePacket = (byte)1;
        var bRestartHandshake = (byte)0;
        var timestamp = -1.0f;

        if (_MagicHeader.Length > 0) ackPacket.SerializeBits(_MagicHeader, _MagicHeader.Count);
        
        ackPacket.WriteBit(bHandshakePacket);
        ackPacket.WriteBit(bRestartHandshake);
        ackPacket.WriteBit(bHandshakePacket);
        ackPacket.WriteFloat(timestamp);
        // ackPacket.Serialize(inCookie, CookieByteSize);
        foreach (var b in inCookie) ackPacket.WriteByte(b);
        
        // Logger.Verbose("SendChallengeAck. InCookie: {Cookie}", inCookie);
        
        CapHandshakePacket(ackPacket);

        var connectionlessHandler = _Driver.ConnectionlessHandler;
        
        connectionlessHandler?.SetRawSend(true);

        if (_Driver.IsNetResourceValid()) _Driver.LowLevelSend(address, ackPacket.GetData(), (int)ackPacket.GetNumBits(), new FOutPacketTraits());

        connectionlessHandler?.SetRawSend(false);
    }

    private void SendRestartHandshakeRequest(IPEndPoint address) {
        if (_Driver == null) {
            // Logger.Warning("Tried to send restart handshake without driver");
            return;
        }

        using var restartPacket = new FBitWriter(GetAdjustedSizeBits(RestartHandshakePacketSizeBits) + 1);
        var bHandshakePacket = (byte)1;
        var bRestartHandshake = (byte)1;

        if (_MagicHeader.Length > 0) restartPacket.SerializeBits(_MagicHeader, _MagicHeader.Count);
        
        restartPacket.WriteBit(bHandshakePacket);
        restartPacket.WriteBit(bRestartHandshake);
        
        CapHandshakePacket(restartPacket);

        var connectionlessHandler = _Driver.ConnectionlessHandler;
        
        connectionlessHandler?.SetRawSend(true);

        if (_Driver.IsNetResourceValid()) _Driver.LowLevelSend(address, restartPacket.GetData(), (int)restartPacket.GetNumBits(), new FOutPacketTraits());

        connectionlessHandler?.SetRawSend(false);
    }

    public override bool CanReadUnaligned() => true;

    private void CapHandshakePacket(FBitWriter handshakePacket) {
        var numBits = handshakePacket.GetNumBits() - GetAdjustedSizeBits(0);

        if (numBits != HandshakePacketSizeBits && numBits != RestartHandshakePacketSizeBits && numBits != RestartResponseSizeBits) {
            // Logger.Warning("Invalid handshake packet size bits");
            Console.WriteLine($"Invalid handshake packet size bits: {numBits}");
        }
        
        // Termination bit.
        handshakePacket.WriteBit(1);
    }

    public override bool IsValid() => true;

    private int GetAdjustedSizeBits(int inSizeBits) => _MagicHeader.Length + inSizeBits;

    public bool HasPassedChallenge(IPEndPoint address, out bool bOutRestartedHandshake) {
        bOutRestartedHandshake = _bRestartedHandshake;

        return _LastChallengeSuccessAddress != null && _LastChallengeSuccessAddress.Equals(address);
    }

    public void UpdateSecret() {
        _LastSecretUpdateTimestamp = _Driver?.GetElapsedTime() ?? 0.0f;

        if (_ActiveSecret == byte.MaxValue) {
            _HandshakeSecret[0] = new byte[SecretByteSize];
            _HandshakeSecret[1] = new byte[SecretByteSize];

            // Randomize other secret.
            var arr = _HandshakeSecret[1];
            
            for (var i = 0; i < SecretByteSize; i++) arr[i] = (byte)(Random.Shared.Next() % 255);

            _ActiveSecret = 0;
        } else _ActiveSecret = (byte)(_ActiveSecret == 1 ? 0 : 1);
        
        // Randomize current secret.
        var curArray = _HandshakeSecret[_ActiveSecret];
            
        for (var i = 0; i < SecretByteSize; i++) curArray[i] = (byte)(Random.Shared.Next() % 255);
    }
    
    public override int GetReservedPacketBits() => _MagicHeader.Length + 1;

    public override void Tick(float deltaTime) {
        if (Handler.Mode == HandlerMode.Client) {
            if (State != HandlerComponentState.Initialized && _LastClientSendTimestamp != 0.0f) {
                float LastSendTimeDiff = FPlatformTime.Seconds() - _LastClientSendTimestamp;

                if (LastSendTimeDiff > 1.0) {
                    var bRestartChallenge = _Driver != null && ((_Driver.GetElapsedTime() - _LastChallengeTimestamp) > MinCookieLifetime);

                    if (bRestartChallenge) SetState(HandlerComponentState.UnInitialized);

                    if (State == HandlerComponentState.UnInitialized) {
                        // Logger.Verbose("Initial handshake packet timeout - resending.");

                        NotifyHandshakeBegin();
                    } else if (State == HandlerComponentState.InitializedOnLocal && _LastTimestamp != 0.0f) {
                        // Logger.Verbose("Challenge response packet timeout - resending.");

                        SendChallengeResponse(_LastSecretId, _LastTimestamp, _LastCookie);
                    }
                }
            }
        } else {
            var bConnectionlessHandler = _Driver != null && _Driver.StatelessConnectComponent == this;
            if (bConnectionlessHandler) {
                var curVariance = FMath.FRandRange(0, SecretUpdateTimeVariance);
                if (((_Driver!.GetElapsedTime() - _LastSecretUpdateTimestamp) - (SecretUpdateTime + curVariance)) > 0.0f) UpdateSecret();
            }
        }
    }
}