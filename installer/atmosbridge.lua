-- AtmosBridge helper for mpv / mpv.net (installed by the AtmosBridge installer, removed on uninstall).
--
-- Decides which audio mpv passes through to the bridge and which it decodes itself:
-- * Before a file's decoder opens: the bridge's E-AC-3 decoder (Cavern) handles 5.1/7.1 and Atmos (JOC) but not
--   stereo or mono (E-)AC-3. Those never carry Atmos anyway, so for such a track drop ac3/eac3 from audio-spdif.
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

-- Remove codecs from audio-spdif for this file; true if anything changed
local function decode_in_mpv(codecs, why)
    local drop = {}
    for _, c in ipairs(codecs) do
        drop[c] = true
    end
    local all = split(mp.get_property("audio-spdif"))
    local kept = {}
    for _, c in ipairs(all) do
        if not drop[c] then
            kept[#kept + 1] = c
        end
    end
    if #kept == #all then
        return false
    end
    local rest = table.concat(kept, ",")
    mp.set_property("file-local-options/audio-spdif", rest)
    mp.msg.info(("%s: decoded by mpv, not passed to AtmosBridge (audio-spdif=%s)"):format(why, rest))
    return true
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

mp.add_hook("on_preloaded", 50, function()
    local t = predicted_audio_track()
    local channels = t and t["demux-channel-count"] or 0
    if t and (t.codec == "ac3" or t.codec == "eac3") and channels >= 1 and channels <= 2 then
        decode_in_mpv({ "ac3", "eac3" }, ("%s %d ch"):format(t.codec, channels))
    end
end)

mp.register_script_message("atmosbridge-decode", function(list)
    if decode_in_mpv(split(list), "bridge can't decode " .. list) then
        -- the new audio-spdif applies when the decoder reopens: reselect the track, both switches in one command so
        -- mpv doesn't see "no audio" in between (an audio-only file would end)
        local aid = mp.get_property_number("aid")
        if aid then
            mp.command(("no-osd set aid no; no-osd set aid %d"):format(aid))
        end
    end
end)
