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
    public int Length => Samples.Length == 0 ? 0 : Samples[0].Length;
}

static class Program {
    const int sampleRate = 48000; // Windows spatial audio object format
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
            // .ec3 goes through the same decoder setup as live passthrough
            // .wav: a capture of mpv's ao=pcm spdif output (--ao-pcm-file=x.wav), unwrapped like the live pipe
            (Renderer renderer, int rate) = file.EndsWith(".ec3", StringComparison.OrdinalIgnoreCase) ? OpenEac3(File.OpenRead(file))
                : file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? OpenSpdifCapture(file)
                : OpenFile(file);
            static (Renderer, int) OpenSpdifCapture(string path) {
                var input = new BufferedStream(File.OpenRead(path));
                WaveFormat.Read(input);
                return OpenEac3(new Iec61937Stream(input));
            }
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
        new Thread(() => MpvIpc.Run(ipcName, () => running, Log)) { IsBackground = true }.Start();

        if (file != null && file.EndsWith(".thd", StringComparison.OrdinalIgnoreCase)) {
            Pump(TrueHdSource.Read(File.OpenRead(file), blockSize, roomScale, Log));
        } else if (file != null) {
            Pump(CavernChunks(() => OpenFile(file), () => false));
        } else {
            while (running) {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.None, 4096, 0);
                Console.WriteLine($@"Waiting for mpv on \\.\pipe\{pipeName} ...");
                pipe.WaitForConnection();
                var input = new BufferedStream(pipe, 16384);
                try {
                    WaveFormat format = WaveFormat.Read(input);
                    if (format.IsSpdif) {
                        Console.WriteLine($"[{clock.Elapsed.TotalSeconds:0.00}] mpv connected: E-AC-3 passthrough");
                        var iec = new Iec61937Stream(input);
                        Pump(CavernChunks(() => OpenEac3(iec), () => iec.EndOfInput));
                    } else if (format.IsHbr) {
                        Console.WriteLine($"[{clock.Elapsed.TotalSeconds:0.00}] mpv connected: TrueHD passthrough");
                        Pump(TrueHdSource.Read(new Iec61937Stream(input), blockSize, roomScale, Log));
                    } else if (format.SampleRate == sampleRate && format.Bits is 8 or 16 or 32) {
                        Console.WriteLine($"[{clock.Elapsed.TotalSeconds:0.00}] mpv connected: PCM {format}");
                        Pump(PcmSource.Read(input, format, blockSize, roomScale));
                    } else {
                        Console.WriteLine($"Unsupported PCM {format}: set audio-samplerate={sampleRate} in mpv");
                    }
                } catch (Exception e) when (e is IOException || e is EndOfStreamException || !pipe.IsConnected) {
                    // mpv closed the pipe (stop, track change)
                } catch (Exception e) {
                    Log("Decode error: " + e);
                }
                Console.WriteLine($"[{clock.Elapsed.TotalSeconds:0.00}] mpv disconnected");
            }
        }
        running = false;
        renderThread.Join(500);
        return 0;
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
    /// E-AC-3 from mpv's passthrough. Built directly instead of via EnhancedAC3Reader: its stream constructor divides
    /// by a frame size it measures from offsets inside its read blocks, which is 0 when frames sit on block boundaries
    /// (e.g. one 5.1 JOC frame per IEC burst). Small blocks also keep decode latency low.
    /// </summary>
    static (Renderer, int) OpenFile(string path) {
        AudioReader reader = AudioReader.Open(path);
        return (reader.GetRenderer(), reader.SampleRate);
    }

    static (Renderer, int) OpenEac3(Stream raw) {
        var decoder = new EnhancedAC3Decoder(BlockBuffer<byte>.Create(raw, 1024));
        return (new EnhancedAC3Renderer(decoder), decoder.SampleRate);
    }

    /// <summary>
    /// Pull object blocks from Cavern until the input ends.
    /// </summary>
    static IEnumerable<Chunk> CavernChunks(Func<(Renderer renderer, int rate)> open, Func<bool> ended) {
        (Renderer renderer, int rate) = open();
        if (rate != sampleRate) {
            throw new NotSupportedException($"Stream is {rate} Hz, spatial engine wants {sampleRate} Hz");
        }
        IReadOnlyList<Source> objects = renderer.Objects;
        Console.WriteLine($"[{clock.Elapsed.TotalSeconds:0.00}] Decoding: {objects.Count} objects, has objects (JOC): {renderer.HasObjects}");
        Vector3 roomInverse = Vector3.One / Listener.EnvironmentSize;

        while (!ended()) {
            float[][] samples = renderer.GetNextObjectSamples(blockSize);
            var chunk = new Chunk(new float[objects.Count][], new Vector3[objects.Count], new float[objects.Count], new bool[objects.Count]);
            for (int i = 0; i < objects.Count; i++) {
                chunk.Samples[i] = (float[])samples[i].Clone(); // ponytail: allocates ~2 MB/s, pool if GC shows up
                Vector3 p = objects[i].Position * roomInverse; // Cavern: x right, y up, z front, room cube [-1, 1]
                chunk.Positions[i] = new Vector3(p.X, p.Y, -p.Z) * roomScale; // Windows: -z is front
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
        var wire = new MemoryStream();
        wire.Write([0xAA, 0x72]); // junk incl. a partial sync byte
        foreach (byte[] payload in new[] { frameA, frameB }) {
            wire.Write([0x72, 0xF8, 0x1F, 0x4E, 21, 0, (byte)payload.Length, 0]);
            for (int i = 0; i < payload.Length; i += 2) {
                wire.Write([payload[i + 1], payload[i]]); // spdif words are byte-swapped
            }
            wire.Write(new byte[64]);
        }
        wire.Write([0x72, 0xF8, 0x1F, 0x4E, 0, 0, 4, 0, 0, 0, 0, 0]); // pause burst (type 0) is skipped
        wire.Position = 0;

        var iec = new Iec61937Stream(wire);
        byte[] expected = [.. frameA, .. frameB], got = new byte[expected.Length];
        bool ok = iec.Length == frameA.Length && iec.Read(got, 0, got.Length) == got.Length && got.SequenceEqual(expected)
            && iec.Read(new byte[1], 0, 1) == 0 && iec.EndOfInput;
        Console.WriteLine(ok ? "IEC 61937 OK" : "IEC 61937 FAILED: " + Convert.ToHexString(got));

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
                Console.WriteLine($"[{clock.Elapsed.TotalSeconds:0.00}] Spatial endpoint {output.DeviceId}: {output.MaxDynamicObjects} dynamic objects, {output.SampleRate} Hz");
                if (output.SampleRate != sampleRate) {
                    Console.WriteLine($"Warning: spatial engine runs at {output.SampleRate} Hz, bridge assumes {sampleRate} Hz");
                }
                RenderLoop(output);
            } catch (Exception e) {
                Console.WriteLine($"[{clock.Elapsed.TotalSeconds:0.00}] Spatial output unavailable: {e.Message}");
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
        bool started = false, prebuffering = true;
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

        while (running) {
            if (!started) {
                SpatialOutput.Check(stream.Start(), "Start");
                started = true;
            }
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
                        Vector3 p = current.Positions[i];
                        slots[slot].SetPosition(p.X, p.Y, p.Z);
                        if (p.Y > maxHeight && current.Samples[i].Any(x => Math.Abs(x) > 1e-4f)) {
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

