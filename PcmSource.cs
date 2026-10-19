using System.Numerics;

namespace AtmosBridge;

/// <summary>
/// mpv's ao_pcm WAV header, and plain PCM playback as one object per speaker position.
/// </summary>
sealed class WaveFormat {
    public int Channels, SampleRate, Bits;
    public bool Float;
    public uint ChannelMask;

    /// <summary>
    /// mpv writes S16 2ch 192 kHz for spdif passthrough; the payload then carries IEC 61937 bursts.
    /// </summary>
    public bool IsSpdif => Channels == 2 && SampleRate == 192000 && Bits == 16 && !Float;

    /// <summary>
    /// TrueHD passthrough (audio-spdif=truehd) is HBR: S16 8ch 192 kHz carrying IEC 61937 MAT bursts.
    /// </summary>
    public bool IsHbr => Channels == 8 && SampleRate == 192000 && Bits == 16 && !Float;

    /// <summary>
    /// Plain DTS passthrough (no DTS-HD) is S16 2ch at 44.1/48 kHz, the same header as stereo PCM.
    /// </summary>
    public bool MaybeDts => Channels == 2 && SampleRate is 44100 or 48000 && Bits == 16 && !Float;

    /// <summary>
    /// Read RIFF/WAVE chunks up to the start of the data chunk.
    /// </summary>
    public static WaveFormat Read(Stream s) {
        var r = new BinaryReader(s);
        if (r.ReadUInt32() != 0x46464952 || r.ReadUInt32() == 0 || r.ReadUInt32() != 0x45564157) { // RIFF ... WAVE
            throw new InvalidDataException("Not a WAV stream: set --ao-pcm-waveheader=yes in mpv");
        }
        var f = new WaveFormat();
        while (true) {
            uint id = r.ReadUInt32(), size = r.ReadUInt32();
            if (id == 0x61746164) { // data
                return f;
            }
            byte[] chunk = r.ReadBytes((int)size);
            if (id == 0x20746D66) { // fmt
                int tag = BitConverter.ToUInt16(chunk, 0);
                f.Channels = BitConverter.ToUInt16(chunk, 2);
                f.SampleRate = BitConverter.ToInt32(chunk, 4);
                f.Bits = BitConverter.ToUInt16(chunk, 14);
                f.Float = tag == 3;
                if (tag == 0xFFFE && size >= 40) {
                    f.ChannelMask = BitConverter.ToUInt32(chunk, 20);
                    f.Float = chunk[24] == 3; // SubFormat GUID starts with the format tag
                }
            }
        }
    }

    public override string ToString() =>
        $"{Channels}ch {SampleRate} Hz {Bits}-bit{(Float ? " float" : "")} mask 0x{ChannelMask:X}";
}

/// <summary>
/// Bytes already read (sniffed) followed by the rest of the stream.
/// </summary>
sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream {
    int pos;

    public override int Read(byte[] buffer, int offset, int count) {
        if (pos == prefix.Length) {
            return rest.Read(buffer, offset, count);
        }
        int n = Math.Min(count, prefix.Length - pos);
        Buffer.BlockCopy(prefix, pos, buffer, offset, n);
        pos += n;
        return n;
    }

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

static class PcmSource {
    /// <summary>
    /// WAVEFORMATEXTENSIBLE speaker bits in order, as Windows spatial positions (x right, y up, -z front).
    /// </summary>
    static readonly Vector3[] speakerPositions = [
        new(-1, 0, -1), new(1, 0, -1), new(0, 0, -1), default /* LFE */, new(-1, 0, 1), new(1, 0, 1),
        new(-.5f, 0, -1), new(.5f, 0, -1), new(0, 0, 1), new(-1, 0, 0), new(1, 0, 0), new(0, 1, 0),
        new(-1, 1, -1), new(0, 1, -1), new(1, 1, -1), new(-1, 1, 1), new(0, 1, 1), new(1, 1, 1),
    ];

    static uint DefaultMask(int channels) => channels switch {
        1 => 0x4, 2 => 0x3, 6 => 0x60F, 8 => 0x63F, _ => (1u << channels) - 1,
    };

    /// <summary>
    /// Deinterleave blocks of PCM into chunks until the stream ends.
    /// </summary>
    public static IEnumerable<Chunk> Read(Stream s, WaveFormat f, int blockSize) {
        uint mask = f.ChannelMask != 0 ? f.ChannelMask : DefaultMask(f.Channels);
        var positions = new Vector3[f.Channels];
        var lfe = new bool[f.Channels];
        for (int ch = 0, bit = 0; ch < f.Channels && bit < 32; bit++) {
            if ((mask & (1u << bit)) != 0) {
                lfe[ch] = bit == 3;
                positions[ch] = bit < speakerPositions.Length ? speakerPositions[bit] : default;
                ch++;
            }
        }
        var volumes = Enumerable.Repeat(1f, f.Channels).ToArray();

        int bytesPerSample = f.Bits / 8, frameBytes = bytesPerSample * f.Channels;
        byte[] raw = new byte[frameBytes * blockSize];
        while (s.ReadAtLeast(raw, raw.Length, false) == raw.Length) {
            var samples = new float[f.Channels][];
            for (int ch = 0; ch < f.Channels; ch++) {
                samples[ch] = new float[blockSize];
            }
            for (int i = 0; i < blockSize; i++) {
                for (int ch = 0; ch < f.Channels; ch++) {
                    int o = i * frameBytes + ch * bytesPerSample;
                    samples[ch][i] = (f.Bits, f.Float) switch {
                        (32, true) => BitConverter.ToSingle(raw, o),
                        (32, false) => BitConverter.ToInt32(raw, o) / 2147483648f,
                        (16, _) => BitConverter.ToInt16(raw, o) / 32768f,
                        _ => (raw[o] - 128) / 128f,
                    };
                }
            }
            yield return new Chunk(samples, positions, volumes, lfe);
        }
    }
}
