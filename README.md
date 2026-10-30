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
DTS, DTS-HD and DTS:X are passed through too (`audio-spdif=dts-hd`, IEC 61937 DTS bursts). The bridge strips the
burst framing and pipes the raw DTS into `dtsx-objects.exe`, a C++ helper built on
[DTSX_decoder](https://github.com/almirus/DTSX_decoder) (git submodule): libdcadec decodes the core / DTS-HD bed,
DTSX_decoder decodes the DTS:X object waveforms and metadata, removes their fold-down from the bed and pans them
with its renderer into 7.1.4. The 12 channels go to Windows as one object per speaker.
Any other codec (AAC, FLAC, ...) arrives as PCM and is sent as one object per speaker position.

## Install

Download `AtmosBridge-<version>-setup.exe` from Releases (or the `build` workflow artifact) and run it. It installs
per user (no admin) to `%LOCALAPPDATA%\Programs\AtmosBridge`, self-contained (no .NET runtime needed), and optionally:

- starts the bridge at sign-in (Startup shortcut, log in `%LOCALAPPDATA%\AtmosBridge\log.txt`);
- adds the mpv settings below to `%APPDATA%\mpv.net\mpv.conf` and/or `%APPDATA%\mpv\mpv.conf`, between
  `# >>> AtmosBridge` / `# <<< AtmosBridge` markers (first run keeps a `.bak-atmosbridge` copy), and the companion
  script `scripts\atmosbridge.lua` next to it. Uninstall removes both again.

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

`video-sync=display-resample` can't work as is on the untimed AO: mpv sees no output delay and throttles by whole
audio frames, so its A/V difference swings by up to a frame and display sync keeps dropping and repeating frames.
`atmosbridge.lua` switches any such `display-*` mode to `display-resample-desync` while the pcm AO is active
(vsync-locked frames and resampled audio as before, without mpv's A/V correction, which the throttle and the bridge's
drift control make redundant), and back when mpv plays elsewhere. Passthrough never used it: mpv skips
display-resample for spdif. It also turns `audio-stream-silence` off when `ao` starts with `pcm`: mpv would write
silence into the untimed AO as fast as it takes it while paused, and the bridge would play it after resume.

Over mpv's JSON IPC (`input-ipc-server`) the bridge freezes the output on pause and flushes it on
seek, then prebuffers again. Without IPC it still plays, but pause/seek leave ~0.1 s of stale audio.

End to end (bridge cushion + Windows spatial engine + Dolby renderer) the sound leaves ~0.26 s after mpv writes
it (`--latency` + ~0.16 s); `audio-delay=-0.26` lines it up with the picture (adjust it together with `--latency`). Measured with beep test videos and WASAPI loopback: within
-19 ... +33 ms on the PCM, E-AC-3, TrueHD and DTS paths. Pause reaches the output after ~0.23 s, resume ~0.22 s.

## Fallbacks

- Stereo / mono AC-3 and E-AC-3 (no Atmos possible; Cavern can't decode them): `atmosbridge.lua` drops ac3/eac3
  from `audio-spdif` for that file before the decoder opens, so mpv decodes it and the bridge gets PCM.
- Any passthrough stream a decoder still fails on (e.g. after a manual track switch, TrueHD above 48 kHz): the
  bridge sends `script-message atmosbridge-decode <codecs>` over IPC; the script drops them from `audio-spdif` for
  this file and reselects the track, the bridge keeps the pipe flowing meanwhile and takes the PCM on mpv's
  reconnect. A hand-written mpv.conf without `atmosbridge.lua` gets no such fallback (the stream stays silent).
- mpv started before the bridge (fell back to WASAPI), or the bridge restarted mid-file: once the bridge has a
  spatial output and sees mpv over IPC, it sends `ao-reload` so mpv reopens its AO and lands on the pipe again.
- Bridge not running at all: `ao=pcm,wasapi` plays normally through WASAPI, without the bridge's `audio-delay`.

## Run

```
cd thd-objects && cargo build -r && cd ..     # TrueHD helper (Rust >= 1.88), copied next to the exe by the build
git submodule update --init                   # DTS helper sources (DTSX_decoder)
cmake -S dtsx-objects -B dtsx-objects/build && cmake --build dtsx-objects/build --config Release   # MSVC
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
| `--file x.ec3` | | play a file directly (test); `.thd` (raw TrueHD) goes through thd-objects, `.dts` / `.dtshd` through dtsx-objects |
| `--selftest` | | IEC 61937 (E-AC-3, AC-3, DTS, MAT) unpacker, passthrough detection, resampler and PCM layout checks |
| `--dump --file x.ec3` | | print every object's position and level once per second (diagnostics) |

Login startup: `Startup\AtmosBridge.lnk` runs the windowless build in `dist\` (`dotnet publish -c Release -o dist
-p:OutputType=WinExe`) with `--log %LOCALAPPDATA%\AtmosBridge\log.txt`. If the device is busy or spatial sound is
off, it retries every 3 s and drops audio meanwhile so mpv doesn't stall.

## mpv.conf

```
ao=pcm,wasapi                 # bridge first, normal WASAPI when the bridge isn't running
audio-spdif=eac3,truehd,dts-hd
ao-pcm-file=\\.\pipe\atmos
audio-samplerate=48000        # PCM path needs the spatial engine's rate
audio-channels=auto           # the bridge takes any layout; a fixed list would fold height channels down
input-ipc-server=\\.\pipe\mpv-atmos

# measured end-to-end latency, only when audio goes to the bridge
[atmos-bridge-delay]
profile-cond=get("current-ao") == "pcm"
profile-restore=copy
audio-delay=-0.26
```

## Limits

- TrueHD: 48 kHz only. 96/192 kHz TrueHD (some music discs) fails in the helper; the bridge then has mpv decode it
  (7.1 core, no objects). OAMD is applied per access unit (0.83 ms); `sample_offset` and ramps
  are ignored, and multi-update-block or ISF-object streams are untested. After a seek (seen over IPC) the bridge
  restarts the helper, which picks up at the next major sync (a few ms of audio dropped, already discarded by the
  seek flush).
- TrueHD buffer settles slower than E-AC-3: right after start it sits ~50 ms under `--latency` and the drift
  controller takes a minute or two to fill it, so lip sync is ~50 ms early meanwhile.
- DTS: 48 kHz only, like TrueHD. DTS:X objects arrive pre-rendered to 7.1.4, not as dynamic objects. DTS-UHD (DTS:X
  P2, streaming) is not supported: DTSX_decoder handles it only through DTS's own DTSXDecoder.dll, left out here.
  14-bit DTS (DTS-CD) stays silent. After a seek (seen over IPC) the bridge restarts the helper; without IPC the
  helper restarts on the first frame it can't decode. DTSX_decoder is experimental and licensed PolyForm
  Noncommercial 1.0.0; libdcadec is LGPL-2.1.
- `audio-delay=-0.26` was measured with Dolby Atmos for Headphones; another spatial renderer (Windows Sonic) has
  its own latency, fine-tune with Ctrl+plus/minus.
- Several mpv instances at once: only the first gets the IPC pipe and the audio pipe.
- Cavern licence: free, non-commercial use. truehd/truehdd: Apache-2.0, a research implementation of TrueHD/OAMD
  "not intended for production environments or consumer playback systems" (its README); Dolby patents may apply.
