using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Numerics;
using System.Runtime.InteropServices;

using Cavern;
using Cavern.Format;
using Cavern.Format.Decoders;
using Cavern.Format.Renderers;
using Cavern.Format.Utilities;

namespace AtmosBridge;

/// <summary>
/// One decoded block: mono samples, position and gain for every Cavern object.
/// </summary>
sealed record Chunk(float[][] Samples, Vector3[] Positions, float[] Volumes, bool[] Lfe) {
    public Chunk(int channels) : this(new float[channels][], new Vector3[channels], new float[channels], new bool[channels]) { }

    public int Length => Samples.Length == 0 ? 0 : Samples[0].Length;
}

static class Program {
    internal const int sampleRate = 48000; // Windows spatial audio object format
    const int blockSize = 480; // 10 ms @ 48 kHz, matches the spatial engine's period
    const float roomScale = 1; // meters from listener to the Atmos room wall

    static BlockingCollection<Chunk> queue; // capped at ~2.5x latency: without video mpv writes as fast as we read
    static volatile bool running = true;
    static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Bridge latency the buffer is held at (--latency). mpv's audio-delay should be about -(this + 0.05).
    /// </summary>
    static int targetSamples = sampleRate / 10;

    /// <summary>
    /// Samples decoded but not yet handed to the spatial engine.
    /// </summary>
    static long buffered;

    static volatile bool playing, discarding;
    static volatile bool outputReady, sessionActive; // spatial stream open / mpv connected on the audio pipe
    static volatile int flushRequests;
    static long discardSince;
    static double ratio = 1;

    static void Log(string message) => Console.WriteLine($"[{clock.Elapsed.TotalSeconds:0.00}] {message}");

    static int Main(string[] args) {
        string pipeName = "atmos", ipcName = "mpv-atmos", deviceId = null, file = null;
        for (int i = 0; i < args.Length - 1; i++) {
            switch (args[i]) {
                case "--pipe": pipeName = args[++i]; break;
                case "--ipc": ipcName = args[++i]; break;
                case "--latency": targetSamples = (int)(double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture) * sampleRate); break;
                case "--device": deviceId = args[++i]; break;
                case "--file": file = args[++i]; break;
                case "--log": // windowless runs (login startup) have no console
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[i + 1])));
                    Console.SetOut(new StreamWriter(args[++i], false) { AutoFlush = true });
                    break;
            }
        }

        if (args.Contains("--selftest")) {
            return SelfTest();
        }
        if (args.Contains("--dump")) { // diagnostics: object positions and levels once per second of --file
            (Renderer renderer, int rate) = OpenFile(file);
            Vector3 roomInverse = Vector3.One / Listener.EnvironmentSize;
            for (int second = 0; second < 60 * 15; second++) {
                float[][] samples = renderer.GetNextObjectSamples(rate);
                Console.WriteLine($"{second,4}s " + string.Join(" ", renderer.Objects.Select((o, i) => {
                    Vector3 p = o.Position * roomInverse;
                    double rms = Math.Sqrt(samples[i].Sum(x => (double)x * x) / samples[i].Length);
                    return $"{(o.LFE ? "L" : "")}({p.X:0.0},{p.Y:0.0},{p.Z:0.0}){20 * Math.Log10(rms + 1e-9):0}dB";
                })));
            }
            return 0;
        }

        queue = new(Math.Max(4, targetSamples * 5 / 2 / blockSize));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; queue.CompleteAdding(); };
        var renderThread = new Thread(() => RenderSupervisor(deviceId)) { Priority = ThreadPriority.Highest, IsBackground = true };
        renderThread.Start();

        // Seek: drop what is buffered and whatever still arrives until mpv restarts playback at the new position
        MpvIpc.Seek += () => {
            Interlocked.Exchange(ref discardSince, clock.ElapsedMilliseconds);
            discarding = true;
            flushRequests++;
        };
        MpvIpc.PlaybackRestart += () => discarding = false;
        // mpv started before the bridge (fell back to WASAPI) or the bridge restarted mid-file (mpv's pcm AO lost the pipe):
        // once the bridge can play, ask mpv to reopen its AO, which tries the pipe first
        MpvIpc.Connected += () => Task.Run(() => {
            Thread.Sleep(1500); // a normal start connects the audio pipe right after IPC
            if (outputReady && !sessionActive && MpvIpc.Request("get_property", "current-ao") is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } ao
                && ao.GetString() is "pcm" or "wasapi" && !sessionActive) {
                Log($"mpv plays to {ao.GetString()} without the bridge: reopening its audio output");
                MpvIpc.Request("ao-reload");
            }
        });
        new Thread(() => MpvIpc.Run(ipcName, () => running, Log)) { IsBackground = true }.Start();

        if (file != null) {
            Pump(FileChunks(file));
        } else {
            // The next instance exists before the current session ends: mpv reopens its AO within milliseconds (track
            // change, decode fallback) and falls back to WASAPI if no pipe instance is there to take the reconnect
            NamedPipeServerStream Listen() => new(pipeName, PipeDirection.In, 2, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 0);
            NamedPipeServerStream next = Listen();
            while (running) {
                Console.WriteLine($@"Waiting for mpv on \\.\pipe\{pipeName} ...");
                next.WaitForConnection();
                using var pipe = next;
                sessionActive = true;
                next = Listen();
                Stream input = new BufferedStream(pipe, 16384);
                Codec? passthrough = null;
                WaveFormat format = null;
                try {
                    format = WaveFormat.Read(input);
                    if (OpenBitstream(format, ref input) is (Codec codec, Stream bitstream)) {
                        passthrough = codec;
                        Log($"mpv connected: {codec} passthrough");
                        Pump(Chunks(codec, bitstream));
                    } else if (format.SampleRate == sampleRate && format.Bits is 8 or 16 or 32) {
                        Log($"mpv connected: PCM {format}");
                        Pump(PcmSource.Read(input, format, blockSize));
                    } else {
                        Console.WriteLine($"Unsupported PCM {format}: set audio-samplerate={sampleRate} in mpv");
                    }
                } catch (Exception e) when (e is IOException || e is EndOfStreamException || !pipe.IsConnected) {
                    // mpv closed the pipe (stop, track change)
                } catch (Exception e) {
                    Log("Decode error: " + e);
                    // A stream our decoders can't take (Cavern: stereo/mono E-AC-3; TrueHD above 48 kHz; ...):
                    // let mpv decode it instead, it comes back as PCM on the next connection. Keep reading the pipe
                    // at its real rate meanwhile: mpv's AO thread is blocked writing to it (closing it instead makes
                    // the untimed AO race through the file), and mpv reopens the AO once the change applies.
                    if (passthrough is Codec failed) {
                        string[] codecs = failed switch {
                            Codec.Eac3 => ["eac3", "ac3"],
                            Codec.TrueHd => ["truehd"],
                            _ => ["dts-hd", "dts"],
                        };
                        var asked = Task.Run(() => MpvIpc.DecodeInMpv(codecs, Log));
                        Drain(input, format.SampleRate * format.Channels * 2, () => asked.IsCompleted && !asked.Result);
                    }
                }
                sessionActive = false;
                Log("mpv disconnected");
            }
        }
        running = false;
        renderThread.Join(500);
        return 0;
    }

    /// <summary>
    /// Discard the pipe at its real byte rate until mpv closes it (at most 5 s, or once <paramref name="giveUp"/>).
    /// </summary>
    static void Drain(Stream input, int bytesPerSecond, Func<bool> giveUp) {
        byte[] block = new byte[bytesPerSecond / 100];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try {
            for (long sent = 0; clock.ElapsedMilliseconds < 5000 && !giveUp(); sent += block.Length) {
                if (input.ReadAtLeast(block, block.Length, false) < block.Length) {
                    return; // mpv reopened the AO
                }
                int ahead = (int)(sent * 1000 / bytesPerSecond - clock.ElapsedMilliseconds);
                if (ahead > 0) {
                    Thread.Sleep(ahead);
                }
            }
        } catch (IOException) {
        }
    }

    /// <summary>
    /// mpv's passthrough output, if this is one: IEC 61937 bursts, or plain DTS core frames without burst framing. The WAV
    /// header alone can't tell (plain DTS comes as 48 kHz stereo like PCM), so look past the leading silence (mpv pads
    /// the start for A/V sync, up to a second is checked) for a sync. Everything read goes back in front of the stream,
    /// silence included, so PCM plays unchanged.
    /// </summary>
    static (Codec codec, Stream bitstream)? OpenBitstream(WaveFormat format, ref Stream input) {
        if (!format.MayCarryBitstream) {
            return null;
        }
        int frame = format.Channels * 2;
        var head = new MemoryStream();
        Span<byte> sample = stackalloc byte[frame];
        bool sound = false;
        while (!sound && head.Length < format.SampleRate * frame && input.ReadAtLeast(sample, frame, false) == frame) {
            head.Write(sample);
            sound = sample.ContainsAnyExcept((byte)0);
        }
        int start = (int)head.Length - frame;
        if (sound) {
            byte[] block = new byte[8192]; // >= 1 burst: DTS type III is 2048 samples = 8192 bytes
            head.Write(block, 0, input.ReadAtLeast(block, block.Length, false));
        }
        byte[] read = head.ToArray();
        input = new PrefixedStream(read, input);
        if (!sound) {
            return null;
        }
        if (Iec61937Stream.HasSync(read.AsSpan(start))) {
            var iec = new Iec61937Stream(input);
            return iec.WaitForCodec() is Codec codec ? (codec, iec) : null;
        }
        return Iec61937Stream.HasRawDtsSync(read.AsSpan(start)) ? (Codec.Dts, input) : null;
    }

    /// <summary>
    /// Decoder for a passthrough bitstream (IEC-unwrapped, raw DTS or a raw file).
    /// </summary>
    static IEnumerable<Chunk> Chunks(Codec codec, Stream bitstream) => codec switch {
        Codec.Eac3 => CavernChunks(OpenEac3(bitstream), () => bitstream is Iec61937Stream { EndOfInput: true }),
        Codec.TrueHd => HelperSource.Read(HelperSource.TrueHd, bitstream, blockSize, Log),
        _ => HelperSource.Read(HelperSource.Dts, bitstream, blockSize, Log),
    };

    /// <summary>
    /// --file input: raw TrueHD / DTS go to their helpers, a passthrough capture .wav is unwrapped like the live pipe,
    /// the rest goes to Cavern.
    /// </summary>
    static IEnumerable<Chunk> FileChunks(string path) {
        switch (Path.GetExtension(path).ToLowerInvariant()) {
            case ".thd":
                return Chunks(Codec.TrueHd, File.OpenRead(path));
            case ".dts" or ".dtshd":
                return Chunks(Codec.Dts, File.OpenRead(path));
        }
        return OpenCapture(path) is (Codec codec, Stream bitstream)
            ? Chunks(codec, bitstream)
            : CavernChunks(OpenCavern(path), () => false);
    }

    /// <summary>
    /// A capture of mpv's passthrough output (ao=pcm, --ao-pcm-file=x.wav), or null for any other file.
    /// </summary>
    static (Codec codec, Stream bitstream)? OpenCapture(string path) {
        if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }
        Stream input = new BufferedStream(File.OpenRead(path));
        var capture = OpenBitstream(WaveFormat.Read(input), ref input);
        if (capture == null) {
            input.Dispose();
        }
        return capture;
    }

    /// <summary>
    /// Drift-correct and queue blocks for the render thread. Blocks when the queue is full, which back-pressures mpv.
    /// </summary>
    static void Pump(IEnumerable<Chunk> chunks) {
        var resampler = new DriftResampler();
        var controller = new DriftController(targetSamples, sampleRate);
        discarding = false;
        foreach (Chunk chunk in chunks) {
            if (!running) {
                return;
            }
            if (discarding) {
                if (clock.ElapsedMilliseconds - Interlocked.Read(ref discardSince) < 3000) {
                    continue; // pre-seek audio still in the pipe
                }
                discarding = false; // missed playback-restart
            }
            controller.Update(Interlocked.Read(ref buffered), playing, chunk.Length / (double)sampleRate);
            ratio = controller.Ratio;
            Chunk output = resampler.Process(chunk, ratio);
            Interlocked.Add(ref buffered, output.Length);
            try {
                queue.Add(output);
            } catch (InvalidOperationException) {
                return; // CompleteAdding on exit
            }
        }
    }

    /// <summary>
    /// --dump input: Cavern objects, so E-AC-3 captures and whatever Cavern reads.
    /// </summary>
    static (Renderer, int) OpenFile(string path) => OpenCapture(path) switch {
        (Codec.Eac3, Stream bitstream) => OpenEac3(bitstream),
        (Codec codec, _) => throw new NotSupportedException($"--dump decodes E-AC-3 captures only, this is {codec}"),
        null => OpenCavern(path),
    };

    /// <summary>
    /// .ec3 goes through the same decoder setup as live passthrough; anything else, plain PCM .wav included, goes to
    /// Cavern's AudioReader.
    /// </summary>
    static (Renderer, int) OpenCavern(string path) {
        if (path.EndsWith(".ec3", StringComparison.OrdinalIgnoreCase)) {
            return OpenEac3(File.OpenRead(path));
        }
        AudioReader reader = AudioReader.Open(path);
        return (reader.GetRenderer(), reader.SampleRate);
    }

    /// <summary>
    /// E-AC-3 from mpv's passthrough. Built directly instead of via EnhancedAC3Reader: its stream constructor divides
    /// by a frame size it measures from offsets inside its read blocks, which is 0 when frames sit on block boundaries
    /// (e.g. one 5.1 JOC frame per IEC burst). Small blocks also keep decode latency low.
    /// </summary>
    static (Renderer, int) OpenEac3(Stream raw) {
        var decoder = new EnhancedAC3Decoder(BlockBuffer<byte>.Create(raw, 1024));
        return (new EnhancedAC3Renderer(decoder), decoder.SampleRate);
    }

    /// <summary>
    /// Pull object blocks from Cavern until the input ends.
    /// </summary>
    static IEnumerable<Chunk> CavernChunks((Renderer renderer, int rate) source, Func<bool> ended) {
        (Renderer renderer, int rate) = source;
        if (rate != sampleRate) {
            throw new NotSupportedException($"Stream is {rate} Hz, spatial engine wants {sampleRate} Hz");
        }
        IReadOnlyList<Source> objects = renderer.Objects;
        Log($"Decoding: {objects.Count} objects, has objects (JOC): {renderer.HasObjects}");
        Vector3 roomInverse = Vector3.One / Listener.EnvironmentSize;

        while (!ended()) {
            float[][] samples = renderer.GetNextObjectSamples(blockSize);
            var chunk = new Chunk(objects.Count);
            for (int i = 0; i < objects.Count; i++) {
                chunk.Samples[i] = samples[i]; // Cavern reuses it, but Pump's resampler copies before the next block
                Vector3 p = objects[i].Position * roomInverse; // Cavern: x right, y up, z front, room cube [-1, 1]
                chunk.Positions[i] = new Vector3(p.X, p.Y, -p.Z); // Windows: -z is front
                chunk.Volumes[i] = objects[i].Volume;
                chunk.Lfe[i] = objects[i].LFE;
            }
            yield return chunk;
        }
    }

    /// <summary>
    /// IEC 61937 unwrap check: two E-AC-3 bursts with zero padding and junk around them must come back byte-exact.
    /// </summary>
    static int SelfTest() {
        byte[] frameA = [0x0B, 0x77, 1, 2, 3, 4, 5, 6], frameB = [0x0B, 0x77, 9, 8, 7, 6];
        static void Burst(MemoryStream wire, int type, int pd, byte[] payload) {
            wire.Write([0x72, 0xF8, 0x1F, 0x4E, (byte)type, 0, (byte)pd, (byte)(pd >> 8)]);
            for (int i = 0; i < payload.Length; i += 2) {
                wire.Write([payload[i + 1], payload[i]]); // spdif words are byte-swapped
            }
            wire.Write(new byte[64]);
        }
        var wire = new MemoryStream();
        wire.Write([0xAA, 0x72]); // junk incl. a partial sync byte
        Burst(wire, 21, frameA.Length, frameA);
        Burst(wire, 21, frameB.Length, frameB);
        wire.Write([0x72, 0xF8, 0x1F, 0x4E, 0, 0, 4, 0, 0, 0, 0, 0]); // pause burst (type 0) is skipped
        wire.Position = 0;

        var iec = new Iec61937Stream(wire);
        byte[] expected = [.. frameA, .. frameB], got = new byte[expected.Length];
        bool ok = iec.WaitForCodec() == Codec.Eac3 && iec.Length == frameA.Length && iec.Read(got, 0, got.Length) == got.Length
            && got.SequenceEqual(expected) && iec.Read(new byte[1], 0, 1) == 0 && iec.EndOfInput;
        Console.WriteLine(ok ? "IEC 61937 OK" : "IEC 61937 FAILED: " + Convert.ToHexString(got));

        // AC-3 (type 1, Pd in bits) goes to the E-AC-3 decoder like E-AC-3
        var ac3Wire = new MemoryStream();
        Burst(ac3Wire, 1, frameA.Length * 8, frameA);
        ac3Wire.Position = 0;
        var ac3 = new Iec61937Stream(ac3Wire);
        byte[] ac3Got = new byte[frameA.Length];
        bool ac3Ok = ac3.WaitForCodec() == Codec.Eac3 && ac3.Read(ac3Got, 0, ac3Got.Length) == ac3Got.Length && ac3Got.SequenceEqual(frameA);
        Console.WriteLine(ac3Ok ? "AC-3 OK" : "AC-3 FAILED: " + Convert.ToHexString(ac3Got));
        ok &= ac3Ok;

        // DTS type I (Pd in bits) and DTS-HD (start code + frame size, Pd padded past the frame): bare frames come back
        byte[] core = [0x7F, 0xFE, 0x80, 0x01, 1, 2], hd = [0x64, 0x58, 0x20, 0x25, 3, 4, 5, 6];
        var dtsWire = new MemoryStream();
        Burst(dtsWire, 11, core.Length * 8, core);
        Burst(dtsWire, 17, 24, [1, 0, 0, 0, 0, 0, 0, 0, 0xFE, 0xFE, 0, (byte)hd.Length, .. hd, 0, 0, 0, 0]);
        dtsWire.Position = 0;
        var dtsIec = new Iec61937Stream(dtsWire);
        byte[] dts = new byte[core.Length + hd.Length];
        bool dtsOk = dtsIec.WaitForCodec() == Codec.Dts && dtsIec.Read(dts, 0, dts.Length) == dts.Length
            && dts.SequenceEqual([.. core, .. hd]) && dtsIec.Read(new byte[1], 0, 1) == 0;
        Console.WriteLine(dtsOk ? "DTS OK" : "DTS FAILED: " + Convert.ToHexString(dts));
        ok &= dtsOk;

        // Passthrough detection: bursts only on IEC 60958 frames with a defined type; plain DTS without bursts (core
        // sync as LE words); PCM, leading silence included, comes back untouched
        byte[] sync = [0x72, 0xF8, 0x1F, 0x4E];
        bool syncOk = Iec61937Stream.HasSync(dtsWire.ToArray()) && !Iec61937Stream.HasSync(new byte[64])
            && !Iec61937Stream.HasSync([0, 0, .. sync, 21, 0, 0, 0]) && !Iec61937Stream.HasSync([.. sync, 0x40, 0])
            && Iec61937Stream.HasRawDtsSync([0, 0, 0, 0, 0xFE, 0x7F, 0x01, 0x80]) && !Iec61937Stream.HasRawDtsSync(new byte[64]);
        var stereo = new WaveFormat { Channels = 2, SampleRate = sampleRate, Bits = 16 };
        byte[] pcm = [.. new byte[4000], 1, 2, 3, 4, .. Enumerable.Repeat((byte)5, 20000)];
        Stream pcmIn = new MemoryStream(pcm);
        bool pcmOk = OpenBitstream(stereo, ref pcmIn) == null && new BinaryReader(pcmIn).ReadBytes(pcm.Length + 1).SequenceEqual(pcm);
        Stream rawIn = new MemoryStream([.. new byte[400], 0xFE, 0x7F, 0x01, 0x80, .. new byte[100]]);
        bool rawOk = OpenBitstream(stereo, ref rawIn) is (Codec.Dts, _);
        bool detectOk = syncOk && pcmOk && rawOk;
        Console.WriteLine(detectOk ? "passthrough detection OK" : $"passthrough detection FAILED: sync {syncOk} pcm {pcmOk} raw DTS {rawOk}");
        ok &= detectOk;

        // TrueHD MAT: codes and zero padding go, access units (length in words, low 12 bits) survive, also split by the middle code
        byte[] mat = new byte[61424];
        mat[0] = 0x07; // start code (content not checked)
        byte[] auA = [0x10, 0x02, 0xAB, 0xCD], auB = [0x10, 0x03, 1, 2, 3, 4];
        auA.CopyTo(mat, 20);
        auB.AsSpan(0, 4).CopyTo(mat.AsSpan(30704)); // auB straddles the 12-byte middle code at 30708
        mat[30708] = 0xC3;
        auB.AsSpan(4).CopyTo(mat.AsSpan(30720));
        mat[^16] = 0xC3; // end code
        var hbr = new MemoryStream();
        hbr.Write([0x72, 0xF8, 0x1F, 0x4E, 22, 0, 0xF0, 0xEF]); // Pd = 61424
        for (int i = 0; i < mat.Length; i += 2) {
            hbr.Write([mat[i + 1], mat[i]]);
        }
        hbr.Position = 0;
        byte[] thd = new byte[auA.Length + auB.Length];
        bool matOk = new Iec61937Stream(hbr).Read(thd, 0, thd.Length) == thd.Length && thd.SequenceEqual([.. auA, .. auB]);
        Console.WriteLine(matOk ? "MAT OK" : "MAT FAILED: " + Convert.ToHexString(thd));
        ok &= matOk;

        // Resampler: ratio 1 is a 2-sample-delayed exact copy; ratio 1.001 yields 0.1 % more samples over time
        var ramp = Enumerable.Range(0, 2000).Select(i => (float)Math.Sin(i * .01)).ToArray();
        Chunk Block(int start) => new([ramp[start..(start + 500)]], [default], [1], [false]);
        var exact = new DriftResampler();
        float[] a = exact.Process(Block(0), 1).Samples[0], b = exact.Process(Block(500), 1).Samples[0];
        bool passthrough = a.Length == 500 && b.Length == 500 && Math.Abs(b[10] - ramp[508]) < 1e-6f;
        var drift = new DriftResampler();
        int total = 0;
        for (int k = 0; k < 400; k++) {
            total += drift.Process(Block(0), 1.001).Samples[0].Length;
        }
        bool stretched = Math.Abs(total - 200_000 * 1.001) <= 2;
        Console.WriteLine($"resampler passthrough {(passthrough ? "OK" : "FAILED")}, drift {(stretched ? "OK" : "FAILED")} ({total})");
        ok &= passthrough && stretched;

        // PCM layouts: every channel keeps its samples and lands at its speaker, by WAVEFORMATEXTENSIBLE bit
        // (signs of x right / y up / z back; LFE has no position). One mask per layout mpv can send.
        (int x, int y, int z)[] side = [
            (-1, 0, -1), (1, 0, -1), (0, 0, -1), (0, 0, 0), (-1, 0, 1), (1, 0, 1), (-1, 0, -1), (1, 0, -1), (0, 0, 1),
            (-1, 0, 0), (1, 0, 0), (0, 1, 0), (-1, 1, -1), (0, 1, -1), (1, 1, -1), (-1, 1, 1), (0, 1, 1), (1, 1, 1),
        ];
        var layouts = new (string name, uint mask)[] {
            ("mono", 0x4), ("stereo", 0x3), ("2.1", 0xB), ("3.0", 0x7), ("quad", 0x33), ("4.0", 0x107), ("5.0", 0x607),
            ("5.1", 0x3F), ("5.1(side)", 0x60F), ("6.1", 0x70F), ("7.1", 0x63F), ("7.1(wide)", 0xFF), ("5.1.2", 0x560F),
            ("5.1.4", 0x2D60F), ("7.1.2", 0x563F), ("7.1.4", 0x2D63F), ("all 18", 0x3FFFF), ("22.2 via mpv", 0xFFFFFF),
        };
        var failed = new List<string>();
        foreach ((string name, uint mask) in layouts) {
            int channels = System.Numerics.BitOperations.PopCount(mask);
            var wav = new MemoryStream();
            var w = new BinaryWriter(wav);
            w.Write(0x46464952); w.Write(0x7FFFF000); w.Write(0x45564157); // RIFF size WAVE
            w.Write(0x20746D66); w.Write(40); // fmt, WAVEFORMATEXTENSIBLE
            w.Write((ushort)0xFFFE); w.Write((ushort)channels); w.Write(48000); w.Write(48000 * channels * 2);
            w.Write((ushort)(channels * 2)); w.Write((ushort)16); w.Write((ushort)22); w.Write((ushort)16); w.Write(mask);
            w.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
            w.Write(0x61746164); w.Write(0x7FFFF000); // data
            for (int i = 0; i < blockSize; i++) {
                for (int ch = 0; ch < channels; ch++) {
                    w.Write((short)((ch + 1) * 1000));
                }
            }
            wav.Position = 0;
            WaveFormat format = WaveFormat.Read(wav);
            Chunk chunk = PcmSource.Read(wav, format, blockSize).First();
            for (int ch = 0, bit = 0; bit < 32; bit++) {
                if ((mask & (1u << bit)) == 0) {
                    continue;
                }
                Vector3 p = chunk.Positions[ch];
                (int x, int y, int z) e = bit < side.Length ? side[bit] : side[2];
                bool right = chunk.Samples[ch][0] == (ch + 1) * 1000 / 32768f && chunk.Lfe[ch] == (bit == 3)
                    && Math.Sign(p.X) == e.x && Math.Sign(p.Y) == e.y && Math.Sign(p.Z) == e.z;
                if (!right) {
                    failed.Add($"{name} ch{ch} (bit {bit}) at {p}");
                }
                ch++;
            }
        }
        Console.WriteLine(failed.Count == 0 ? $"PCM layouts OK ({layouts.Length})" : "PCM layouts FAILED: " + string.Join("; ", failed));
        ok &= failed.Count == 0;
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Keep a spatial stream open, reopening it after failures. While there is none, drop audio in real time
    /// so mpv keeps playing (silently) instead of stalling on the pipe.
    /// </summary>
    static void RenderSupervisor(string deviceId) {
        while (running) {
            try {
                using var output = new SpatialOutput(deviceId);
                Log($"Spatial endpoint {output.DeviceId}: {output.MaxDynamicObjects} dynamic objects, {output.SampleRate} Hz");
                if (output.SampleRate != sampleRate) {
                    Console.WriteLine($"Warning: spatial engine runs at {output.SampleRate} Hz, bridge assumes {sampleRate} Hz");
                }
                outputReady = true;
                try {
                    RenderLoop(output);
                } finally {
                    outputReady = false;
                }
            } catch (Exception e) {
                Log($"Spatial output unavailable: {e.Message}");
            }
            playing = false;
            while (queue.TryTake(out _)) { }
            Interlocked.Exchange(ref buffered, 0);
            for (int i = 0; i < 300 && running; i++) { // retry in 3 s, consuming one 10 ms block per 10 ms
                Thread.Sleep(10);
                if (queue.TryTake(out Chunk dropped)) {
                    Interlocked.Add(ref buffered, -dropped.Length);
                }
            }
        }
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr AvSetMmThreadCharacteristics(string task, ref int index);

    /// <summary>
    /// Feed the spatial engine every period: one dynamic object per Cavern object, LFE to the static LFE object.
    /// Underruns (mpv paused or not connected) are written as silence.
    /// </summary>
    static unsafe void RenderLoop(SpatialOutput output) {
        int taskIndex = 0;
        AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
        ISpatialAudioObjectRenderStream stream = output.Stream;
        // Slot 0 is the static LFE object, the rest are dynamic objects; inputs map onto them by role
        var slots = new List<ISpatialAudioObject>();
        var buffers = new List<IntPtr>();
        Chunk current = null;
        int currentPos = 0;
        bool prebuffering = true;
        int handledFlush = flushRequests;
        long periods = 0, underruns = 0;
        float maxHeight = 0; // highest audible object since the last status line

        // Slot of each input channel/object: LFE -> 0, others -> 1, 2, ... (-1 when out of objects)
        int Slot(Chunk chunk, int index) {
            if (chunk.Lfe[index]) {
                return 0;
            }
            int dynamic = 1;
            for (int i = 0; i < index; i++) {
                if (!chunk.Lfe[i]) {
                    dynamic++;
                }
            }
            return dynamic < slots.Count ? dynamic : -1;
        }

        SpatialOutput.Check(stream.Start(), "Start");
        while (running) {
            output.BufferEvent.WaitOne(100);
            int hr = stream.BeginUpdatingAudioObjects(out _, out uint frames);
            if (hr < 0) {
                Console.WriteLine($"Spatial stream lost: 0x{hr:X8}"); // device unplugged, spatial sound off, exclusive app
                return;
            }

            if (handledFlush != flushRequests) { // mpv seeked: everything queued is from the old position
                handledFlush = flushRequests;
                long dropped = current == null ? 0 : current.Length - currentPos;
                current = null;
                while (queue.TryTake(out Chunk old)) {
                    dropped += old.Length;
                }
                Interlocked.Add(ref buffered, -dropped);
                prebuffering = true;
            }
            if (prebuffering && Interlocked.Read(ref buffered) >= targetSamples) {
                prebuffering = false;
            }
            bool consume = !prebuffering && !MpvIpc.Paused;
            playing = consume;

            if (consume && current == null && queue.TryTake(out Chunk first)) {
                current = first;
                currentPos = 0;
            }
            if (slots.Count == 0) {
                SpatialOutput.Check(stream.ActivateSpatialAudioObject(AudioObjectType.LowFrequency, out ISpatialAudioObject lfe), "LFE object");
                slots.Add(lfe);
            }
            int needed = current == null ? 0 : 1 + current.Lfe.Count(x => !x);
            while (slots.Count < needed && stream.ActivateSpatialAudioObject(AudioObjectType.Dynamic, out ISpatialAudioObject obj) >= 0) {
                slots.Add(obj); // stops early when out of objects: the rest stays silent
            }

            buffers.Clear();
            foreach (ISpatialAudioObject slot in slots) {
                slot.GetBuffer(out IntPtr buffer, out _);
                new Span<float>((void*)buffer, (int)frames).Clear();
                buffers.Add(buffer);
            }

            int written = 0;
            while (consume && written < frames && current != null) {
                int n = Math.Min((int)frames - written, current.Length - currentPos);
                for (int i = 0; i < current.Samples.Length; i++) {
                    int slot = Slot(current, i);
                    if (slot >= 0) {
                        current.Samples[i].AsSpan(currentPos, n).CopyTo(new Span<float>((float*)buffers[slot] + written, n));
                    }
                }
                written += n;
                currentPos += n;
                if (currentPos == current.Length) {
                    if (queue.TryTake(out Chunk next)) {
                        current = next;
                        currentPos = 0;
                    } else {
                        current = null; // underrun: rest of the period stays silent
                    }
                }
            }

            Interlocked.Add(ref buffered, -written);

            if (consume && current != null) {
                for (int i = 0; i < current.Samples.Length; i++) {
                    int slot = Slot(current, i);
                    if (slot > 0) {
                        Vector3 p = current.Positions[i] * roomScale;
                        slots[slot].SetPosition(p.X, p.Y, p.Z);
                        if (p.Y > maxHeight && current.Samples[i].AsSpan().ContainsAnyExceptInRange(-1e-4f, 1e-4f)) {
                            maxHeight = p.Y;
                        }
                    }
                    if (slot >= 0) {
                        slots[slot].SetVolume(current.Volumes[i]);
                    }
                }
            }
            stream.EndUpdatingAudioObjects();

            if (consume && written < frames) {
                underruns++;
                prebuffering = true; // rebuild the latency cushion instead of crackling
            }
            if (++periods % 500 == 0 && current != null) { // every ~5 s
                Log($"underruns={underruns} buffer={Interlocked.Read(ref buffered) * 1000 / sampleRate} ms " +
                    $"drift={(ratio - 1) * 1e6:+0;-0;0} ppm paused={MpvIpc.Paused} objects={slots.Count - 1} " +
                    $"maxHeight={maxHeight:0.00}");
                maxHeight = 0;
            }
        }
    }
}

