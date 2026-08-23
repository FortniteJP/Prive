using System.Runtime.InteropServices;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     Port of UE 4.23's OodleHandlerComponent. Fortnite's PacketHandler chain has this registered with
///     EOodleEnableMode.AlwaysEnabled on both ends, so every packet (compressed or not) carries a leading
///     "is this payload Oodle-compressed" bit that must be stripped/written regardless of whether we can
///     actually compress/decompress. Uses the real FortniteGameInput.udic/FortniteGameOutput.udic dictionaries
///     (extracted from the client's own Content/Oodle/*.udic, same file format as UE's FOodleDictionaryArchive)
///     so that we can actually decode packets the real client compressed, and compress our own replies the way
///     the real client expects.
/// </summary>
public class OodleHandlerComponent : HandlerComponent {
    private const string OodleDll = "oo2core_5_win64";

    private const int MaxOodlePacketBytes = UNetConnection.MaxPacketSize; // MAX_OODLE_PACKET_BYTES = MAX_PACKET_SIZE

    [DllImport(OodleDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint OodleNetwork1UDP_State_Size();

    [DllImport(OodleDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint OodleNetwork1_Shared_Size(int htBits);

    [DllImport(OodleDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void OodleNetwork1_Shared_SetWindow(nint data, int htBits, nint window, int windowSize);

    [DllImport(OodleDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void OodleNetwork1UDP_State_Uncompact(nint state, nint compactState);

    [DllImport(OodleDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern long OodleNetwork1UDP_Encode(nint state, nint shared, nint rawData, nint rawLen, nint compBuf);

    [DllImport(OodleDll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool OodleNetwork1UDP_Decode(nint state, nint shared, nint compBuf, nuint compBufSize, nint rawBuf, nuint rawLen);

    // General-purpose LZ (de)compression, used only to unpack the .udic dictionary file's two compressed
    // sections (the raw dictionary bytes, and the compacted network compressor state) - NOT used for packets.
    // NOTE: fuzzSafe MUST be 0 - this particular (old, ~2019) SDK build returns a hard failure for every input
    // when called with fuzzSafe=1 (OodleLZ_FuzzSafe_Yes), confirmed empirically against known-good dictionary data.
    [DllImport(OodleDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern long OodleLZ_Decompress(nint compBuf, nint compBufSize, nint rawBuf, nint rawLen,
        int fuzzSafe, int checkCRC, int verbosity, nint decBufBase, nint decBufSize, nint fpCallback, nint callbackUserData,
        nint decoderMemory, nint decoderMemorySize, int threadPhase);

    private const int HashTableBits = 17; // Matches the HashTableSize field stored in both .udic files.

    private sealed record OodleDictionary(nint State, nint Shared);

    private static readonly OodleDictionary? ServerDictionary; // Used to compress our own outgoing packets.
    private static readonly OodleDictionary? ClientDictionary; // Used to decompress the real client's packets.

    static OodleHandlerComponent() {
        try {
            var contentDir = Path.Combine(AppContext.BaseDirectory, "Content", "Oodle");

            ServerDictionary = LoadDictionary(Path.Combine(contentDir, "FortniteGameOutput.udic"));
            ClientDictionary = LoadDictionary(Path.Combine(contentDir, "FortniteGameInput.udic"));
        } catch (Exception ex) {
            Console.WriteLine($"OodleHandlerComponent: failed to load Oodle dictionaries, compression will be disabled ({ex.Message})");
        }
    }

    private static unsafe OodleDictionary LoadDictionary(string udicPath) {
        var data = File.ReadAllBytes(udicPath);
        var pos = 0;

        uint ReadU32() {
            var v = BitConverter.ToUInt32(data, pos);
            pos += 4;
            return v;
        }

        int ReadI32() {
            var v = BitConverter.ToInt32(data, pos);
            pos += 4;
            return v;
        }

        var magic = ReadU32();
        if (magic != 0x1B1BACD4) throw new InvalidDataException($"Bad Oodle dictionary magic in {udicPath}: 0x{magic:X8}");

        ReadU32(); // DictionaryVersion
        ReadU32(); // OodleMajorHeaderVersion
        var hashTableSize = ReadI32();

        (uint offset, uint compLen, uint decompLen) ReadCompressedDataInfo() {
            var offset = ReadU32();
            var compLen = ReadU32();
            var decompLen = ReadU32();
            return (offset, compLen, decompLen);
        }

        var dictInfo = ReadCompressedDataInfo();
        var stateInfo = ReadCompressedDataInfo();

        byte[] DecompressSection((uint offset, uint compLen, uint decompLen) info) {
            var compressed = new byte[info.compLen];
            Array.Copy(data, info.offset, compressed, 0, info.compLen);
            var raw = new byte[info.decompLen];

            long result;
            fixed (byte* pComp = compressed)
            fixed (byte* pRaw = raw) {
                result = OodleLZ_Decompress((nint)pComp, compressed.Length, (nint)pRaw, raw.Length, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            }

            if (result != info.decompLen) throw new InvalidDataException($"OodleLZ_Decompress failed for {udicPath}: got {result}, expected {info.decompLen}");

            return raw;
        }

        var dictionaryBytes = DecompressSection(dictInfo);
        var compactState = DecompressSection(stateInfo);

        var stateSize = (int)OodleNetwork1UDP_State_Size();
        var sharedSize = (int)OodleNetwork1_Shared_Size(hashTableSize);

        var state = Marshal.AllocHGlobal(stateSize);
        var shared = Marshal.AllocHGlobal(sharedSize);
        var dictionaryPtr = Marshal.AllocHGlobal(dictionaryBytes.Length);
        Marshal.Copy(dictionaryBytes, 0, dictionaryPtr, dictionaryBytes.Length);

        OodleNetwork1_Shared_SetWindow(shared, hashTableSize, dictionaryPtr, dictionaryBytes.Length);

        fixed (byte* pCompactState = compactState) {
            OodleNetwork1UDP_State_Uncompact(state, (nint)pCompactState);
        }

        Console.WriteLine($"OodleHandlerComponent: loaded {Path.GetFileName(udicPath)} (dictionary={dictionaryBytes.Length} bytes, hashTableBits={hashTableSize})");

        return new OodleDictionary(state, shared);
    }

    public OodleHandlerComponent(PacketHandler handler) : base(handler, nameof(OodleHandlerComponent)) {}

    public override void Initialize() {
        SetActive(true);
        SetState(HandlerComponentState.Initialized);
        Initialized();
    }

    public override bool IsValid() => true;

    public override bool CanReadUnaligned() => false;

    public override unsafe void Incoming(FBitReader packet) {
        if (!IsValid() || packet.GetNumBytes() <= 0) return;

        var bCompressedPacket = packet.ReadBit();

        if (!bCompressedPacket) return;

        uint decompressedLength = 0;
        packet.SerializeInt(&decompressedLength, (uint)MaxOodlePacketBytes);
        decompressedLength++;

        if (packet.IsError() || decompressedLength >= MaxOodlePacketBytes) {
            packet.SetError();
            Console.WriteLine($"OodleHandlerComponent.Incoming: invalid decompressed length {decompressedLength}");
            return;
        }

        if (ClientDictionary == null) {
            packet.SetError();
            Console.WriteLine("OodleHandlerComponent.Incoming: received compressed packet but no client dictionary is loaded");
            return;
        }

        var compressedLength = packet.GetBytesLeft();
        var compressedData = new byte[compressedLength];
        packet.SerializeBits(compressedData, packet.GetBitsLeft());

        if (packet.IsError()) {
            packet.SetError();
            return;
        }

        var decompressedData = new byte[decompressedLength];
        bool success;

        fixed (byte* pCompressed = compressedData)
        fixed (byte* pDecompressed = decompressedData) {
            success = OodleNetwork1UDP_Decode(ClientDictionary.State, ClientDictionary.Shared, (nint)pCompressed, (nuint)compressedLength, (nint)pDecompressed, (nuint)decompressedLength);
        }

        if (!success) {
            packet.SetError();
            Console.WriteLine("OodleHandlerComponent.Incoming: OodleNetwork1UDP_Decode failed");
            return;
        }

        packet.SetData(decompressedData, decompressedLength * 8);
    }

    public override unsafe void Outgoing(ref FBitWriter packet, FOutPacketTraits traits) {
        if (!IsValid() || packet.GetNumBytes() <= 0) return;

        var uncompressedData = packet.GetData();
        var uncompressedBytes = (int)packet.GetNumBytes();
        var uncompressedBits = packet.GetNumBits();

        var newPacket = new FBitWriter(uncompressedBits + GetReservedPacketBits(), true);

        const bool bDebugDisableOutgoingCompression = true; // TEMP: isolate whether the bunch overflow is Oodle-related.

        if (ServerDictionary != null && !bDebugDisableOutgoingCompression) {
            var compBuf = new byte[uncompressedBytes * 2 + 1024];
            long encodedLength;

            fixed (byte* pRaw = uncompressedData)
            fixed (byte* pComp = compBuf) {
                encodedLength = OodleNetwork1UDP_Encode(ServerDictionary.State, ServerDictionary.Shared, (nint)pRaw, uncompressedBytes, (nint)pComp);
            }

            if (encodedLength > 0 && encodedLength < uncompressedBytes) {
                newPacket.WriteBit(true);
                // SerializeOodlePacketSize assumes PacketSize is never 0, so it stores (PacketSize - 1) and the
                // reader adds 1 back - mirror that here, matching OodleHandlerComponent.Incoming's decompressedLength++.
                uint decompressedLengthField = (uint)(uncompressedBytes - 1);
                newPacket.SerializeInt(&decompressedLengthField, (uint)MaxOodlePacketBytes);
                newPacket.Serialize(compBuf, (int)encodedLength);

                packet = newPacket;
                return;
            }
        }

        newPacket.WriteBit(false);
        newPacket.SerializeBits(uncompressedData, uncompressedBits);

        packet = newPacket;
    }

    public override int GetReservedPacketBits() {
        var lengthBits = (int)Math.Ceiling(Math.Log2(MaxOodlePacketBytes));

        return 1 + lengthBits;
    }
}
