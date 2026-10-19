using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace AtmosBridge;

/// <summary>
/// Follows mpv's pause and seeks over its JSON IPC (input-ipc-server), reconnecting forever, and sends it commands.
/// </summary>
static class MpvIpc {
    public static volatile bool Paused;
    public static event Action Seek, PlaybackRestart, Connected;

    static volatile StreamWriter writer;
    static readonly object sendLock = new();
    static readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> pending = new();
    static int nextRequest;

    public static void Run(string pipeName, Func<bool> running, Action<string> log) {
        while (running()) {
            bool connected = false;
            try {
                // Asynchronous (overlapped) handle: with a synchronous one, Send blocks until the pending ReadLine returns
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(1000);
                using var reader = new StreamReader(pipe);
                writer = new StreamWriter(pipe) { AutoFlush = true };
                Send("""{ "command": ["observe_property", 1, "pause"] }""");
                connected = true;
                log($@"mpv IPC connected (\\.\pipe\{pipeName})");
                Connected?.Invoke();
                while (reader.ReadLine() is string line) {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("request_id", out JsonElement id) && id.TryGetInt32(out int request)
                        && pending.TryRemove(request, out var reply)) {
                        bool ok = root.TryGetProperty("error", out JsonElement error) && error.GetString() == "success";
                        reply.SetResult(ok && root.TryGetProperty("data", out JsonElement data) ? data.Clone() : (ok ? default(JsonElement) : null));
                        continue;
                    }
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
            } catch (Exception e) when (e is TimeoutException || e is IOException || e is JsonException || e is ObjectDisposedException) {
            }
            writer = null;
            foreach (int request in pending.Keys) {
                if (pending.TryRemove(request, out var reply)) {
                    reply.TrySetResult(null);
                }
            }
            if (connected) {
                log("mpv IPC disconnected");
            }
            Paused = false;
            PlaybackRestart?.Invoke(); // never stay in seek-discard without mpv
            Thread.Sleep(1000);
        }
    }

    static void Send(string json) {
        lock (sendLock) {
            writer?.WriteLine(json);
        }
    }

    /// <summary>
    /// Run an mpv command and wait for its result: the reply's data (default when it has none), null on error/timeout.
    /// </summary>
    public static JsonElement? Request(params object[] command) {
        if (writer == null) {
            return null;
        }
        int id = Interlocked.Increment(ref nextRequest);
        var reply = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        try {
            Send(JsonSerializer.Serialize(new { command, request_id = id }));
        } catch (IOException) {
            pending.TryRemove(id, out _);
            return null;
        }
        if (reply.Task.Wait(2000)) {
            return reply.Task.Result;
        }
        pending.TryRemove(id, out _);
        return null;
    }

    /// <summary>
    /// Two commands in one write, then wait for both replies.
    /// </summary>
    static bool RequestPair(object[] first, object[] second) {
        if (writer == null) {
            return false;
        }
        var replies = new List<Task<JsonElement?>>();
        var lines = new List<string>();
        foreach (object[] command in new[] { first, second }) {
            int id = Interlocked.Increment(ref nextRequest);
            var reply = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = reply;
            replies.Add(reply.Task);
            lines.Add(JsonSerializer.Serialize(new { command, request_id = id }));
        }
        try {
            Send(string.Join("\n", lines));
        } catch (IOException) {
            return false;
        }
        return Task.WaitAll([.. replies], 2000) && replies.All(r => r.Result != null);
    }

    /// <summary>
    /// Stop passing these codecs through for the current file (mpv decodes them to PCM instead). file-local-options
    /// resets by itself on the next file. False without IPC or when nothing would change.
    /// </summary>
    public static bool DecodeInMpv(string[] codecs, Action<string> log) {
        // A decode error comes right after mpv starts, maybe before this client has (re)connected to it
        for (int waited = 0; writer == null && waited < 3000; waited += 50) {
            Thread.Sleep(50);
        }
        if (Request("get_property", "audio-spdif") is not JsonElement current || current.ValueKind != JsonValueKind.String) {
            return false;
        }
        string[] all = current.GetString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string rest = string.Join(",", all.Where(c => !codecs.Contains(c, StringComparer.OrdinalIgnoreCase)));
        if (rest.Length == string.Join(",", all).Length) {
            return false;
        }
        // The new value only applies when the decoder reopens. With video: reselect the audio track. Without video
        // that would end the file (aid=no leaves nothing to play), so reload the entry in place: insert the same file
        // after it with the new option and the current position, then drop the current entry.
        bool ok;
        if (Request("get_property", "vid") is JsonElement { ValueKind: JsonValueKind.Number }) {
            ok = Request("set_property", "file-local-options/audio-spdif", rest) != null
                && Request("get_property", "aid") is JsonElement aid && aid.ValueKind == JsonValueKind.Number
                && RequestPair(["set_property", "aid", "no"], ["set_property", "aid", aid.GetInt32()]);
        } else {
            double position = Request("get_property", "time-pos") is JsonElement { ValueKind: JsonValueKind.Number } t ? t.GetDouble() : 0;
            ok = Request("get_property", "path") is JsonElement { ValueKind: JsonValueKind.String } path
                && Request("loadfile", path.GetString(), "insert-next", -1,
                    $"audio-spdif=%{rest.Length}%{rest},start={position.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}") != null
                && Request("playlist-remove", "current") != null;
        }
        log(ok ? $"asked mpv to decode {string.Join("/", codecs)} itself for this file (audio-spdif={rest})"
            : "mpv refused the audio-spdif change");
        return ok;
    }
}
