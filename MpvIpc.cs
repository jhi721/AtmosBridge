using System.IO.Pipes;
using System.Text.Json;

namespace AtmosBridge;

/// <summary>
/// Follows mpv's pause and seeks over its JSON IPC (input-ipc-server), reconnecting forever.
/// </summary>
static class MpvIpc {
    public static volatile bool Connected, Paused;
    public static event Action Seek, PlaybackRestart;

    public static void Run(string pipeName, Func<bool> running, Action<string> log) {
        while (running()) {
            try {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                pipe.Connect(1000);
                using var reader = new StreamReader(pipe);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                writer.WriteLine("""{ "command": ["observe_property", 1, "pause"] }""");
                Connected = true;
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
                        case "property-change" when root.GetProperty("name").GetString() == "pause":
                            Paused = root.TryGetProperty("data", out JsonElement d) && d.ValueKind == JsonValueKind.True;
                            break;
                    }
                }
            } catch (Exception e) when (e is TimeoutException || e is IOException || e is JsonException) {
            }
            if (Connected) {
                log("mpv IPC disconnected");
            }
            Connected = Paused = false;
            PlaybackRestart?.Invoke(); // never stay in seek-discard without mpv
            Thread.Sleep(1000);
        }
    }
}
