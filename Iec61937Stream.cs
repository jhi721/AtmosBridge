using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AtmosBridge;

/// <summary>
/// Unwraps IEC 61937 bursts (as written by mpv's spdif passthrough, 16-bit LE words) into the raw bitstream:
/// E-AC-3 for Cavern, TrueHD (MAT frames) for thd-objects, DTS (core, DTS-HD, DTS:X) for dtsx-objects.
/// </summary>
sealed class Iec61937Stream(Stream input) : Stream {
    const int typeDts1 = 11, typeDts3 = 13, typeDtsHd = 17, typeEac3 = 21, typeTrueHd = 22;
    const int matMiddle = 30708; // MAT frame: 20-byte start code, 12-byte middle code here, 16-byte end code

    int auLeft; // bytes of the current TrueHD access unit still to copy (MAT zero-pads between them)

    byte[] payload = [];
    int payloadPos, payloadLen;
    string codec;
    readonly byte[] header = new byte[4];

    /// <summary>
    /// Pa/Pb burst sync (F872 4E1F) as LE words.
    /// </summary>
    static ReadOnlySpan<byte> Sync => [0x72, 0xF8, 0x1F, 0x4E];

    /// <summary>
    /// Codec of the first data burst ("E-AC-3", "TrueHD", "DTS"), null if the input ends before one. Blocks until then.
    /// </summary>
    public string Codec {
        get {
            _ = Length;
            return codec;
        }
    }

    /// <summary>
    /// Whether a block contains a burst sync. The WAV header doesn't tell passthrough from PCM (plain DTS comes as
    /// 48 kHz stereo).
    /// </summary>
    public static bool HasSync(ReadOnlySpan<byte> data) => data.IndexOf(Sync) >= 0;

    /// <summary>
    /// The pipe closed (mpv closed the AO): the decoder should stop.
    /// </summary>
    public bool EndOfInput { get; private set; }

    // Cavern assumes Read fills the whole request (like its own QueueStream), so block until it does.
    public override int Read(byte[] buffer, int offset, int count) {
        int read = 0;
        while (read < count) {
            while (payloadPos == payloadLen) {
                if (!NextBurst()) {
                    EndOfInput = true;
                    return read;
                }
            }
            int n = Math.Min(count - read, payloadLen - payloadPos);
            Buffer.BlockCopy(payload, payloadPos, buffer, offset + read, n);
            payloadPos += n;
            read += n;
        }
        return read;
    }

    /// <summary>
    /// Scan to the next Pa/Pb sync, read Pc/Pd and the byte-swapped payload.
    /// </summary>
    bool NextBurst() {
        int state = 0; // matched bytes of the sync
        while (state < 4) {
            int b = input.ReadByte();
            if (b < 0) {
                return false;
            }
            state = b == Sync[state] ? state + 1 : (b == Sync[0] ? 1 : 0);
        }
        if (!ReadExact(header, 4)) {
            return false;
        }
        int type = header[0] & 0x1F;
        bool dtsCore = type is >= typeDts1 and <= typeDts3;
        int length = (header[2] | header[3] << 8) >> (dtsCore ? 3 : 0); // DTS type I-III: Pd in bits, others bytes
        string burstCodec = type switch {
            typeEac3 => "E-AC-3",
            typeTrueHd when length == 61424 => "TrueHD",
            typeDtsHd => "DTS",
            _ => dtsCore ? "DTS" : null, // pause bursts, AC-3 etc.: skipped
        };
        int padded = (length + 1) & ~1;
        if (payload.Length < padded) {
            payload = new byte[padded];
        }
        if (!ReadExact(payload, padded)) {
            return false;
        }
        if (burstCodec == null) {
            payloadPos = payloadLen = 0;
            return true;
        }
        Span<ushort> words = MemoryMarshal.Cast<byte, ushort>(payload.AsSpan(0, padded));
        BinaryPrimitives.ReverseEndianness(words, words);
        codec ??= burstCodec;
        (payloadPos, payloadLen) = type switch {
            typeTrueHd => (0, UnwrapMat(length)),
            typeDtsHd => DtsHdFrame(length),
            _ => (0, length),
        };
        return true;
    }

    /// <summary>
    /// DTS-HD burst: 10-byte start code (01 00 00 00 00 00 00 00 FE FE), big-endian frame size, frame, padding.
    /// </summary>
    (int, int) DtsHdFrame(int length) {
        int size = length < 12 ? 0 : payload[10] << 8 | payload[11];
        return size > 0 && payload[9] == 0xFE && 12 + size <= length ? (12, 12 + size) : (0, 0);
    }

    /// <summary>
    /// Drop the MAT codes and the zero padding between access units (each starts with a non-zero
    /// check nibble + 12-bit length in words), compacting the payload in place. Units may span frames.
    /// </summary>
    int UnwrapMat(int length) {
        int w = 0;
        foreach ((int from, int to) in new[] { (20, matMiddle), (matMiddle + 12, length - 16) }) {
            for (int r = from; r < to; r += 2) {
                if (auLeft == 0) {
                    int word = payload[r] << 8 | payload[r + 1];
                    if (word == 0) {
                        continue;
                    }
                    auLeft = (word & 0xFFF) * 2;
                }
                payload[w++] = payload[r];
                payload[w++] = payload[r + 1];
                auLeft -= 2;
            }
        }
        return w;
    }

    bool ReadExact(byte[] buf, int count) => input.ReadAtLeast(buf.AsSpan(0, count), count, false) == count;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    // Bytes of the current burst still unread (blocks until one is available)
    public override long Length {
        get {
            while (payloadPos == payloadLen && NextBurst()) { }
            return payloadLen - payloadPos;
        }
    }
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
