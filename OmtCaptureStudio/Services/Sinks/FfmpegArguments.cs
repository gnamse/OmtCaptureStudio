using System;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// Pure FFmpeg argument construction — the encoder×quality → argv mapping and the quality → CRF
/// lookup, extracted from <see cref="FfmpegProcessHost"/> (NET-004 AC3) so the table is testable
/// headlessly without spawning ffmpeg.
///
/// <para>Deliberately static and dependency-free: the only thing that varies is which encoder a
/// quality maps to, and <see cref="Resolve"/> takes the hardware detector as a parameter rather
/// than reaching into the registry — so AutoHardware resolution is itself a pure, injectable rule.
/// Nothing here launches a process or touches the filesystem.</para>
/// </summary>
public static class FfmpegArguments
{
    /// <summary>Quality preset → x264-style CRF / NVENC-cq / QSV-global_quality integer.</summary>
    public static int QualityToCrf(QualityPreset quality) => quality switch
    {
        QualityPreset.Ultra => 16,
        QualityPreset.High => 20,
        QualityPreset.Medium => 24,
        QualityPreset.Low => 28,
        _ => 20
    };

    /// <summary>
    /// Video codec argv for an <b>already-resolved</b> encoder. <see cref="VideoEncoderChoice.AutoHardware"/>
    /// must be resolved by <see cref="Resolve"/> first — this method does not consult the registry, so the
    /// matrix below is a pure table. ProRes ignores quality (profile-fixed, no CRF).
    /// </summary>
    public static string VideoCodecArguments(VideoEncoderChoice encoder, QualityPreset quality)
    {
        int crf = QualityToCrf(quality);

        return encoder switch
        {
            VideoEncoderChoice.NvidiaNvenc => $"-c:v h264_nvenc -preset p4 -cq {crf} -pix_fmt yuv420p",
            VideoEncoderChoice.IntelQuickSync => $"-c:v h264_qsv -global_quality {crf} -pix_fmt yuv420p",
            VideoEncoderChoice.AmdAmf => $"-c:v h264_amf -quality quality -pix_fmt yuv420p",
            VideoEncoderChoice.ProRes => "-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le",
            VideoEncoderChoice.CpuX264 => $"-c:v libx264 -preset ultrafast -tune zerolatency -crf {crf} -pix_fmt yuv420p",
            _ => $"-c:v libx264 -preset ultrafast -tune zerolatency -crf {crf} -pix_fmt yuv420p"
        };
    }

    /// <summary>
    /// Audio codec argv for the container/encoder pair. PCM passthrough only for MOV+ProRes;
    /// everything else gets AAC 320k.
    /// </summary>
    public static string AudioCodecArguments(OutputContainerFormat container, VideoEncoderChoice encoder) =>
        container == OutputContainerFormat.MOV && encoder == VideoEncoderChoice.ProRes
            ? "-c:a pcm_s24le "
            : "-c:a aac -b:a 320k ";

    /// <summary>
    /// AutoHardware resolves to whatever the injected detector returns; a concrete choice passes through
    /// unchanged. Injecting the detector is what makes this rule testable — the production detector is the
    /// registry probe in <see cref="FfmpegProcessHost"/>, the test detector is a constant.
    /// </summary>
    public static VideoEncoderChoice Resolve(VideoEncoderChoice encoder, Func<VideoEncoderChoice> detectHardware) =>
        encoder == VideoEncoderChoice.AutoHardware ? detectHardware() : encoder;
}
