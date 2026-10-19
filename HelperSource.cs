using System.Diagnostics;
using System.Numerics;

namespace AtmosBridge;

/// <summary>
/// Decoding helper process: the raw bitstream goes to its stdin, decoded channels with positions come back on its
/// stdout. thd-objects (Rust, truehd crate) for TrueHD, dtsx-objects (C++, DTSX_decoder) for DTS.
/// See thd-objects/src/main.rs for the framing.
/// </summary>
static class HelperSource {
    public const string TrueHd = "thd-objects.exe", Dts = "dtsx-objects.exe";

    public static IEnumerable<Chunk> Read(string exe, Stream input, int blockSize, Action<string> log) {
        var helper = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, exe)) {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        });
        helper.ErrorDataReceived += (_, e) => {
            if (e.Data != null) {
                log(e.Data);
            }
        };
        helper.BeginErrorReadLine();
        new Thread(() => {
            // Small reads: the IEC unwrapper blocks until a request is full, which would add latency
            byte[] buffer = new byte[4096];
            Stream stdin = helper.StandardInput.BaseStream;
            try {
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0) {
                    stdin.Write(buffer, 0, n);
                    stdin.Flush();
                }
            } catch (IOException) { // mpv or the helper went away
            } finally {
                try { stdin.Close(); } catch (IOException) { }
            }
        }) { IsBackground = true }.Start();

        try {
            Stream stdout = helper.StandardOutput.BaseStream;
            byte[] au = new byte[8 + 16 * 17 + 160 * 16 * 4];
            var pending = new List<float>[0];
            int pendingLength = 0;
            while (stdout.ReadAtLeast(au.AsSpan(0, 8), 8, false) == 8) {
                int channels = BitConverter.ToUInt16(au, 0), samples = BitConverter.ToUInt16(au, 2), rate = BitConverter.ToInt32(au, 4);
                int size = channels * 17 + samples * channels * 4;
                if (au.Length < 8 + size) { // DTS frames are up to 4096 samples
                    Array.Resize(ref au, 8 + size);
                }
                if (stdout.ReadAtLeast(au.AsSpan(8, size), size, false) != size) {
                    yield break; // helper killed mid-frame
                }
                if (rate != Program.sampleRate) {
                    throw new NotSupportedException($"Stream at {rate} Hz, spatial engine wants {Program.sampleRate} Hz");
                }
                if (pending.Length != channels) { // layout change: start over
                    pending = Enumerable.Range(0, channels).Select(_ => new List<float>(blockSize)).ToArray();
                    pendingLength = 0;
                }
                for (int i = 0, o = 8 + channels * 17; i < samples; i++) {
                    for (int ch = 0; ch < channels; ch++, o += 4) {
                        pending[ch].Add(BitConverter.ToSingle(au, o));
                    }
                }
                pendingLength += samples;
                if (pendingLength >= blockSize) { // 12 access units of 40 samples; positions of the last one
                    var chunk = new Chunk(channels);
                    for (int ch = 0, o = 8; ch < channels; ch++, o += 17) {
                        chunk.Positions[ch] = new Vector3(BitConverter.ToSingle(au, o), BitConverter.ToSingle(au, o + 4), BitConverter.ToSingle(au, o + 8));
                        chunk.Volumes[ch] = BitConverter.ToSingle(au, o + 12);
                        chunk.Lfe[ch] = au[o + 16] != 0;
                        chunk.Samples[ch] = [.. pending[ch]];
                        pending[ch].Clear();
                    }
                    pendingLength = 0;
                    yield return chunk;
                }
            }
        } finally {
            try { helper.Kill(); } catch (InvalidOperationException) { }
            helper.Dispose();
        }
    }
}
