using System.ComponentModel;
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

    /// <summary>
    /// The running helper. A seek replaces it, so no decoder state from before the jump survives (DTS XLL and object
    /// state, the core frame plain DTS holds back, TrueHD's resync).
    /// </summary>
    sealed class Session(string exe, Action<string> log) {
        public volatile Process Current;
        public volatile bool InputEnded;
        volatile bool restarting;

        /// <summary>
        /// Whether this helper was (or is being) replaced after a seek.
        /// </summary>
        public bool Replaced(Process helper) => restarting || helper != Current;

        public void Start() {
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
            Current = helper;
        }

        /// <summary>
        /// mpv seeked: end the current helper; its output ends and the reader starts the next one.
        /// </summary>
        public void Restart() {
            lock (this) {
                restarting = true;
                Kill(Current);
            }
        }

        /// <summary>
        /// The current helper's output ended: start the next one if that was a seek and input still comes.
        /// </summary>
        public bool Next() {
            lock (this) {
                if (!restarting || InputEnded) {
                    return false;
                }
                restarting = false;
                Current.Dispose();
                Start();
                log($"{exe} restarted after seek");
                return true;
            }
        }

        public static void Kill(Process helper) {
            try {
                helper.Kill();
            } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { // already gone
            }
        }
    }

    public static IEnumerable<Chunk> Read(string exe, Stream input, int blockSize, Action<string> log) {
        var session = new Session(exe, log);
        session.Start();
        MpvIpc.Seek += session.Restart;
        new Thread(() => Feed(input, session)) { IsBackground = true }.Start();

        try {
            byte[] au = new byte[8 + 16 * 17 + 160 * 16 * 4];
            do {
                Stream stdout = session.Current.StandardOutput.BaseStream;
                var pending = new List<float>[0];
                int pendingLength = 0;
                while (stdout.ReadAtLeast(au.AsSpan(0, 8), 8, false) == 8) {
                    int channels = BitConverter.ToUInt16(au, 0), samples = BitConverter.ToUInt16(au, 2), rate = BitConverter.ToInt32(au, 4);
                    int size = channels * 17 + samples * channels * 4;
                    if (au.Length < 8 + size) { // DTS frames are up to 4096 samples
                        Array.Resize(ref au, 8 + size);
                    }
                    if (stdout.ReadAtLeast(au.AsSpan(8, size), size, false) != size) {
                        break; // helper killed mid-frame
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
            } while (session.Next());
        } finally {
            MpvIpc.Seek -= session.Restart;
            Session.Kill(session.Current);
            session.Current.Dispose();
        }
    }

    /// <summary>
    /// Copy the bitstream into whichever helper is running. The IEC unwrapper blocks until a request is full, so ask
    /// it for at most what is left of the current burst (its Length): low-rate DTS would otherwise arrive several frames
    /// late. Other inputs (files, raw DTS) return what they have.
    /// </summary>
    static void Feed(Stream input, Session session) {
        byte[] buffer = new byte[4096];
        try {
            int n;
            while ((n = input.Read(buffer, 0, input is Iec61937Stream iec ? (int)Math.Min(buffer.Length, iec.Length) : buffer.Length)) > 0) {
                Process helper = session.Current;
                try {
                    Stream stdin = helper.StandardInput.BaseStream;
                    stdin.Write(buffer, 0, n);
                    stdin.Flush();
                } catch (Exception e) when ((e is IOException or ObjectDisposedException) && session.Replaced(helper)) {
                    // killed (and disposed) for a seek: pre-seek data, discarded anyway
                }
            }
        } catch (Exception e) when (e is IOException or InvalidOperationException) { // mpv or the helper went away
        } finally {
            session.InputEnded = true;
            try {
                session.Current.StandardInput.Close();
            } catch (Exception e) when (e is IOException or InvalidOperationException) {
            }
        }
    }
}
