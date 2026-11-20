using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>
    /// The full ffmpeg argument vector for one take, one element per argv token. The output path is
    /// the final element and is NOT quoted: <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// passes it through as a single argument, so a space or a quote in the path cannot break out into
    /// another flag. Callers must not also set <c>ProcessStartInfo.Arguments</c>.
    /// </summary>
    public static string[] BuildArgumentList(
        RecordingConfig config,
        StreamFormat format,
        string outputPath,
        string videoPipeName,
        string? audioPipeName,
        VideoEncoderChoice resolvedEncoder)
    {
        string fpsStr = FormatRate(format);

        var args = new List<string>
        {
            "-nostdin", "-y",
            "-thread_queue_size", "128",
            "-f", "rawvideo",
            "-vcodec", "rawvideo",
            "-pix_fmt", "bgra",
            "-s", $"{format.Width}x{format.Height}",
            "-r", fpsStr,
            "-i", $@"\\.\pipe\{videoPipeName}"
        };

        if (!string.IsNullOrEmpty(audioPipeName))
        {
            args.Add("-thread_queue_size");
            args.Add("512");
            args.Add("-f");
            args.Add("f32le");
            args.Add("-ar");
            args.Add(format.SampleRate.ToString(CultureInfo.InvariantCulture));
            args.Add("-ac");
            args.Add(format.Channels.ToString(CultureInfo.InvariantCulture));
            args.Add("-i");
            args.Add($@"\\.\pipe\{audioPipeName}");
        }

        args.Add("-max_interleave_delta");
        args.Add("5000000");
        AddTokens(args, VideoCodecArguments(resolvedEncoder, config.Quality));

        if (!string.IsNullOrEmpty(audioPipeName))
        {
            AddTokens(args, AudioCodecArguments(config.ContainerFormat, resolvedEncoder));
        }
        else
        {
            args.Add("-an");
        }

        if (config.ContainerFormat is OutputContainerFormat.MP4 or OutputContainerFormat.MOV)
        {
            args.Add("-movflags");
            args.Add("+frag_keyframe+empty_moov");
        }

        args.Add(outputPath);
        return args.ToArray();
    }

    /// <summary>
    /// Rate token for <c>-r</c>. An explicit ratio is passed through (<c>60000/1001</c>), which ffmpeg
    /// accepts and which does not round 59.94. A legacy format that only stored a double keeps the
    /// invariant-culture decimal (<c>59.94</c>, never a locale comma).
    /// </summary>
    public static string FormatRate(StreamFormat format)
    {
        if (format.FrameRateNumerator > 0 && format.FrameRateDenominator > 0)
        {
            return $"{format.FrameRateNumerator.ToString(CultureInfo.InvariantCulture)}/{format.FrameRateDenominator.ToString(CultureInfo.InvariantCulture)}";
        }

        double fps = format.FrameRate > 0 ? format.FrameRate : 60.0;
        return fps.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Splits a codec fragment on whitespace. The fragments this class builds contain no quoted tokens.</summary>
    private static void AddTokens(List<string> args, string fragment)
    {
        int i = 0;
        while (i < fragment.Length)
        {
            while (i < fragment.Length && char.IsWhiteSpace(fragment[i])) i++;
            if (i >= fragment.Length) break;
            int start = i;
            while (i < fragment.Length && !char.IsWhiteSpace(fragment[i])) i++;
            args.Add(fragment.Substring(start, i - start));
        }
    }
}
