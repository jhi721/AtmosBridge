using System.IO.Pipes;
using System.Text.Json;

namespace AtmosBridge;

/// <summary>
/// Follows mpv over its JSON IPC (input-ipc-server): pause, seeks and the audio output in use, reconnecting forever.
/// Commands are fire-and-forget; one sent while disconnected goes out on the next connection (if still fresh).
/// </summary>
static class MpvIpc {
    public static volatile bool Paused;

    /// <summary>
    /// mpv's audio output driver ("pcm" = the bridge's pipe, "wasapi", ...), null while unknown.
    /// </summary>
    public static volatile string CurrentAo;

    public static event Action Seek, PlaybackRestart, AoChanged;

    static StreamWriter writer;
    static readonly object sendLock = new();
    static (string json, long at)? queued;

    public static void Run(string pipeName, Func<bool> running, Action<string> log) {
        while (running()) {
            bool connected = false;
            try {
                // Asynchronous (overlapped) handle: with a synchronous one, a write blocks until the pending ReadLine returns
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(1000);
                using var reader = new StreamReader(pipe);
                lock (sendLock) {
                    writer = new StreamWriter(pipe) { AutoFlush = true };
                    writer.WriteLine("""{ "command": ["observe_property", 1, "pause"] }""");
                    writer.WriteLine("""{ "command": ["observe_property", 2, "current-ao"] }""");
                    if (queued is (string json, long at) && Environment.TickCount64 - at < 5000) {
                        writer.WriteLine(json);
                    }
                    queued = null;
                }
                connected = true;
                log($@"mpv IPC connected (\\.\pipe\{pipeName})");
                while (reader.ReadLine() is string line) {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("event", out JsonElement e)) {
                        continue;
                    }
                    switch (e.GetString()) {
                        case "seek":
                            Seek?.Invoke();
                            break;
                        case "playback-restart":
                            PlaybackRestart?.Invoke();
                            break;
                        case "property-change":
                            root.TryGetProperty("data", out JsonElement d);
                            switch (root.GetProperty("name").GetString()) {
                                case "pause":
                                    Paused = d.ValueKind == JsonValueKind.True;
                                    break;
                                case "current-ao":
                                    CurrentAo = d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                                    AoChanged?.Invoke();
                                    break;
                            }
                            break;
                    }
                }
            } catch (Exception e) when (e is TimeoutException || e is IOException || e is JsonException || e is ObjectDisposedException) {
            }
            lock (sendLock) {
                writer = null;
            }
            if (connected) {
                log("mpv IPC disconnected");
            }
            Paused = false;
            CurrentAo = null;
            PlaybackRestart?.Invoke(); // never stay in seek-discard without mpv
            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// Send an mpv command, or keep it for the next connection.
    /// </summary>
    public static void Command(params object[] command) {
        string json = JsonSerializer.Serialize(new { command });
        lock (sendLock) {
            try {
                if (writer != null) {
                    writer.WriteLine(json);
                    return;
                }
            } catch (Exception e) when (e is IOException || e is ObjectDisposedException) {
            }
            queued = (json, Environment.TickCount64);
        }
    }
}
