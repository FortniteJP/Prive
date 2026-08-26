namespace AFortOnlineBeacon.Net.Channels;

public abstract class UChannel {
    private const int NetMaxConstructedPartialBunchSizeBytes = 1024 * 64;

    public UNetConnection? Connection { get; set; }
    public bool OpenAcked { get; set; }
    public bool Closing { get; set; }
    public bool Dormant { get; set; }
    public bool bIsReplicationPaused { get; set; }
    public bool OpenTemporary { get; set; }
    public bool Broken { get; set; }
    public bool bTornOff { get; set; }
    public bool bPendingDormancy { get; set; }
    public bool bIsInDormancyHysteresis { get; set; }
    public bool bPausedUntilReliableACK { get; set; }
    public bool SentClosingBunch { get; set; }
    public bool bPooled { get; set; }
    public bool OpenedLocally { get; set; }
    public bool bOpenedForCheckpoint { get; set; }
    public int ChIndex { get; set; }
    public FPacketIdRange OpenPacketId { get; set; }
    public EChannelType ChType { get; set; }
    public FName ChName { get; set; }
    public int NumInRec { get; set; }
    public int NumOutRec { get; set; }
    public FInBunch? InRec { get; set; }
    public FOutBunch? OutRec { get; set; }
    public FInBunch? InPartialBunch { get; set; }

    public virtual void Init(UNetConnection inConnection, int inChIndex, EChannelCreateFlags createFlags) {
        Connection = inConnection;
        ChIndex = inChIndex;
        OpenedLocally = (createFlags & EChannelCreateFlags.OpenedLocally) != 0;
        OpenPacketId = new FPacketIdRange();
        bPausedUntilReliableACK = false;
        SentClosingBunch = false;
    }

    public virtual void Tick() {
        // TODO: Dormancy
    }

    public virtual bool CanStopTicking() => !bPendingDormancy;

    public void ReceivedRawBunch(FInBunch bunch, out bool bOutSkipAck) {
        bOutSkipAck = false;

        // Immediately consume the NetGUID portion of this bunch, regardless if it is partial or reliable.
        // NOTE - For replays, we do this even earlier, to try and load this as soon as possible, in case there is an issue creating the channel
        // If a replay fails to create a channel, we want to salvage as much as possible
        if (bunch.bHasPackageMapExports && !Connection!.IsInternalAck()) throw new NotImplementedException();

        if (Connection!.IsInternalAck() && Broken) return;

        if (bunch.bReliable && bunch.ChSequence != Connection.InReliable[ChIndex] + 1) {
            if (Connection.IsInternalAck()) throw new UnrealNetException("Shouldn't hit this path on 100% reliable connections");

            if (bunch.ChSequence <= Connection.InReliable[ChIndex]) throw new UnrealNetException("Invalid bunch");

            // TODO: (InRec) Queue
            throw new NotImplementedException();
        } else {
            var bDeleted = ReceivedNextBunch(bunch, out bOutSkipAck);

            if (bunch.IsError()) {
                // Logger.Error("Bunch.IsError() after ReceivedNextBunch 1");
                return;
            }

            if (bDeleted) return;
            
            // TODO: (InRec) Dispatch waiting bunches
            while (InRec != null) throw new NotImplementedException();
        }
    }

    private bool ReceivedNextBunch(FInBunch bunch, out bool bOutSkipAck) {
        bOutSkipAck = false;
        
        // We received the next bunch. Basically at this point:
        //	-We know this is in order if reliable
        //	-We dont know if this is partial or not
        // If its not a partial bunch, of it completes a partial bunch, we can call ReceivedSequencedBunch to actually handle it

        // Note this bunch's retirement.
        if (bunch.bReliable) {
            // Reliables should be ordered properly at this point
            if (bunch.ChSequence != Connection.InReliable[bunch.ChIndex] + 1) throw new UnrealNetException("Reliables should be ordered properly at this point");

            Connection.InReliable[bunch.ChIndex] = bunch.ChSequence;
        }

        var handleBunch = bunch;
        
        if (bunch.bPartial) {
            handleBunch = null;
            
            if (bunch.bPartialInitial) {
                // Create new InPartialBunch if this is the initial bunch of a new sequence.
                if (InPartialBunch != null) {
                    if (!InPartialBunch.bPartialFinal) {
                        if (InPartialBunch.bReliable) {
                            if (bunch.bReliable) {
                                // Logger.Warning("Reliable partial trying to destroy reliable partial 1");
                                bunch.SetError();
                                return false;
                            }
                            
                            // Logger.Information("Unreliable partial trying to destroy reliable partial 1");
                            bOutSkipAck = true;
                            return false;
                        }
                        
                        // We didn't complete the last partial bunch - this isn't fatal since they can be unreliable, but may want to log it.
                        // Logger.Verbose("Incomplete partial bunch. Channel: {ChIndex} ChSequence: {ChSequence}", InPartialBunch.ChIndex, InPartialBunch.ChSequence);
                    }

                    InPartialBunch = null;
                }
                
                InPartialBunch = new FInBunch(bunch, false);

                if (!bunch.bHasPackageMapExports && bunch.GetBitsLeft() > 0) {
                    if (bunch.GetBitsLeft() % 8 != 0) {
                        // Logger.Warning("Corrupt partial bunch. Initial partial bunches are expected to be byte-aligned. BitsLeft = {BitCount}", bunch.GetBitsLeft());
                        bunch.SetError();
                        return false;
                    }

                    InPartialBunch.AppendDataFromChecked(bunch.GetBufferPosChecked(), bunch.GetBuffer(), bunch.GetBitsLeft());
                    
                    // Log.Verbose("Received new partial bunch");
                } else {
                    // Log.Verbose("Received New partial bunch. It only contained NetGUIDs");
                }
            } else {
                // Merge in next partial bunch to InPartialBunch if:
                //	-We have a valid InPartialBunch
                //	-The current InPartialBunch wasn't already complete
                //  -ChSequence is next in partial sequence
                //	-Reliability flag matches

                var bSequenceMatches = false;
                
                if (InPartialBunch != null) {
                    var bReliableSequencesMatches = bunch.ChSequence == InPartialBunch.ChSequence + 1;
                    var bUnreliableSequenceMatches = bReliableSequencesMatches || bunch.ChSequence == InPartialBunch.ChSequence;
                    
                    // Unreliable partial bunches use the packet sequence, and since we can merge multiple bunches into a single packet,
                    // it's perfectly legal for the ChSequence to match in this case.
                    // Reliable partial bunches must be in consecutive order though
                    bSequenceMatches = InPartialBunch.bReliable ? bReliableSequencesMatches : bUnreliableSequenceMatches;
                }

                if (InPartialBunch != null && !InPartialBunch.bPartialFinal && bSequenceMatches && InPartialBunch.bReliable == bunch.bReliable) {
                    // Merge.
                    // Logger.Verbose("Merging Partial Bunch: {BytesLeft} Bytes", bunch.GetBytesLeft());

                    if (!bunch.bHasPackageMapExports && bunch.GetBitsLeft() > 0) {
                        // TODO: Check if works.
                        InPartialBunch.AppendDataFromChecked(bunch.GetBufferPosChecked(), bunch.GetBuffer(), bunch.GetBitsLeft());
                    }
                    
                    // Only the final partial bunch should ever be non byte aligned. This is enforced during partial bunch creation
                    // This is to ensure fast copies/appending of partial bunches. The final partial bunch may be non byte aligned.
                    if (!bunch.bHasPackageMapExports && !bunch.bPartialFinal && bunch.GetBitsLeft() % 8 != 0) {
                        // Logger.Warning("Corrupt partial bunch. Non-final partial bunches are expected to be byte-aligned. bHasPackageMapExports = {HasPackageMapExports}, bPartialFinal = {PartialFinal}, BitsLeft = {BitsLeft}",
                        //     bunch.bHasPackageMapExports ? 1 : 0,
                        //     bunch.bPartialFinal ? 1 : 0,
                        //     bunch.GetBitsLeft());
                        bunch.SetError();
                        return false;
                    }
                    
                    // Advance the sequence of the current partial bunch so we know what to expect next
                    InPartialBunch.ChSequence = bunch.ChSequence;

                    if (bunch.bPartialFinal) {
                        // Logger.Verbose("Completed Partial Bunch ({BytesLeft} left)", bunch.GetBytesLeft());

                        if (bunch.bHasPackageMapExports) {
                            // Shouldn't have these, they only go in initial partial export bunches
                            // Logger.Warning("Corrupt partial bunch. Final partial bunch has package map exports");
                            bunch.SetError();
                            return false;
                        }

                        handleBunch = InPartialBunch;

                        InPartialBunch.bPartialFinal = true;
                        InPartialBunch.bClose = bunch.bClose;
                        InPartialBunch.bDormant = bunch.bDormant;
                        InPartialBunch.CloseReason = bunch.CloseReason;
                        InPartialBunch.bIsReplicationPaused = bunch.bIsReplicationPaused;
                        InPartialBunch.bHasMustBeMappedGUIDs = bunch.bHasMustBeMappedGUIDs;
                    } else {
                        // Logger.Verbose("Received Partial Bunch");
                    }
                } else {
                    // Merge problem - delete InPartialBunch.
                    // This is mainly so that in the unlikely chance that ChSequence wraps around, we wont merge two completely separate partial bunches.
                    // We shouldn't hit this path on 100% reliable connections

                    if (Connection.IsInternalAck()) throw new UnrealNetException("We shouldn't hit this path on 100% reliable connections");

                    bOutSkipAck = true; // Don't ack the packet, since we didn't process the bunch

                    if (InPartialBunch != null && InPartialBunch.bReliable) {
                        if (bunch.bReliable) {
                            // Logger.Warning("Reliable partial trying to destroy reliable partial 2");
                            bunch.SetError();
                            return false;
                        }
                        
                        // Logger.Warning("Unreliable partial trying to destroy reliable partial 2");
                        return false;
                    }

                    if (InPartialBunch != null) InPartialBunch = null;
                }
            }
            
            // Fairly large number, and probably a bad idea to even have a bunch this size, but want to be safe for now and not throw out legitimate data
            if (IsBunchTooLarge(Connection, InPartialBunch)) {
                // Logger.Error("Received a partial bunch exceeding max allowed size. BunchSize={Size}, MaximumSize={MaxSize}", InPartialBunch!.GetNumBytes(), NetMaxConstructedPartialBunchSizeBytes);
                bunch.SetError();
                return false;
            }
        }

        if (handleBunch != null) {
            var bBothSidesCanOpen = Connection.Driver != null &&
                                    Connection.Driver.ChannelDefinitionMap[ChName].ServerOpen &&
                                    Connection.Driver.ChannelDefinitionMap[ChName].ClientOpen;
                
            if (handleBunch.bOpen) {
                // Voice channels can open from both side simultaneously, so ignore this logic until we resolve this
                if (!bBothSidesCanOpen) {
                    // If we opened the channel, we shouldn't be receiving bOpen commands from the other side
                    if (OpenedLocally) throw new UnrealNetException("Received channel open command for channel that was already opened locally.");

                    if (OpenPacketId.First != UnrealConstants.IndexNone || OpenPacketId.Last != UnrealConstants.IndexNone) {
                        // Logger.Error("This should be the first and only assignment of the packet range (we should only receive one bOpen bunch)");
                        bunch.SetError();
                        return false;
                    }
                }

                // Remember the range.
                // In the case of a non partial, HandleBunch == Bunch
                // In the case of a partial, HandleBunch should == InPartialBunch, and Bunch should be the last bunch.
                OpenPacketId = new FPacketIdRange(handleBunch.PacketId, bunch.PacketId);
                OpenAcked = true;

                // Logger.Verbose("ReceivedNextBunch: Channel now fully open. ChIndex: {ChIndex}, OpenPacketId.First: {First}, OpenPacketId.Last: {Last}", ChIndex, OpenPacketId.First, OpenPacketId.Last);
            }

            // Voice channels can open from both side simultaneously, so ignore this logic until we resolve this
            if (!bBothSidesCanOpen) {
                // Don't process any packets until we've fully opened this channel 
                // (unless we opened it locally, in which case it's safe to process packets)
                if (!OpenedLocally && !OpenAcked) {
                    if (handleBunch.bReliable) {
                        // Logger.Error("ReceivedNextBunch: Reliable bunch before channel was fully open");
                        bunch.SetError();
                        return false;
                    }

                    if (Connection.IsInternalAck()) {
                        // Shouldn't be possible for 100% reliable connections
                        Broken = true;
                        return false;
                    }

                    // Don't ack this packet (since we won't process all of it)
                    bOutSkipAck = true;
                    
                    // Logger.Verbose("ReceivedNextBunch: Skipping bunch since channel isn't fully open. ChIndex: {ChIndex}", ChIndex);
                    return false;
                }

                // At this point, we should have the open packet range
                // This is because if we opened the channel locally, we set it immediately when we sent the first bOpen bunch
                // If we opened it from a remote connection, then we shouldn't be processing any packets until it's fully opened (which is handled above)
                if (OpenPacketId.First == -1) throw new UnrealNetException("Should have open packet range.");

                if (OpenPacketId.Last == -1) throw new UnrealNetException("Should have open packet range.");
            }
            
            // Receive it in sequence.
            return ReceivedSequencedBunch(handleBunch);
        }

        return false;
    }

    private bool ReceivedSequencedBunch(FInBunch bunch) {
        // Handle a regular bunch.
        if (!Closing) ReceivedBunch(bunch);
        
        // We have fully received the bunch, so process it.
        if (bunch.bClose) {
            Dormant = bunch.bDormant || (bunch.CloseReason == EChannelCloseReason.Dormancy);

            if (InRec != null) {
                // Logger.Warning("Close Anomaly {Seq} / {InRecSeq}", bunch.ChSequence, InRec.ChSequence);
            }

            if (ChIndex == 0) {
                // Logger.Debug("UChannel::ReceivedSequencedBunch: Bunch.bClose == true. ChIndex == 0. Calling ConditionalCleanUp");   
            }
            
            ConditionalCleanUp(false, bunch.CloseReason);
            return true;
        }

        return false;
    }

    protected abstract void ReceivedBunch(FInBunch bunch);

    /// <summary>
    ///     UChannel::AppendMustBeMappedGuids (DataChannel.cpp:796). Rewrites the bunch with the list
    ///     of GUIDs the client may still need to load in front of it:
    ///
    ///         [uint16 count][packed guid] x count][the original bunch bits]
    ///
    ///     plus the bHasMustBeMappedGUIDs header bit, which is what tells the client's
    ///     UActorChannel::ReceivedBunch to read that prefix at all. Having read it, the client holds
    ///     every later bunch on this channel until those GUIDs resolve, instead of failing to
    ///     resolve a reference mid-payload.
    ///
    ///     No queueing counterpart to UActorChannel::QueuedMustBeMappedGuidsInLastBunch is needed
    ///     here: real UE accumulates RPCs into a RemoteFunctions bunch that is flushed much later,
    ///     so it has to carry the GUIDs along separately, whereas UActorChannel.SendRpc in this
    ///     project builds a bunch and sends it immediately - the package map's list is still intact
    ///     when we get here.
    /// </summary>
    protected virtual void AppendMustBeMappedGuids(FOutBunch bunch) {
        var mustBeMappedGuids = ((UPackageMapClient) Connection!.PackageMap!).GetMustBeMappedGuidsInLastBunch();

        if (mustBeMappedGuids.Count == 0) return;

        // Rewrite the bunch with the unique guids in front.
        using var tempBunch = new FOutBunch(bunch);

        bunch.Reset();

        bunch.WriteUInt16((ushort) mustBeMappedGuids.Count);
        foreach (var netGuid in mustBeMappedGuids) netGuid.NetSerialize(bunch);

        bunch.SerializeBits(tempBunch.GetData(), tempBunch.GetNumBits());

        bunch.bHasMustBeMappedGUIDs = true;

        Console.WriteLine($"UChannel.AppendMustBeMappedGuids: ChIndex={ChIndex} count={mustBeMappedGuids.Count} " +
                          $"guids=[{string.Join(",", mustBeMappedGuids)}]");

        mustBeMappedGuids.Clear();
    }

    public virtual FPacketIdRange SendBunch(FOutBunch bunch, bool merge) {
        if (Connection == null || Connection.Driver == null) throw new UnrealNetException();
        
        if (ChIndex == -1) return new FPacketIdRange(UnrealConstants.IndexNone);

        if (IsBunchTooLarge(Connection!, bunch)) {
            // Logger.Error("Attempted to send bunch exceeding max allowed size. BunchSize={Size}, MaximumSize={MaxSize}", bunch.GetNumBytes(), NetMaxConstructedPartialBunchSizeBytes);
            bunch.SetError();
            return new FPacketIdRange(UnrealConstants.IndexNone);
        }

        // Was a bare `throw new UnrealNetException()`, which said nothing about which of four very
        // different problems had occurred. bunch.IsError() in particular is usually NOT a
        // serialization fault: FOutBunch's constructor marks a bunch overflowed when
        // NumOutRec >= ReliableBuffer - 1, so a jammed reliable queue surfaces here as an opaque
        // throw from the send path. NumOutRec only drains from the head of OutRec, so one bunch that
        // never gets acked blocks every later one.
        if (Closing || Connection.Channels[ChIndex] != this || bunch.IsError() || bunch.bHasPackageMapExports) {
            throw new UnrealNetException(
                $"UChannel.SendBunch refused to send on ChIndex={ChIndex}: Closing={Closing}, " +
                $"ChannelMismatch={Connection.Channels[ChIndex] != this}, BunchIsError={bunch.IsError()}, " +
                $"bHasPackageMapExports={bunch.bHasPackageMapExports}, NumOutRec={NumOutRec}/{UNetConnection.ReliableBuffer}, " +
                $"OldestUnackedSeq={OutRec?.ChSequence.ToString() ?? "none"}");
        }

        // Set bunch flags.
        var bDormancyClose = bunch.bClose && (bunch.CloseReason == EChannelCloseReason.Dormancy);

        if (OpenedLocally && ((OpenPacketId.First == UnrealConstants.IndexNone) || ((Connection.ResendAllDataState != EResendAllDataState.None) && !bDormancyClose))) {
            var bOpenBunch = true;

            if (Connection.ResendAllDataState == EResendAllDataState.SinceCheckpoint) {
                bOpenBunch = !bOpenedForCheckpoint;
                bOpenedForCheckpoint = true;
            }

            if (bOpenBunch) {
                bunch.bOpen = true;
                OpenTemporary = !bunch.bReliable;
            }
        }

        if (OpenTemporary && bunch.bReliable) throw new UnrealNetException("Channel was opened temporarily, we are never allowed to send reliable packets on it");

        // This is the max number of bits we can have in a single bunch
        var MAX_SINGLE_BUNCH_SIZE_BITS = Connection.GetMaxSingleBunchSizeBits();

        // Max bytes we'll put in a partial bunch
        var MAX_SINGLE_BUNCH_SIZE_BYTES = MAX_SINGLE_BUNCH_SIZE_BITS / 8;

        // Max bits will put in a partial bunch (byte aligned, we dont want to deal with partial bytes in the partial bunches)
        var MAX_PARTIAL_BUNCH_SIZE_BITS = MAX_SINGLE_BUNCH_SIZE_BYTES * 8;

        var outgoingBunches = new List<FOutBunch>();

        // Add any export bunches
        // Replay connections will manage export bunches separately.
        if (!Connection.IsInternalAck()) {
            ((UPackageMapClient) Connection.PackageMap!).AppendExportBunches(outgoingBunches);
        }

        if (NetDebugLog.VerboseEnabled) Console.WriteLine($"SendBunch: ChIndex={ChIndex} outgoingBunches(export)Count={outgoingBunches.Count}" + (outgoingBunches.Count > 0 ? " hex=" + string.Join(",", outgoingBunches.Select(b => Convert.ToHexString(b.GetData(), 0, (int) b.GetNumBytes()))) : ""));

        if (outgoingBunches.Count != 0) {
            // Don't merge if we are exporting guid's
            // We can't be for sure if the last bunch has exported guids as well, so this just simplifies things
            merge = false;
        }

        if (Connection.Driver.IsServer()) {
            // Append any "must be mapped" guids to front of bunch from the packagemap
            AppendMustBeMappedGuids(bunch);

            if (bunch.bHasMustBeMappedGUIDs) {
                merge = false;
            }
        }

        //-----------------------------------------------------
        // Contemplate merging.
        //-----------------------------------------------------
        
        // TODO: Merge
        // var preExistingBits = 0;
        FOutBunch? outBunch = null;
        
        // if (merge
        //     && Connection.LastOut.ChIndex == bunch.ChIndex
        //     && Connection.LastOut.bReliable == bunch.bReliable
        //     && Connection.AllowMerge
        //     && Connection.LastEnd.GetNumBits() != 0
        //     && Connection.LastEnd.GetNumBits() == Connection.SendBuffer.GetNumBits()
        //     && Connection.LastOut.GetNumBits() + bunch.GetNumBits() <= MAX_SINGLE_BUNCH_SIZE_BITS)
        // {
        //     
        // }

        //-----------------------------------------------------
        // Possibly split large bunch into list of smaller partial bunches
        //-----------------------------------------------------
        if (bunch.GetNumBits() > MAX_SINGLE_BUNCH_SIZE_BITS) {
            var data = bunch.GetData().AsSpan();
            var bitsLeft = bunch.GetNumBits();
            
            merge = false;

            while (bitsLeft > 0) {
                var partialBunch = new FOutBunch(this, false);
                var bitsThisBunch = (long) Math.Min(bitsLeft, MAX_PARTIAL_BUNCH_SIZE_BITS);
                
                partialBunch.SerializeBits(data, bitsThisBunch);
                
                outgoingBunches.Add(partialBunch);

                bitsLeft -= bitsThisBunch;
                data = data.Slice((int)(bitsThisBunch >> 3));
                
                // Logger.Debug("Making partial bunch from content bunch. bitsThisBunch: {Bits} bitsLeft: {Left}", bitsThisBunch, bitsLeft);
            }
        } else outgoingBunches.Add(bunch);

        //-----------------------------------------------------
        // Send all the bunches we need to
        //	Note: this is done all at once. We could queue this up somewhere else before sending to Out.
        //-----------------------------------------------------
        var packetIdRange = new FPacketIdRange();
        var bOverflowsReliable = (NumOutRec + outgoingBunches.Count >= UNetConnection.ReliableBuffer + (bunch.bClose ? 1 : 0));

        if (bunch.bReliable && bOverflowsReliable) {
            // Real UE closes the connection here too, but this used to also throw, which turned a
            // diagnosable condition into an opaque crash. It is worth logging loudly: overflowing
            // this buffer almost always means the server is answering some client request in an
            // unthrottled loop (see UActorChannel.SafeRetryClientRestart, which did exactly that),
            // and the symptom - the connection dying a few seconds in - looks nothing like the cause.
            Console.WriteLine($"UChannel.SendBunch: RELIABLE BUFFER OVERFLOW on ChIndex={ChIndex} " +
                $"(NumOutRec={NumOutRec} + {outgoingBunches.Count} >= {UNetConnection.ReliableBuffer}). " +
                "Closing the connection - something is queueing reliable bunches faster than they can be acked.");

            // TODO: Send NMT_Failure
            // TODO: FlushNet(true);
            Connection.Close();
            return packetIdRange;
        }

        if (outgoingBunches.Count > 1) {
            // Logger.Debug("Sending {Count} bunches. Channel: {ChIndex} {Channel}", outgoingBunches.Count, bunch.ChIndex, this);
        }

        for (var partialNum = 0; partialNum < outgoingBunches.Count; partialNum++) {
            var nextBunch = outgoingBunches[partialNum];

            nextBunch.bReliable = bunch.bReliable;
            nextBunch.bOpen = bunch.bOpen;
            nextBunch.bClose = bunch.bClose;
            nextBunch.bDormant = bunch.bDormant;
            nextBunch.CloseReason = bunch.CloseReason;
            nextBunch.bIsReplicationPaused = bunch.bIsReplicationPaused;
            nextBunch.ChIndex = bunch.ChIndex;
            nextBunch.ChType = bunch.ChType;
            nextBunch.ChName = bunch.ChName;

            if (!nextBunch.bHasPackageMapExports) nextBunch.bHasMustBeMappedGUIDs |= bunch.bHasMustBeMappedGUIDs;

            if (outgoingBunches.Count > 1) {
                nextBunch.bPartial = true;
                nextBunch.bPartialInitial = partialNum == 0;
                nextBunch.bPartialFinal = partialNum == outgoingBunches.Count - 1;
                nextBunch.bOpen &= partialNum == 0;                                             // Only the first bunch should have the bOpen bit set
                nextBunch.bClose = (bunch.bClose && (outgoingBunches.Count - 1 == partialNum)); // Only last bunch should have bClose bit set
            }

            var thisOutBunch = PrepBunch(nextBunch, outBunch, merge);

            // Update Packet Range
            var packetId = SendRawBunch(thisOutBunch, merge);
            if (partialNum == 0) packetIdRange = new FPacketIdRange(packetId);
            else packetIdRange = new FPacketIdRange(packetIdRange.First, packetId);

            // Update channel sequence count.
            Connection.LastOut = thisOutBunch;
            Connection.LastEnd = new FBitWriterMark(Connection.SendBuffer);
        }
        
        // Update open range if necessary
        if (bunch.bOpen && (Connection.ResendAllDataState == EResendAllDataState.None)) OpenPacketId = packetIdRange;

        // Destroy outgoing bunches now that they are sent, except the one that was passed into ::SendBunch
        //	This is because the one passed in ::SendBunch is the responsibility of the caller, the other bunches in OutgoingBunches
        //	were either allocated in this function for partial bunches, or taken from the package map, which expects us to destroy them.
        foreach (var deleteBunch in outgoingBunches) {
            if (deleteBunch != bunch) deleteBunch.Dispose();
        }

        return packetIdRange;
    }

    private int SendRawBunch(FOutBunch outBunch, bool merge) {
        // Send the raw bunch.
        outBunch.ReceivedAck = false;
        
        var packetId = Connection!.SendRawBunch(outBunch, merge);
        
        if (OpenPacketId.First == UnrealConstants.IndexNone && OpenedLocally) OpenPacketId = new FPacketIdRange(packetId);

        if (outBunch.bClose) SetClosingFlag();
        
        return packetId;
    }

    // NOTE: `outBunch` is intentionally passed by value, not by ref. In real UE, UChannel::PrepBunch
    // takes OutBunch as a plain pointer that the caller's SendBunch loop never writes back to between
    // iterations - it only ever holds Connection->LastOutBunch from a *merge* with a previous SendBunch
    // call (merging isn't implemented here, so it's always null). Passing it by ref here previously
    // made every bunch after the first in a single SendBunch call (e.g. a GUID-export bunch followed by
    // the actor's content bunch) skip ChSequence assignment entirely, leaving it at the default 0 -
    // the client then rejected that bunch as "outdated" since the channel's sequence had already moved
    // past 0 from the first bunch, silently dropping the actor's real spawn data.
    private FOutBunch PrepBunch(FOutBunch bunch, FOutBunch? outBunch, bool merge) {
        if (Connection!.ResendAllDataState != EResendAllDataState.None) return bunch;

        // Find outgoing bunch index.
        if (bunch.bReliable) {
            // Find spot, which was guaranteed available by FOutBunch constructor.
            if (outBunch == null) {
                if (!(NumOutRec < UNetConnection.ReliableBuffer - 1 + (bunch.bClose ? 1 : 0))) {
                    // Logger.Warning("PrepBunch: Reliable buffer overflow! {Channel}", this);
                }

                bunch.Next = null;
                bunch.ChSequence = ++Connection.OutReliable[ChIndex];
                NumOutRec++;
                outBunch = new FOutBunch(bunch);

                // Append to the TAIL of OutRec. Real UE walks a pointer-to-pointer
                // (FOutBunch** OutLink = &OutRec; while (*OutLink) OutLink = &(*OutLink)->Next;)
                // so the final assignment lands on either OutRec itself or the last node's Next.
                // The C# translation of that walked a plain reference to null and then always
                // assigned OutRec, which replaced the head of the list on every send: each previously
                // queued bunch was orphaned (never released, never disposed) while NumOutRec kept
                // counting them, so the queue drifted upward until it tripped RELIABLE_BUFFER.
                if (OutRec == null) {
                    OutRec = outBunch;
                } else {
                    var outLink = OutRec;
                    while (outLink.Next != null) outLink = outLink.Next;
                    outLink.Next = outBunch;
                }
            } else {
                bunch.Next = outBunch.Next;
                outBunch = bunch;
            }

            Connection.LastOutBunch = outBunch;
        } else {
            outBunch = bunch;
            Connection.LastOutBunch = null;
        }

        return outBunch;
    }

    /// <summary>
    ///     UChannel::Close (DataChannel.cpp). An empty RELIABLE bunch whose only content is the
    ///     bClose flag and a reason. Reliable because a lost close leaves the actor on the client
    ///     forever, with nothing to retry it.
    ///
    ///     The local side is NOT cleaned up here: real UE waits for the close to be acked, and
    ///     ReceivedAcks' bCleanup tail calls ConditionalCleanUp. Tearing the channel down early
    ///     would drop the very bunch that has to be acked.
    /// </summary>
    public void Close(EChannelCloseReason reason) {
        if (Connection == null || Closing || Connection.Channels[ChIndex] != this) return;

        using var closeBunch = new FOutBunch(this, true) {
            bReliable = true,
            CloseReason = reason
        };

        if (closeBunch.IsError()) return;

        Console.WriteLine($"UChannel.Close: ChIndex={ChIndex} reason={reason}");

        SendBunch(closeBunch, false);
    }

    private void SetClosingFlag() => Closing = true;

    public bool IsPendingKill { get; private set; }

    public void ConditionalCleanUp(bool bForDestroy, EChannelCloseReason closeReason) {
        if (IsPendingKill) return;

        if (Connection!.Channels[ChIndex] == this) {
            IsPendingKill = true;
            Connection.Channels[ChIndex] = null;
            Connection.OpenChannels.Remove(this);

            CleanUp(bForDestroy, closeReason);
        }
    }

    protected virtual void CleanUp(bool bForDestroy, EChannelCloseReason closeReason) {}

    private static bool IsBunchTooLarge(UNetConnection connection, FInBunch? bunch) => !connection.IsInternalAck() && bunch != null && bunch.GetNumBytes() > NetMaxConstructedPartialBunchSizeBytes;

    private static bool IsBunchTooLarge(UNetConnection connection, FOutBunch? bunch) => !connection.IsInternalAck() && bunch != null && bunch.GetNumBytes() > NetMaxConstructedPartialBunchSizeBytes;

    /// <summary>
    ///     Port of UChannel::ReceivedNak - resend every reliable bunch that was in the lost packet and
    ///     hasn't since been acked. Until FWrittenChannelsRecord existed there was nothing to drive
    ///     this from, so a dropped packet simply lost its reliable bunches forever.
    /// </summary>
    public virtual void ReceivedNak(int nakPacketId) {
        for (var outBunch = OutRec; outBunch != null; outBunch = outBunch.Next) {
            if (outBunch.PacketId != nakPacketId || outBunch.ReceivedAck) continue;

            if (NetDebugLog.VerboseEnabled) Console.WriteLine($"UChannel.ReceivedNak: ChIndex={ChIndex} resending ChSequence={outBunch.ChSequence}");

            Connection!.SendRawBunch(outBunch, false);
        }
    }

    /// <summary>
    ///     Set the first time this channel actually releases a reliable bunch. Logged once, unguarded,
    ///     because "did the reliable queue ever drain at all" is the single fact that separates a
    ///     healthy connection from the failure mode this used to have, and it is otherwise invisible
    ///     until the connection dies several seconds later for an apparently unrelated reason.
    /// </summary>
    private bool _LoggedFirstReliableRelease;

    public void ReceivedAcks() {
        bool bCleanup = false;
        EChannelCloseReason closeReason = EChannelCloseReason.Destroyed;

        while (OutRec is not null && OutRec.ReceivedAck) {
            if (OutRec.bOpen) {
                bool openFinished = true;
                if (OutRec.bPartial) {
                    var openBunch = OutRec;
                    while (openBunch is not null) {
                        if (NetDebugLog.VerboseEnabled) Console.WriteLine($"Channel {ChIndex} open partials {openBunch.PacketId} ackd {openBunch.ReceivedAck} final {openBunch.bPartialFinal}");
                        if (!openBunch.ReceivedAck) {
                            openFinished = false;
                            break;
                        }
                        if (openBunch.bPartialFinal) break;
                        openBunch = openBunch.Next;
                    }
                }
                if (openFinished) {
                    if (NetDebugLog.VerboseEnabled) Console.WriteLine($"Channel {ChIndex} is fully ackd. PacketID: {OutRec.PacketId}");
                    OpenAcked = true;
                } else {
                    // Head-of-line block: nothing behind this bunch can be released either. Worth
                    // shouting about once the backlog gets close to the limit, because the eventual
                    // symptom is an unexplained send failure rather than anything mentioning acks.
                    if (NumOutRec > UNetConnection.ReliableBuffer / 2) {
                        Console.WriteLine($"UChannel.ReceivedAcks: ChIndex={ChIndex} reliable queue is not draining - " +
                            $"the channel-open bunch (Seq {OutRec.ChSequence}, PacketId {OutRec.PacketId}) is still unacked " +
                            $"while NumOutRec={NumOutRec}/{UNetConnection.ReliableBuffer}.");
                    }
                    break;
                }
            }

            bCleanup = bCleanup || OutRec.bClose;

            if (OutRec.bClose) closeReason = OutRec.CloseReason;

            var release = OutRec;
            OutRec = OutRec.Next;
            release.Dispose();
            NumOutRec--;

            if (!_LoggedFirstReliableRelease) {
                _LoggedFirstReliableRelease = true;
                Console.WriteLine($"UChannel.ReceivedAcks: ChIndex={ChIndex} released its first acked reliable bunch " +
                    $"(Seq {release.ChSequence}, PacketId {release.PacketId}); NumOutRec now {NumOutRec}.");
            }
        }

        // If a close has been acknowledged in sequence, we're done. This was previously unreachable
        // in practice - bCleanup was computed and then dropped - because no bunch was ever acked at
        // all; now that acks land, a channel we closed actually gets torn down.
        if (bCleanup || (OpenTemporary && OpenAcked)) {
            if (NetDebugLog.VerboseEnabled) Console.WriteLine($"UChannel.ReceivedAcks: cleaning up after close acked. ChIndex={ChIndex} CloseReason={closeReason}");

            ConditionalCleanUp(false, closeReason);
        }
    }
}