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

    /// <summary>
    /// Whether this can be mpv's passthrough (spdif) output rather than PCM: E-AC-3 and DTS-HD HRA come as S16 2ch
    /// 192 kHz, TrueHD and DTS-HD MA as S16 8ch 192 kHz (HBR), plain DTS as S16 2ch 44.1/48 kHz.
    /// </summary>
    public bool MayCarryBitstream => Bits == 16 && !Float && Channels is 2 or 8;

    public override string ToString() =>
        $"{Channels}ch {SampleRate} Hz {Bits}-bit{(Float ? " float" : "")} mask 0x{ChannelMask:X}";
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
                positions[ch] = speakerPositions[bit < speakerPositions.Length ? bit : 2]; // no standard speaker (22.2 extras): front center
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
