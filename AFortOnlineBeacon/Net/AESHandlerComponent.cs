using System.Security.Cryptography;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     Port of UE 4.23's FAESHandlerComponent (AES-256-ECB). Fortnite's PacketHandler chain always
///     includes this component, even when encryption itself is disabled: every packet still carries
///     the leading "is this payload encrypted" bit, so it must be registered just to keep framing
///     aligned with the real client, regardless of whether SetEncryptionKey/EnableEncryption are ever called.
/// </summary>
public class AESHandlerComponent : HandlerComponent {
    private const int KeySizeInBytes = 32;
    private const int BlockSizeInBytes = 16;

    private byte[]? _Key;
    private bool _bEncryptionEnabled;

    public AESHandlerComponent(PacketHandler handler) : base(handler, nameof(AESHandlerComponent)) {}

    public void SetEncryptionKey(byte[] newKey) {
        if (newKey.Length != KeySizeInBytes) return;

        _Key = newKey;
    }

    public void EnableEncryption() => _bEncryptionEnabled = true;

    public void DisableEncryption() => _bEncryptionEnabled = false;

    public bool IsEncryptionEnabled() => _bEncryptionEnabled;

    public override void Initialize() {
        SetActive(true);
        SetState(HandlerComponentState.Initialized);
        Initialized();
    }

    public override bool IsValid() => true;

    public override void Incoming(FBitReader packet) {
        if (!IsValid() || packet.GetNumBytes() <= 0) return;

        if (packet.ReadBit()) {
            if (_Key == null) {
                // Packet arrived encrypted before we have a key - could just be out-of-order, not an error.
                packet.SetData(Array.Empty<byte>(), 0);
                return;
            }

            var ciphertext = new byte[packet.GetBytesLeft()];
            packet.SerializeBits(ciphertext, packet.GetBitsLeft());

            byte[] plaintext;
            using (var aes = Aes.Create()) {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _Key;

                using var decryptor = aes.CreateDecryptor();
                plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            }

            if (plaintext.Length == 0) {
                packet.SetData(Array.Empty<byte>(), 0);
                return;
            }

            // Find the termination bit written in Outgoing, to recover the exact plaintext bit size.
            var lastByte = plaintext[^1];
            if (lastByte == 0) {
                packet.SetError();
                return;
            }

            var bitSize = plaintext.Length * 8 - 1;
            while ((lastByte & 0x80) == 0) {
                lastByte *= 2;
                bitSize--;
            }

            packet.SetData(plaintext, bitSize);
        }
    }

    public override void Outgoing(ref FBitWriter packet, FOutPacketTraits traits) {
        if (!IsValid() || packet.GetNumBytes() <= 0) return;

        var newPacket = new FBitWriter(packet.GetNumBits() + 2, true);
        newPacket.WriteBit(_bEncryptionEnabled);

        if (_bEncryptionEnabled) {
            if (_Key == null) throw new UnrealNetException("AESHandlerComponent.Outgoing: encryption enabled without a key");

            // Termination bit, so the receiving side can recover the exact bit size after decrypting.
            packet.WriteBit(1);

            byte[] ciphertext;
            using (var aes = Aes.Create()) {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _Key;

                using var encryptor = aes.CreateEncryptor();
                var plaintext = packet.GetData().AsSpan(0, (int)packet.GetNumBytes()).ToArray();
                ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
            }

            newPacket.Serialize(ciphertext, ciphertext.Length);
        } else newPacket.SerializeBits(packet.GetData(), packet.GetNumBits());

        packet = newPacket;
    }

    public override int GetReservedPacketBits() => 2 + 7 + BlockSizeInBytes * 8;
}
