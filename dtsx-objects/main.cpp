// Raw DTS (core, DTS-HD, DTS:X) on stdin -> PCM at speaker positions on stdout, for AtmosBridge: DTS:X as 7.1.4, other
// DTS as its own channels (5.1 stays 5.1).
//
// stderr: log lines, and "format: <name>" ("DTS-HD MA 7.1", "DTS:X", ...) whenever the stream's format changes. No
// "objects: 1": the bridge gets channels, DTS:X objects arrive rendered.
//
// Same framing as thd-objects (see thd-objects/src/main.rs), per decoded frame (little-endian):
//   u16 channels, u16 samples, u32 sample_rate
//   channels x { f32 x, f32 y, f32 z, f32 gain, u8 lfe }   Windows space: x right, y up, -z front
//   samples x channels f32                                interleaved, [-1, 1)
//
// Decoding and object rendering come from DTSX_decoder (submodule). Its frame-level helpers live in an anonymous
// namespace in pipeline.cpp, so this file includes it and runs the steps of its render_object_stream() for the
// 7.1.4 layout one frame at a time: bed with the embedded object fold-down removed, plus objects panned by its
// renderer.
// ponytail: objects reach Windows pre-rendered to 7.1.4, not as dynamic objects; pass ObjectMetadataBlock
// positions through if the 7.1.4 render proves too coarse.
// ponytail: including pipeline.cpp ties this file to upstream internals (anonymous-namespace helpers); after a
// submodule bump rebuild and rerun the DTS:X check. Upgrade path: upstream exports a per-frame render_frame().
#include "app/pipeline.cpp"
#include "dca_context.h" // DCADEC_PROFILE_*

#include <fcntl.h>
#include <io.h>

#include <cstdio>
#include <cstring>

namespace dtsx_decode {

// p2_decoder.cpp embeds DTS's own DTSXDecoder.dll for DTS-UHD (P2) and is left out of the build;
// disc DTS:X is ExSS/XLL and doesn't need it.
bool input_is_dts_uhd(const Options&) {
    return false;
}

void decode_p2_stream(const Options&, const std::filesystem::path&, bool) {
    throw std::runtime_error("DTS-UHD is not supported");
}

void print_support_author() {} // options.cpp's --help donation QR codes (support_qr.cpp is left out)

namespace {

struct Speaker final {
    float x, y, z;
    bool lfe;
};

// 7.1.4 in DTSX_decoder's channel order: FL FR FC LFE BL BR SL SR TFL TFR TBL TBR
constexpr Speaker kSpeakers[] = {
    {-1, 0, -1, false}, {1, 0, -1, false}, {0, 0, -1, false}, {0, 0, 0, true},
    {-1, 0, 1, false},  {1, 0, 1, false},  {-1, 0, 0, false}, {1, 0, 0, false},
    {-1, 1, -1, false}, {1, 1, -1, false}, {-1, 1, 1, false}, {1, 1, 1, false},
};
constexpr std::size_t kChannels = std::size(kSpeakers);

using Planar = std::vector<std::vector<std::int32_t>>;

// A speaker bed mapped to 7.1.4. 7.1.4 has no back centre: a DTS-ES 6.1 Cs (DTS physical bit 6) would be dropped, so
// it goes to both back outputs (BL, BR) at -3 dB.
bool map_bed(const DecodedObjectAudioFrame& decoded, const ChannelLayout& layout, Planar& out) {
    if (!map_xll_bed_to_layout(decoded, layout, out)) {
        return false;
    }
    const std::vector<std::uint32_t> physical = dtsx::expand_speaker_activity_mask(decoded.bed_speaker_activity_mask);
    const auto cs = std::find(physical.begin(), physical.end(), 1U << 6U);
    const auto index = static_cast<std::size_t>(cs - physical.begin());
    if (cs == physical.end() || index >= decoded.bed_channels.size()) {
        return true;
    }
    for (const std::size_t back : {4U, 5U}) {
        for (std::size_t sample = 0U; sample < out[back].size(); ++sample) {
            const std::int64_t mixed = out[back][sample] + std::llround(decoded.bed_channels[index][sample] * 0.70710678);
            out[back][sample] = static_cast<std::int32_t>(
                std::clamp<std::int64_t>(mixed, (std::numeric_limits<std::int32_t>::min)(),
                                         (std::numeric_limits<std::int32_t>::max)()));
        }
    }
    return true;
}

bool is_extension(const dtsx::ElementaryFrame& frame) noexcept {
    return frame.packing == dtsx::StreamPacking::ExtensionBigEndian
        || frame.packing == dtsx::StreamPacking::ExtensionLittleEndian;
}

class Decoder final {
public:
    // Core frame: decoded now, played when the next frame shows there is no extension (plain DTS). Once a core follows
    // a core the stream is plain DTS, and play_core_only() plays every core as soon as it is decoded.
    bool core(dtsx::ElementaryFrame& frame, Planar& out, std::uint32_t& rate) {
        switch (frame.packing) {
        case dtsx::StreamPacking::DtsUhd:
            throw std::runtime_error("DTS-UHD is not supported");
        case dtsx::StreamPacking::Core14BitBigEndian:
        case dtsx::StreamPacking::Core14BitLittleEndian:
            throw std::runtime_error("14-bit DTS is not supported");
        case dtsx::StreamPacking::Core16BitLittleEndian: // raw spdif without bursts; libdcadec parses big-endian only
            for (std::size_t i = 0U; i + 1U < frame.bytes.size(); i += 2U) {
                std::swap(frame.bytes[i], frame.bytes[i + 1U]);
            }
            frame.packing = dtsx::StreamPacking::Core16BitBigEndian;
            break;
        default:
            break;
        }
        const bool played = core_pending_ && play_core(out, rate);
        core_only_ = core_only_ || core_pending_;
        bed_.remember_core(frame);
        objects_.remember_core_metadata(frame);
        core_pending_ = true;
        return played;
    }

    // Extension (ExSS) frame: DTS-HD bed and DTS:X objects, together with the core before it.
    bool extension(const dtsx::ElementaryFrame& frame, Planar& out, std::uint32_t& rate) {
        core_pending_ = false;
        DcaDecodedBed decoded_bed;
        DecodedObjectAudioFrame decoded;
        const auto lossy_base = make_xll_lossy_base(bed_.decoded_core());
        std::vector<DcaDecodedObjectAsset> lossy_object_assets;
        // Not limited to DTS:X 7.1 beds: plain DTS-HD 5.1/7.1 plays through here too
        const bool bed_available = bed_.decode_extension(
            frame, decoded_bed, false,
            has_lossy_object_asset(frame) ? &lossy_object_assets : nullptr);
        const ObjectFrameDecodeResult result =
            objects_.decode(frame, decoded, lossy_base, lossy_object_assets);
        if (result != ObjectFrameDecodeResult::Decoded || decoded.bed_channels.empty()) {
            if (!bed_available) {
                throw std::runtime_error("frame has no decodable audio (object: " + objects_.last_error()
                                         + "; bed: " + bed_.last_error() + ")");
            }
            take_bed(decoded, std::move(decoded_bed));
        }
        rate = decoded.sample_rate;
        // DTS:X has its extension sync word also when channel-based (7.1 bed + heights, no objects). Sticky: a frame
        // without it keeps the name.
        dtsx_ = dtsx_ || decoded.dtsx_extension_sync_word != 0U || !decoded.objects.empty();
        if (dtsx_) {
            set_format(decoded.imax_enhanced ? "DTS:X IMAX Enhanced" : "DTS:X", decoded);
        } else {
            const std::int32_t profile = bed_.extension_stream_info().profile;
            set_format(std::string(profile & DCADEC_PROFILE_HD_MA ? "DTS-HD MA"
                                   : profile & DCADEC_PROFILE_HD_HRA ? "DTS-HD HRA"
                                   : profile & DCADEC_PROFILE_EXPRESS ? "DTS Express" : "DTS-HD")
                           + layout_name(decoded.bed_channels.size(), decoded.bed_speaker_activity_mask),
                       decoded);
        }

        if (!decoded.objects.empty() && !renderer_.remove_embedded_object_fold_down(decoded)) {
            throw std::runtime_error("embedded object fold-down removal failed");
        }
        Planar bed;
        if (!map_supplemental_xll_to_height_layout(decoded, layout_, bed)
            && !map_bed(decoded, layout_, bed)) {
            throw std::runtime_error("bed cannot be mapped to 7.1.4");
        }
        Planar rendered;
        if (decoded.objects.empty() || !renderer_.render(decoded, rendered)) {
            out = std::move(bed); // unrenderable object metadata: keep the bed
            return true;
        }
        for (std::size_t channel = 0U; channel < rendered.size(); ++channel) {
            for (std::size_t sample = 0U; sample < rendered[channel].size(); ++sample) {
                const std::int64_t mixed =
                    static_cast<std::int64_t>(bed[channel][sample]) + rendered[channel][sample];
                rendered[channel][sample] = static_cast<std::int32_t>(
                    std::clamp<std::int64_t>(mixed, (std::numeric_limits<std::int32_t>::min)(),
                                             (std::numeric_limits<std::int32_t>::max)()));
            }
        }
        apply_native_object_output_limiters(rendered);
        out = std::move(rendered);
        return true;
    }

    // End of input: the last core frame of a plain DTS stream is still waiting
    bool flush(Planar& out, std::uint32_t& rate) {
        return core_pending_ && play_core(out, rate);
    }

    // Plain DTS: the core just decoded plays now instead of one frame later.
    // ponytail: assumes plain DTS stays plain; mpv reopens its AO (new helper) when the stream changes, a raw file
    // that switches to DTS-HD mid-stream would play one frame twice
    bool play_core_only(Planar& out, std::uint32_t& rate) {
        if (!core_only_ || !core_pending_) {
            return false;
        }
        core_pending_ = false;
        return play_core(out, rate);
    }

    // 7.1.4 outputs that carry the stream's channels
    const std::array<bool, kChannels>& active() const noexcept {
        return active_;
    }

private:
    // libdcadec's bed as the frame's bed (the object decoder had none)
    static void take_bed(DecodedObjectAudioFrame& decoded, DcaDecodedBed bed) {
        decoded.bed_channels = std::move(bed.channels);
        decoded.bed_speaker_activity_mask = bed.speaker_activity_mask;
        decoded.sample_rate = bed.sample_rate;
        decoded.samples_per_channel = bed.samples_per_channel;
    }

    bool play_core(Planar& out, std::uint32_t& rate) {
        if (bed_.decoded_core().channels.empty()) {
            return false;
        }
        DecodedObjectAudioFrame decoded;
        take_bed(decoded, bed_.decoded_core());
        rate = decoded.sample_rate;
        set_format((bed_.core_stream_info().es_matrix_surround ? "DTS-ES" : "DTS")
                       + layout_name(decoded.bed_channels.size(), decoded.bed_speaker_activity_mask),
                   decoded);
        return map_bed(decoded, layout_, out);
    }

    // New format: find the 7.1.4 outputs the stream really has. DTS:X fills all of them (heights, rendered objects);
    // anything else is mapped with a constant bed, and the outputs that stay silent are left out: a 5.1 stream goes
    // to Windows as 5.1, not as 7.1.4 with six silent channels that would look like an upmix.
    void set_format(std::string format, const DecodedObjectAudioFrame& decoded) {
        if (format == format_) {
            return;
        }
        format_ = std::move(format);
        std::fprintf(stderr, "format: %s\n", format_.c_str()); // the bridge shows it in mpv
        active_.fill(true);
        if (dtsx_) {
            return;
        }
        DecodedObjectAudioFrame probe;
        probe.bed_speaker_activity_mask = decoded.bed_speaker_activity_mask;
        probe.bed_channels.assign(decoded.bed_channels.size(), std::vector<std::int32_t>(1U, 1));
        Planar mapped;
        if (map_bed(probe, layout_, mapped)) {
            for (std::size_t i = 0U; i < kChannels; ++i) {
                active_[i] = !mapped[i].empty() && mapped[i][0] != 0;
            }
        }
    }

    // " 5.1", " 7.1", ...: channel count and DTS speaker activity mask (LFE1 = 0x8; 7.1 is 0x84B)
    static std::string layout_name(std::size_t channels, std::uint32_t mask) {
        const std::size_t lfe = mask & 0x8U ? 1U : 0U;
        return channels == 0U ? "" : " " + std::to_string(channels - lfe) + "." + std::to_string(lfe);
    }

    std::string format_;
    std::array<bool, kChannels> active_{};
    bool dtsx_ = false; // DTS:X seen

    ChannelLayout layout_ = *find_layout("7.1.4");
    ObjectFrameDecoder objects_;
    DcaBedDecoder bed_;
    ObjectAudioRenderer renderer_{layout_};
    bool core_pending_ = false; // core frame not (yet) followed by an extension frame
    bool core_only_ = false;    // a core followed a core: plain DTS, no extension frames
};

void write_frame(const Planar& pcm, const std::array<bool, kChannels>& active, std::uint32_t rate, std::vector<char>& out) {
    if (pcm.size() != kChannels || pcm.front().empty()) {
        return;
    }
    const std::size_t samples = pcm.front().size();
    const auto channels = static_cast<std::size_t>(std::count(active.begin(), active.end(), true));
    out.resize(8U + channels * 17U + samples * channels * 4U);
    char* cursor = out.data();
    const auto put = [&cursor](const auto& value) {
        std::memcpy(cursor, &value, sizeof value);
        cursor += sizeof value;
    };
    put(static_cast<std::uint16_t>(channels));
    put(static_cast<std::uint16_t>(samples));
    put(rate);
    for (std::size_t i = 0U; i < kChannels; ++i) {
        if (!active[i]) {
            continue;
        }
        const Speaker& speaker = kSpeakers[i];
        put(speaker.x);
        put(speaker.y);
        put(speaker.z);
        put(1.0F);
        put(static_cast<std::uint8_t>(speaker.lfe));
    }
    for (std::size_t sample = 0U; sample < samples; ++sample) {
        for (std::size_t i = 0U; i < kChannels; ++i) {
            if (active[i]) {
                put(static_cast<float>(pcm[i][sample]) / 8388608.0F); // 24-bit
            }
        }
    }
    std::fwrite(out.data(), 1, out.size(), stdout);
}

// DTS-ES 6.1 bed (C, L R, Ls Rs, LFE, Cs = 7): Cs lands on BL and BR at -3 dB, so 8 outputs carry sound
int self_test() {
    DecodedObjectAudioFrame bed;
    bed.bed_speaker_activity_mask = 0x1FU;
    for (std::int32_t channel = 0; channel < 7; ++channel) {
        bed.bed_channels.push_back({(channel + 1) * 1000});
    }
    Planar out;
    const bool mapped = map_bed(bed, *find_layout("7.1.4"), out);
    const auto active = mapped ? std::count_if(out.begin(), out.end(), [](const auto& c) { return c[0] != 0; }) : 0;
    const bool ok = active == 8 && out[4][0] == 4950 && out[5][0] == 4950; // Cs (7000) x 0.7071
    std::printf("DTS-ES Cs fold %s (%d outputs)\n", ok ? "OK" : "FAILED", static_cast<int>(active));
    return ok ? 0 : 1;
}

} // namespace
} // namespace dtsx_decode

int main(int argc, char** argv) {
    using namespace dtsx_decode;
    if (argc > 1 && std::strcmp(argv[1], "--selftest") == 0) {
        return self_test();
    }
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
    std::setvbuf(stdout, nullptr, _IONBF, 0); // each frame goes out in one write, no flush needed
    auto decoder = std::make_unique<Decoder>();
    dtsx::FrameAssembler assembler;
    std::vector<unsigned char> input(1 << 16);
    std::vector<char> out;
    Planar pcm;
    bool failing = false;
    int n;
    // _read returns what the pipe has instead of waiting for a full buffer
    while ((n = _read(0, input.data(), static_cast<unsigned>(input.size()))) > 0) {
        for (int i = 0; i < n; ++i) {
            std::optional<dtsx::ElementaryFrame> frame = assembler.push(input[i]);
            if (!frame) {
                continue;
            }
            try {
                std::uint32_t rate = 0U;
                if (is_extension(*frame) ? decoder->extension(*frame, pcm, rate) : decoder->core(*frame, pcm, rate)) {
                    write_frame(pcm, decoder->active(), rate, out);
                }
                if (decoder->play_core_only(pcm, rate)) {
                    write_frame(pcm, decoder->active(), rate, out);
                }
                failing = false;
            } catch (const std::exception& e) {
                if (!failing) {
                    std::fprintf(stderr, "dtsx-objects: %s; restarting the decoder\n", e.what());
                    failing = true;
                }
                decoder = std::make_unique<Decoder>(); // seek or damaged stream: XLL state is stale
            }
        }
    }
    std::uint32_t rate = 0U;
    if (decoder->flush(pcm, rate)) {
        write_frame(pcm, decoder->active(), rate, out);
    }
    return 0;
}
