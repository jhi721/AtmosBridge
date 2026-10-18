# AtmosBridge

mpv → Dolby Atmos for Headphones with real dynamic objects.

mpv passes the DD+ Atmos (E-AC-3 JOC) bitstream through `ao=pcm` into a named pipe.
AtmosBridge decodes it with [Cavern](https://github.com/VoidXH/Cavern) and feeds every
object (audio + position) to Windows Spatial Audio (`ISpatialAudioClient`) as a dynamic
object, so the Dolby renderer places it. LFE goes to the static LFE object.

TrueHD Atmos is passed through too (`audio-spdif=truehd`, IEC 61937 MAT frames in an 8ch 192 kHz stream). The bridge
strips the MAT framing and pipes the raw TrueHD into `thd-objects.exe`, a small Rust helper built on the
[truehd](https://crates.io/crates/truehd) crate (the library behind truehdd): it decodes the 16-channel object
presentation and the OAMD positions/gains per access unit, and the bridge sends those as dynamic objects like E-AC-3 JOC.
Plain (non-Atmos) TrueHD goes the same way, one object per speaker.
Any other codec (AAC, FLAC, ...) arrives as PCM and is sent as one object per speaker position.

## Install

Download `AtmosBridge-<version>-setup.exe` from Releases (or the `build` workflow artifact) and run it. It installs
per user (no admin) to `%LOCALAPPDATA%\Programs\AtmosBridge`, self-contained (no .NET runtime needed), and optionally:

- starts the bridge at sign-in (Startup shortcut, log in `%LOCALAPPDATA%\AtmosBridge\log.txt`);
- adds the mpv settings below to `%APPDATA%\mpv.net\mpv.conf` and/or `%APPDATA%\mpv\mpv.conf`, between
  `# >>> AtmosBridge` / `# <<< AtmosBridge` markers (first run keeps a `.bak-atmosbridge` copy). Uninstall removes
  the block again.

Enable **Dolby Atmos for Headphones** (Windows sound settings → device → Spatial audio) on the output device first.

Release: push a tag, CI builds the installer and attaches it to a GitHub release.

```
git tag v0.1.0 && git push origin v0.1.0
```

## How timing works

`ao=pcm` is an "untimed" AO: mpv writes audio in step with its video clock. The bridge keeps a
fixed cushion (`--latency`, default 0.1 s) between decoder and output and holds it there with a
tiny Catmull-Rom resampling correction (±0.2 % max, typically a few hundred ppm), which absorbs
the drift between mpv's clock and the DAC clock. Without video mpv writes as fast as the bridge
reads, so the queue is capped at 2.5× latency and no correction is applied.

Over mpv's JSON IPC (`input-ipc-server`) the bridge freezes the output on pause and flushes it on
seek, then prebuffers again. Without IPC it still plays, but pause/seek leave ~0.1 s of stale audio.

## Run

```
cd thd-objects && cargo build -r && cd ..     # TrueHD helper (Rust >= 1.88), copied next to the exe by the build
dotnet build -c Release
bin\Release\net10.0-windows\AtmosBridge.exe --device {5384faff-9e6b-4060-8de7-b76e2bbdd825}
```

| Option | Default | |
|---|---|---|
| `--device` | auto | render endpoint id (`mpv --audio-device=help`, part after `wasapi/`). Auto: the default endpoint if spatial sound is on there, else the first active endpoint that has it |
| `--pipe` | `atmos` | audio pipe (`ao-pcm-file=\\.\pipe\atmos`) |
| `--ipc` | `mpv-atmos` | mpv IPC pipe (`input-ipc-server=\\.\pipe\mpv-atmos`) |
| `--latency` | `0.1` | buffer cushion in seconds |
| `--log` | console | log file (windowless build) |
| `--file x.ec3` | | play a file directly (test); `.thd` (raw TrueHD) goes through thd-objects |
| `--selftest` | | IEC 61937 / MAT unpacker + resampler checks |
| `--dump --file x.ec3` | | print every object's position and level once per second (diagnostics) |

Login startup: `Startup\AtmosBridge.lnk` runs the windowless build in `dist\` (`dotnet publish -c Release -o dist
-p:OutputType=WinExe`) with `--log %LOCALAPPDATA%\AtmosBridge\log.txt`. If the device is busy or spatial sound is
off, it retries every 3 s and drops audio meanwhile so mpv doesn't stall.

## mpv.conf

```
ao=pcm,wasapi                 # bridge first, normal WASAPI when the bridge isn't running
audio-spdif=eac3,truehd
ao-pcm-file=\\.\pipe\atmos
audio-samplerate=48000        # PCM path needs the spatial engine's rate
input-ipc-server=\\.\pipe\mpv-atmos

# bridge latency (--latency + ~50 ms decoder/engine), only when audio goes to the bridge
[atmos-bridge-delay]
profile-cond=get("current-ao") == "pcm"
profile-restore=copy
audio-delay=-0.15
```

## Limits

- TrueHD: 48 kHz only. 96/192 kHz TrueHD (some music discs) is rejected with a decode error and stays silent; drop
  `truehd` from `audio-spdif` for those files. OAMD is applied per access unit (0.83 ms); `sample_offset` and ramps
  are ignored, and multi-update-block or ISF-object streams are untested. After a seek the decoder resyncs at the
  next major sync (a few ms of audio dropped, already discarded by the seek flush).
- TrueHD buffer settles slower than E-AC-3: right after start it sits ~50 ms under `--latency` and the drift
  controller takes a minute or two to fill it, so lip sync is ~50 ms early meanwhile.
- `audio-delay=-0.15` is computed, not measured acoustically; fine-tune by ear.
- Several mpv instances at once: only the first gets the IPC pipe and the audio pipe.
- Cavern licence: free, non-commercial use. truehd/truehdd: Apache-2.0, a research implementation of TrueHD/OAMD
  "not intended for production environments or consumer playback systems" (its README); Dolby patents may apply.
