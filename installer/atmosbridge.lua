-- AtmosBridge helper for mpv (installed by the AtmosBridge installer, removed on uninstall).
--
-- Shows what the bridge plays (bottom of this file) and decides which audio mpv passes through to the bridge and which it decodes itself:
-- * audio-spdif is only for the bridge. While it has no spatial output (spatial sound off: user-data/atmosbridge-ready
--   false) or isn't there at all, mpv plays through WASAPI as without the bridge and decodes everything: a bitstream
--   the endpoint can't take would leave mpv silent (its PCM fallback opens the decoder but no AO).
-- * Before a file's decoder opens: the bridge's E-AC-3 decoder (Cavern) handles 5.1/7.1 and Atmos (JOC) but not
--   stereo or mono E-AC-3, nor AC-3 at all (Cavern 2.1.0 misparses AC-3 coupling). Those never carry Atmos anyway, so
--   for such a track drop them from audio-spdif: mpv's decoder gives the same channels.
--   ponytail: AC-3 only because of the Cavern bug; pass it through again once a Cavern release fixes it.
-- * "script-message atmosbridge-decode <codecs>" from the bridge, when one of its decoders fails on a stream anyway
--   (manual track switch, TrueHD above 48 kHz, ...): drop those codecs and reopen the decoder.
-- Changes are file-local, so the next file starts from the configured audio-spdif again.

local function split(list)
    local out = {}
    for item in (list or ""):gmatch("[^,]+") do
        out[#out + 1] = item
    end
    return out
end

-- The audio track mpv is about to pick (aid=auto: simplified mpv rules), before track selection runs
local function predicted_audio_track()
    local audio = {}
    for _, t in ipairs(mp.get_property_native("track-list") or {}) do
        if t.type == "audio" then
            audio[#audio + 1] = t
        end
    end
    local aid = mp.get_property("options/aid")
    if #audio == 0 or aid == "no" then
        return nil
    end
    local wanted = tonumber(aid)
    for _, t in ipairs(audio) do
        if t.id == wanted then
            return t
        end
    end
    if wanted then
        return nil
    end
    for _, lang in ipairs(split(mp.get_property("options/alang"))) do
        for _, t in ipairs(audio) do
            if t.lang == lang then
                return t
            end
        end
    end
    for _, t in ipairs(audio) do
        if t.default then
            return t
        end
    end
    return audio[1]
end

local file_spdif -- audio-spdif as configured, taken at this file's start
local refused = {} -- codecs the bridge sent back for this file (atmosbridge-decode)

local function ready()
    return mp.get_property_native("user-data/atmosbridge-ready") == true
end

-- Set audio-spdif for track t: the configured list minus what the bridge can't take, or nothing without the bridge.
-- mpv reopens the decoder and AO on the change by itself (a track reselect on top would reopen the AO twice, the second
-- time before the bridge has its next pipe instance up).
local function apply(t, why)
    local drop = {}
    for c in pairs(refused) do
        drop[c] = true
    end
    local channels = t and t["demux-channel-count"] or 0
    if t and t.codec == "ac3" then
        drop.ac3 = true
    elseif t and t.codec == "eac3" and channels >= 1 and channels <= 2 then
        drop.ac3, drop.eac3 = true, true
    end
    local kept = {}
    for _, c in ipairs(ready() and split(file_spdif) or {}) do
        if not drop[c] then
            kept[#kept + 1] = c
        end
    end
    local spdif = table.concat(kept, ",")
    if spdif == mp.get_property("audio-spdif") then
        return
    end
    mp.set_property("file-local-options/audio-spdif", spdif)
    mp.msg.info(("%s: audio-spdif=%s"):format(why, spdif))
end

-- The bridge sets the flag right after it connects to the IPC, normally before the first file gets here: unset, wait
-- for it briefly, then go on as without the bridge (not running)
mp.add_hook("on_preloaded", 50, function(hook)
    file_spdif = mp.get_property("audio-spdif")
    refused = {}
    local function go()
        apply(predicted_audio_track(), ready() and "AtmosBridge" or "no AtmosBridge output, decoded by mpv")
    end
    if mp.get_property_native("user-data/atmosbridge-ready") ~= nil then
        return go()
    end
    hook:defer()
    local waited = 0
    local timer
    timer = mp.add_periodic_timer(0.05, function()
        waited = waited + 0.05
        if mp.get_property_native("user-data/atmosbridge-ready") ~= nil or waited >= 0.3 then
            timer:kill()
            go()
            hook:cont()
        end
    end)
end)

-- Spatial sound switched off or on mid-file: the playing track gets the matching audio-spdif (the bridge reloads mpv's
-- AO too, for tracks where that changes nothing)
mp.observe_property("user-data/atmosbridge-ready", "native", function(_, r)
    if file_spdif and r ~= nil and mp.get_property("path") then
        apply(mp.get_property_native("current-tracks/audio"), r and "AtmosBridge ready" or "AtmosBridge has no spatial output, decoded by mpv")
    end
end)

-- audio-stream-silence: while paused (and before playback starts) mpv writes silence to an untimed AO as fast as it
-- takes it (GBs into a file); the bridge would queue it and play everything after resume that much later. It is read
-- when the AO opens, too late to change once the pcm AO is up (ao-reload then loses the audio), so turn it off up
-- front when the bridge comes first in the ao list. The WASAPI fallback goes without it.
if (mp.get_property("ao") or ""):match("^pcm") and mp.get_property_bool("audio-stream-silence") then
    mp.set_property_bool("audio-stream-silence", false)
    mp.msg.info("audio-stream-silence off: AtmosBridge's pcm AO would get silence as fast as it takes it")
end

-- video-sync=display-*: display sync judges A/V sync from the pcm AO, which reports no delay and is throttled by whole
-- audio frames, so av_diff swings by up to a frame and display sync drops and repeats frames (jitter). The throttle
-- already keeps audio in step with video and the bridge corrects the drift to the DAC, so drop only mpv's correction:
-- display-resample-desync keeps the vsync-locked frames and resampled audio. Passthrough is unaffected either way (mpv
-- skips display-resample for spdif). The configured mode comes back once mpv plays elsewhere.
local configured_sync -- while replaced
mp.observe_property("current-ao", "string", function(_, ao)
    if ao ~= "pcm" then
        mp.set_property("user-data/atmosbridge", "") -- the bridge's report (below) is stale once mpv plays elsewhere
    end
    if ao == "pcm" and not configured_sync then
        local sync = mp.get_property("video-sync")
        if sync:match("^display%-") and not sync:match("desync$") then
            configured_sync = sync
            mp.set_property("video-sync", "display-resample-desync")
            mp.msg.info(("video-sync=%s -> display-resample-desync while audio goes to AtmosBridge"):format(sync))
        end
    elseif ao and ao ~= "pcm" and configured_sync then
        mp.set_property("video-sync", configured_sync)
        configured_sync = nil
    end
end)

mp.register_script_message("atmosbridge-decode", function(list)
    for _, c in ipairs(split(list)) do
        refused[c] = true
    end
    apply(mp.get_property_native("current-tracks/audio"), "bridge can't decode " .. list)
end)

-- What the bridge actually plays ("TrueHD Atmos → 15 objects + LFE", "DTS-HD MA 5.1 → 5.1 (6 channels)"): it sets user-data/atmosbridge once audio reaches the
-- spatial output and whenever the layout changes. "script-binding atmosbridge/info" shows it for 4 s in the bottom-left
-- corner, clear of the stats overlay (top-left), whose lines come from mpv's own read-only properties. To get it with
-- stats, in input.conf (the installer adds it):
--   i script-binding stats/display-stats; script-binding atmosbridge/info
local overlay = mp.create_osd_overlay("ass-events")
local hide = mp.add_timeout(4, function() overlay:remove() end, true)
local function show_info()
    local info = mp.get_property_native("user-data/atmosbridge", "")
    overlay.data = ([[{\an1\pos(20,700)\fs20\bord1.5}{\b1}AtmosBridge:{\b0} %s]]):format(info ~= "" and info or "not playing")
    overlay:update()
    hide:kill()
    hide:resume()
end
mp.observe_property("user-data/atmosbridge", "native", function(_, info)
    if type(info) == "string" and info ~= "" then
        mp.msg.info(info)
        if hide:is_enabled() then -- on screen: keep it current
            show_info()
        end
    end
end)
mp.add_key_binding(nil, "info", show_info)
