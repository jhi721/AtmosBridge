// Raw DTS (core, DTS-HD, DTS:X) on stdin -> 7.1.4 PCM at speaker positions on stdout, for AtmosBridge.
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
#include "app/pipeline.cpp"

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

bool is_extension(const dtsx::ElementaryFrame& frame) noexcept {
    return frame.packing == dtsx::StreamPacking::ExtensionBigEndian
        || frame.packing == dtsx::StreamPacking::ExtensionLittleEndian;
}

class Decoder final {
public:
    // Core frame: decoded now, played when the next frame shows there is no extension (plain DTS).
    bool core(const dtsx::ElementaryFrame& frame, Planar& out, std::uint32_t& rate) {
        if (frame.packing == dtsx::StreamPacking::DtsUhd) {
            throw std::runtime_error("DTS-UHD is not supported");
        }
        const bool played = core_pending_ && play_core(out, rate);
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

        if (!decoded.objects.empty() && !renderer_.remove_embedded_object_fold_down(decoded)) {
            throw std::runtime_error("embedded object fold-down removal failed");
        }
        Planar bed;
        if (!map_supplemental_xll_to_height_layout(decoded, layout_, bed)
            && !map_xll_bed_to_layout(decoded, layout_, bed)) {
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
        return map_xll_bed_to_layout(decoded, layout_, out);
    }

    ChannelLayout layout_ = *find_layout("7.1.4");
    ObjectFrameDecoder objects_;
    DcaBedDecoder bed_;
    ObjectAudioRenderer renderer_{layout_};
    bool core_pending_ = false; // core frame not (yet) followed by an extension frame
};

void write_frame(const Planar& pcm, std::uint32_t rate, std::vector<char>& out) {
    if (pcm.size() != kChannels || pcm.front().empty()) {
        return;
    }
    const std::size_t samples = pcm.front().size();
    out.resize(8U + kChannels * 17U + samples * kChannels * 4U);
    char* cursor = out.data();
    const auto put = [&cursor](const auto& value) {
        std::memcpy(cursor, &value, sizeof value);
        cursor += sizeof value;
    };
    put(static_cast<std::uint16_t>(kChannels));
    put(static_cast<std::uint16_t>(samples));
    put(rate);
    for (const Speaker& speaker : kSpeakers) {
        put(speaker.x);
        put(speaker.y);
        put(speaker.z);
        put(1.0F);
        put(static_cast<std::uint8_t>(speaker.lfe));
    }
    for (std::size_t sample = 0U; sample < samples; ++sample) {
        for (const auto& channel : pcm) {
            put(static_cast<float>(channel[sample]) / 8388608.0F); // 24-bit
        }
    }
    std::fwrite(out.data(), 1, out.size(), stdout);
}

} // namespace
} // namespace dtsx_decode

int main() {
    using namespace dtsx_decode;
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
                    write_frame(pcm, rate, out);
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
        write_frame(pcm, rate, out);
    }
    return 0;
}
