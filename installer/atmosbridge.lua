-- AtmosBridge helper for mpv / mpv.net (installed by the AtmosBridge installer, removed on uninstall).
--
-- The bridge's E-AC-3 decoder (Cavern) handles 5.1/7.1 and Atmos (JOC) streams but not stereo or mono (E-)AC-3.
-- Those never carry Atmos anyway, so let mpv decode them itself: before the decoder opens, look at the audio track
-- mpv is about to pick and, for 1-2 channel AC-3/E-AC-3, drop ac3/eac3 from audio-spdif for this file only.
-- (The bridge still asks mpv to fall back over IPC if a stream fails anyway, e.g. after a manual track switch.)

local function predicted_audio_track()
    local tracks = mp.get_property_native("track-list") or {}
    local audio = {}
    for _, t in ipairs(tracks) do
        if t.type == "audio" then
            audio[#audio + 1] = t
        end
    end
    if #audio == 0 then
        return nil
    end
    local aid = mp.get_property("options/aid")
    if aid == "no" then
        return nil
    end
    local wanted = tonumber(aid)
    if wanted then
        for _, t in ipairs(audio) do
            if t.id == wanted then
                return t
            end
        end
        return nil
    end
    -- aid=auto, simplified mpv rules: first track in a preferred language, else the default track, else the first
    for lang in (mp.get_property("options/alang") or ""):gmatch("[^,]+") do
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
    if not t or (t.codec ~= "ac3" and t.codec ~= "eac3") then
        return
    end
    local channels = t["demux-channel-count"] or 0
    if channels == 0 or channels > 2 then
        return
    end
    local spdif = mp.get_property("audio-spdif") or ""
    local kept = {}
    for codec in spdif:gmatch("[^,]+") do
        if codec ~= "ac3" and codec ~= "eac3" then
            kept[#kept + 1] = codec
        end
    end
    local rest = table.concat(kept, ",")
    if rest ~= spdif then
        mp.set_property("file-local-options/audio-spdif", rest)
        mp.msg.info(("%s %d ch: decoded by mpv, not passed to AtmosBridge (audio-spdif=%s)"):format(t.codec, channels, rest))
    end
end)
