using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AtmosBridge;

/// <summary>
/// Passthrough bitstreams the bridge decodes.
/// </summary>
enum Codec { Eac3, TrueHd, Dts }

static class CodecExtensions {
    /// <summary>
    /// mpv's audio-spdif names that pass this codec through.
    /// </summary>
    public static string SpdifNames(this Codec codec) => codec switch {
        Codec.Eac3 => "eac3,ac3",
        Codec.TrueHd => "truehd",
        _ => "dts-hd,dts",
    };
}

/// <summary>
/// Unwraps IEC 61937 bursts (as written by mpv's spdif passthrough, 16-bit LE words) into the raw bitstream:
/// E-AC-3 for Cavern, TrueHD (MAT frames) for thd-objects, DTS (core, DTS-HD, DTS:X) for dtsx-objects.
/// </summary>
sealed class Iec61937Stream(Stream input) : Stream {
    const int typeAc3 = 1, typeDts1 = 11, typeDts3 = 13, typeDtsHd = 17, typeEac3 = 21, typeTrueHd = 22;
    const int matMiddle = 30708; // MAT frame: 20-byte start code, 12-byte middle code here, 16-byte end code

    int auLeft; // bytes of the current TrueHD access unit still to copy (MAT zero-pads between them)

    byte[] payload = [];
    int payloadPos, payloadLen;
    Codec? codec;
    readonly byte[] header = new byte[4];

    /// <summary>
    /// Pa/Pb burst sync (F872 4E1F) as LE words.
    /// </summary>
    static ReadOnlySpan<byte> Sync => [0x72, 0xF8, 0x1F, 0x4E];

    /// <summary>
    /// Plain AC-3 rather than E-AC-3 (both are Codec.Eac3), from the first data burst.
    /// </summary>
    public bool Ac3 { get; private set; }

    /// <summary>
    /// Codec of the first data burst, null if the input ends before one. Blocks until then.
    /// </summary>
    public Codec? WaitForCodec() {
        _ = Length;
        return codec;
    }

    /// <summary>
    /// Whether a block (starting on a sample frame) holds a burst: a sync on a 4-byte IEC 60958 frame boundary followed by
    /// a defined data type (up to 22, MAT). The WAV header doesn't tell passthrough from PCM (plain DTS comes as 48 kHz
    /// stereo). Codecs the bridge doesn't decode (MPEG, AAC, ...) still count: their bursts are skipped, i.e. silence.
    /// </summary>
    public static bool HasSync(ReadOnlySpan<byte> data) {
        for (int i = 0; i + 5 <= data.Length; i += 4) {
            if (data.Slice(i, 4).SequenceEqual(Sync) && (data[i + 4] & 0x7F) <= typeTrueHd) {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a block holds a DTS core sync without burst framing: ffmpeg's spdif muxer drops Pa/Pb when the core frame
    /// fills the whole burst period (DTS-CD style streams). 16-bit sync as LE words, or 14-bit packing either way round
    /// (same sync words as DTSX_decoder's frame_sync.cpp).
    /// </summary>
    public static bool HasRawDtsSync(ReadOnlySpan<byte> data) {
        for (int i = 0; i + 4 <= data.Length; i += 4) {
            if (BinaryPrimitives.ReadUInt32BigEndian(data[i..]) is 0xFE7F0180 or 0xFF1F00E8 or 0x1FFFE800) {
                return true;
            }
        }
        return false;
    }

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
        int length = (header[2] | header[3] << 8) >> (dtsCore || type == typeAc3 ? 3 : 0); // AC-3, DTS I-III: Pd in bits
        int padded = (length + 1) & ~1;
        if (payload.Length < padded) {
            payload = new byte[padded];
        }
        if (!ReadExact(payload, padded)) {
            return false;
        }
        Span<ushort> words = MemoryMarshal.Cast<byte, ushort>(payload.AsSpan(0, padded));
        BinaryPrimitives.ReverseEndianness(words, words);
        (Codec? burst, payloadPos, payloadLen) = type switch {
            // Cavern's E-AC-3 decoder is meant to take plain AC-3 too, but 2.1.0 misparses its coupling and throws:
            // atmosbridge.lua keeps AC-3 in mpv, and a burst arriving anyway goes back through the decode fallback
            typeEac3 or typeAc3 => (Codec.Eac3, 0, length),
            typeTrueHd when length == 61424 => (Codec.TrueHd, 0, UnwrapMat(length)),
            typeDtsHd => DtsHdFrame(length),
            _ when dtsCore => (Codec.Dts, 0, length),
            _ => (null, 0, 0), // pause bursts, MPEG etc.: skipped
        };
        // ponytail: the first burst fixes the codec; mpv reopens the AO (a new pipe connection) when it changes
        if (codec == null && burst != null) {
            codec = burst;
            Ac3 = type == typeAc3;
        }
        return true;
    }

    /// <summary>
    /// DTS-HD burst: 10-byte start code (01 00 00 00 00 00 00 00 FE FE), big-endian frame size, frame, padding.
    /// </summary>
    (Codec?, int, int) DtsHdFrame(int length) {
        int size = length < 12 ? 0 : payload[10] << 8 | payload[11];
        return size > 0 && payload[9] == 0xFE && 12 + size <= length ? (Codec.Dts, 12, 12 + size) : (null, 0, 0);
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

/// <summary>
/// Bytes already read (sniffed) followed by the rest of the stream.
/// </summary>
sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream {
    int pos;

    public override int Read(Span<byte> buffer) {
        if (pos == prefix.Length) {
            return rest.Read(buffer);
        }
        int n = Math.Min(buffer.Length, prefix.Length - pos);
        prefix.AsSpan(pos, n).CopyTo(buffer);
        pos += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    // Iec61937Stream scans for syncs byte by byte; the rest is buffered already
    public override int ReadByte() => pos < prefix.Length ? prefix[pos++] : rest.ReadByte();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
