//! Raw TrueHD on stdin -> decoded channels with positions on stdout, for AtmosBridge.
//!
//! Per decoded access unit (little-endian):
//!   u16 channels, u16 samples, u32 sample_rate
//!   channels x { f32 x, f32 y, f32 z, f32 gain, u8 lfe }   Windows space: x right, y up, -z front, room cube [-1, 1]
//!   samples x channels f32                                interleaved, [-1, 1)
//!
//! stderr: log lines, and "objects: 0|1" then "format: TrueHD Atmos" / "format: TrueHD 7.1" when the stream changes.
//!
//! Atmos streams decode the 16-channel object presentation and take positions and gains from OAMD
//! (beds at their speaker positions); other streams decode their largest presentation at speaker positions.

use std::io::{Read, Write};

use truehd::process::MAX_PRESENTATIONS;
use truehd::process::decode::Decoder;
use truehd::process::extract::Extractor;
use truehd::process::parse::Parser;
use truehd::structs::channel::ChannelLabel;
use truehd::structs::oamd::{GAIN_MINUS_INFINITY, ObjectAudioMetadataPayload, SpeakerLabels};

#[derive(Clone, Copy, Default)]
struct Placement {
    pos: [f32; 3], // DAMF: x right, y front, z up
    gain: f32,
    lfe: bool,
}

/// TrueHD speaker labels in DAMF space.
fn label_placement(label: ChannelLabel) -> Placement {
    use ChannelLabel::*;
    let w = 42.0 / 62.0;
    let pos = match label {
        L => [-1.0, 1.0, 0.0],
        R => [1.0, 1.0, 0.0],
        C => [0.0, 1.0, 0.0],
        Ls => [-1.0, 0.0, 0.0],
        Rs => [1.0, 0.0, 0.0],
        Tfl => [-1.0, 1.0, 1.0],
        Tfr => [1.0, 1.0, 1.0],
        Tsl => [-1.0, 0.0, 1.0],
        Tsr => [1.0, 0.0, 1.0],
        Tbl => [-1.0, -1.0, 1.0],
        Tbr => [1.0, -1.0, 1.0],
        Lsc => [-0.5, 1.0, 0.0],
        Rsc => [0.5, 1.0, 0.0],
        Lb => [-1.0, -1.0, 0.0],
        Rb => [1.0, -1.0, 0.0],
        Cb => [0.0, -1.0, 0.0],
        Tc => [0.0, 0.0, 1.0],
        Lsd => [-1.0, 0.5, 0.0],
        Rsd => [1.0, 0.5, 0.0],
        Lw => [-1.0, w, 0.0],
        Rw => [1.0, w, 0.0],
        Tfc => [0.0, 1.0, 1.0],
        LFE | LFE2 => [0.0, 0.0, 0.0],
    };
    Placement { pos, gain: 1.0, lfe: matches!(label, LFE | LFE2) }
}

/// Apply the first update block of an OAMD payload: channel i of the object presentation is object i.
// ponytail: positions jump at access-unit granularity (0.83 ms), sample_offset and ramp_duration are ignored
fn apply_oamd(oamd: &ObjectAudioMetadataPayload, placements: &mut [Placement]) {
    let Some(element) = &oamd.object_element else { return };
    let beds = oamd
        .program_assignment
        .bed_assignment
        .first()
        .map(|bed| bed.to_index_vec())
        .unwrap_or_default();
    let positions = oamd.get_damf_pos();
    for (i, placement) in placements.iter_mut().enumerate().take(oamd.object_count) {
        let Some(block) = element.object_data.get(i).and_then(|blocks| blocks.first()) else { continue };
        let gain = block.object_basic_info.object_gain;
        placement.gain = if block.b_object_not_active || gain == GAIN_MINUS_INFINITY {
            0.0
        } else {
            10f32.powf(gain as f32 / 20.0)
        };
        if block.b_object_in_bed_or_isf {
            if let Some(label) = beds.get(i).and_then(|&b| SpeakerLabels::from_u8(b as u8)) {
                placement.lfe = matches!(label, SpeakerLabels::LFE | SpeakerLabels::LFE2);
                placement.pos = *label.pos();
            }
        } else if let Some(p) = positions.get(i).and_then(|blocks| blocks.first()) {
            placement.lfe = false;
            placement.pos = [p[0] as f32, p[1] as f32, p[2] as f32];
        }
    }
}

fn main() -> std::io::Result<()> {
    let mut stdin = std::io::stdin().lock();
    let mut stdout = std::io::BufWriter::with_capacity(1 << 16, std::io::stdout().lock());
    let mut extractor = Extractor::default();
    let mut parser = Parser::default();
    let mut decoder = Decoder::default();
    // ponytail: decodes every presentation and keeps the largest; they share substreams, so the extra cost is the downmixes
    let required = [true; MAX_PRESENTATIONS];
    parser.set_required_presentations(&required);

    let mut placements: Vec<Placement> = Vec::new();
    let mut atmos = false;
    let mut reported = String::new(); // last "format:" line
    let mut resyncing = false;
    let mut input = vec![0u8; 1 << 16];
    let mut out = Vec::new();

    loop {
        let n = stdin.read(&mut input)?;
        if n == 0 {
            return Ok(());
        }
        extractor.push_bytes(&input[..n]);
        for frame in extractor.by_ref() {
            let Ok(frame) = frame else { continue }; // extractor resyncs on its own
            let decoded = parser
                .parse(&frame)
                .and_then(|au| decoder.decode_presentations(&au, &required));
            let decoded = match decoded {
                Ok(d) => {
                    resyncing = false;
                    d
                }
                Err(e) => {
                    if !resyncing {
                        eprintln!("thd-objects: {e}; resuming at next major sync");
                        parser.reset_for_next_major_sync();
                        decoder.reset_for_next_major_sync();
                        resyncing = true;
                    }
                    continue;
                }
            };
            let Some(au) = decoded.iter().rev().flatten().next() else { continue };
            if au.is_duplicate {
                continue;
            }

            if placements.len() != au.channel_count || au.substream_info_changed {
                placements = au.channel_labels.iter().map(|&l| label_placement(l)).collect();
                placements.resize(au.channel_count, Placement::default());
                atmos = false;
                // For the bridge (shown in mpv): the 16-channel presentation is Atmos objects, the others are channels
                let format = if au.channel_count == 16 {
                    "TrueHD Atmos".to_string()
                } else {
                    let lfe = placements.iter().filter(|p| p.lfe).count();
                    format!("TrueHD {}.{lfe}", au.channel_count - lfe)
                };
                if format != reported {
                    eprintln!("objects: {}", u8::from(au.channel_count == 16));
                    eprintln!("format: {format}");
                    reported = format;
                }
            }
            if let Some(oamd) = au.oamd.last() {
                if !atmos {
                    eprintln!("thd-objects: Atmos, {} channels, {} objects", au.channel_count, oamd.object_count);
                    atmos = true;
                }
                apply_oamd(oamd, &mut placements);
            }

            out.clear();
            out.extend((au.channel_count as u16).to_le_bytes());
            out.extend((au.sample_length as u16).to_le_bytes());
            out.extend(au.sampling_frequency.to_le_bytes());
            for p in &placements {
                let gain = if au.channel_count == 16 && !atmos { 0.0 } else { p.gain }; // objects before the first OAMD
                for v in [p.pos[0], p.pos[2], -p.pos[1], gain] {
                    out.extend(v.to_le_bytes());
                }
                out.push(p.lfe as u8);
            }
            for frame in &au.pcm_data[..au.sample_length] {
                for &s in &frame[..au.channel_count] {
                    out.extend((s as f32 / 8_388_608.0).to_le_bytes());
                }
            }
            stdout.write_all(&out)?;
        }
        stdout.flush()?;
    }
}
