namespace AtmosBridge;

/// <summary>
/// Unwraps IEC 61937 bursts (as written by mpv's spdif passthrough, 16-bit LE words) into the raw bitstream:
/// E-AC-3 for Cavern, or TrueHD (MAT frames) for thd-objects.
/// </summary>
sealed class Iec61937Stream(Stream input) : Stream {
    const int typeEac3 = 21, typeTrueHd = 22;
    const int matMiddle = 30708; // MAT frame: 20-byte start code, 12-byte middle code here, 16-byte end code

    int auLeft; // bytes of the current TrueHD access unit still to copy (MAT zero-pads between them)

    byte[] payload = [];
    int payloadPos, payloadLen;
    readonly byte[] header = new byte[8];

    /// <summary>
    /// The pipe closed (mpv closed the AO): the decoder should stop.
    /// </summary>
    public bool EndOfInput { get; private set; }

    public long Bursts { get; private set; }

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
    /// Scan to the next Pa/Pb sync (F872 4E1F), read Pc/Pd and the byte-swapped payload.
    /// </summary>
    bool NextBurst() {
        int state = 0; // matched bytes of 72 F8 1F 4E
        ReadOnlySpan<byte> sync = [0x72, 0xF8, 0x1F, 0x4E];
        while (state < 4) {
            int b = input.ReadByte();
            if (b < 0) {
                return false;
            }
            state = b == sync[state] ? state + 1 : (b == sync[0] ? 1 : 0);
        }
        if (!ReadExact(header, 4)) {
            return false;
        }
        int dataType = header[0] & 0x1F;
        int length = header[2] | header[3] << 8; // E-AC-3 and TrueHD: Pd is in bytes
        int padded = (length + 1) & ~1;
        if (payload.Length < padded) {
            payload = new byte[padded];
        }
        if (!ReadExact(payload, padded)) {
            return false;
        }
        if (dataType != typeEac3 && (dataType != typeTrueHd || length != 61424)) {
            payloadPos = payloadLen = 0; // pause bursts, AC-3 etc.: skip
            return true;
        }
        for (int i = 0; i < padded; i += 2) {
            (payload[i], payload[i + 1]) = (payload[i + 1], payload[i]);
        }
        payloadPos = 0;
        payloadLen = dataType == typeTrueHd ? UnwrapMat(length) : length;
        Bursts++;
        return true;
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

    bool ReadExact(byte[] buf, int count) {
        int got = 0;
        while (got < count) {
            int n = input.Read(buf, got, count - got);
            if (n <= 0) {
                return false;
            }
            got += n;
        }
        return true;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    // Cavern sizes its fetch block from Length once (else 10 MB); report one burst so it reads ~32 ms at a time.
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
