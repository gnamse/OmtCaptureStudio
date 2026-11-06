
using System.Diagnostics;
using System.Reflection;
using Vortice.MediaFoundation;

// SPIKE-MF - Media Foundation SinkWriter feasibility. THROWAWAY, harness-side only.
//   --list
//   --encode <secs> <out.mp4> [--bgra] [--fmpeg4]
// Never Finalize when killed - that is the crash test.

if (args.Length == 0) { Console.WriteLine("usage: --list | --encode <secs> <out.mp4> [--bgra] [--fmpeg4] | --av <secs> <out.mp4>"); return; }

const int W = 1920, H = 1080, FPS = 60;


if (args[0] == "--av")
{
    await AvTest(int.Parse(args[1]), args[2]);
    return;
}

static async Task AvTest(int seconds, string outPath)
{
    const int AW = 1920, AH = 1080, AFPS = 60, RATE = 48000, CH = 2;
    if (File.Exists(outPath)) File.Delete(outPath);

    Guid vMajor = FieldGuid("MediaTypeGuids", "Video");
    Guid aMajor = FieldGuid("MediaTypeGuids", "Audio");
    Guid h264   = FieldGuid("VideoFormatGuids", "H264");
    Guid argb32 = FieldGuid("VideoFormatGuids", "Argb32", "ARGB32", "Rgb32");
    Guid aac    = FieldGuid("AudioFormatGuids", "Aac");
    Guid aFloat = FieldGuid("AudioFormatGuids", "Float");
    Guid fmpeg4 = FieldGuid("TranscodeContainerTypeGuids", "Fmpeg4");

    bool plain = outPath.Contains("plain");
    MediaFactory.MFStartup(false);
    IMFAttributes? attrs = null;
    if (!plain) { attrs = MediaFactory.MFCreateAttributes(1); attrs.Set(TranscodeAttributeKeys.TranscodeContainertype, fmpeg4); }
    Console.WriteLine($"  container: {(plain ? "PLAIN MPEG4 (default)" : "FMPEG4 (fragmented)")}");
    var w = MediaFactory.MFCreateSinkWriterFromURL(outPath, null, attrs);

    var k = MediaTypeAttributeKeys.MajorType; var st = MediaTypeAttributeKeys.Subtype;
    var fs = MediaTypeAttributeKeys.FrameSize; var fr = MediaTypeAttributeKeys.FrameRate;
    var br = MediaTypeAttributeKeys.AvgBitrate; var il = MediaTypeAttributeKeys.InterlaceMode;
    var par = MediaTypeAttributeKeys.PixelAspectRatio;
    var nch = MediaTypeAttributeKeys.AudioNumChannels;
    var sps = MediaTypeAttributeKeys.AudioSamplesPerSecond;
    var bps = MediaTypeAttributeKeys.AudioBitsPerSample;
    var ba  = MediaTypeAttributeKeys.AudioBlockAlignment;
    var abr = MediaTypeAttributeKeys.AudioAvgBytesPerSecond;
    var aacp = MediaTypeAttributeKeys.AacAudioProfileLevelIndication;

    // ---- AUDIO FIRST, ALONE: does AAC-in-this-container work at all? (isolates audio from video) ----
    {
        using var ao0 = MediaFactory.MFCreateMediaType();
        ao0.Set(k, aMajor); ao0.Set(st, aac); ao0.Set(nch,(uint)CH); ao0.Set(sps,(uint)RATE); ao0.Set(bps,16u);
        try
        {
            int t0 = w.AddStream(ao0);
            using var ai0 = MediaFactory.MFCreateMediaType();
            ai0.Set(k, aMajor); ai0.Set(st, FieldGuid("AudioFormatGuids","Pcm")); ai0.Set(nch,(uint)CH); ai0.Set(sps,(uint)RATE);
            ai0.Set(bps,16u); ai0.Set(ba,(uint)(CH*2)); ai0.Set(abr,(uint)(RATE*CH*2));
            w.SetInputMediaType(t0, ai0, null);
            Console.WriteLine("  AUDIO-FIRST PCM16: ACCEPTED");

            // now add VIDEO after a working audio stream - isolates ordering from format
            try
            {
                using var vo0 = MediaFactory.MFCreateMediaType();
                vo0.Set(k, vMajor); vo0.Set(st, h264); vo0.Set(fs, ((ulong)AW << 32) | (uint)AH);
                vo0.Set(fr, ((ulong)AFPS << 32) | 1u); vo0.Set(br, 20_000_000u); vo0.Set(il, 2u); vo0.Set(par, ((ulong)1 << 32) | 1u);
                int v0 = w.AddStream(vo0);
                using var vi0 = MediaFactory.MFCreateMediaType();
                vi0.Set(k, vMajor); vi0.Set(st, argb32); vi0.Set(fs, ((ulong)AW << 32) | (uint)AH);
                vi0.Set(fr, ((ulong)AFPS << 32) | 1u); vi0.Set(il, 2u); vi0.Set(par, ((ulong)1 << 32) | 1u);
                w.SetInputMediaType(v0, vi0, null);
                Console.WriteLine("  THEN VIDEO ARGB32: ACCEPTED  -> AUDIO-FIRST ORDER WORKS (both streams live)");
            }
            catch (Exception ex2) { Console.WriteLine("  THEN VIDEO ARGB32: REFUSED -> " + ex2.Message.Split('\n')[0]); }
        }
        catch (Exception ex) { Console.WriteLine("  AUDIO-FIRST PCM16: REFUSED -> " + ex.Message.Split('\n')[0]); }
        return;
    }

    // ---- video ----
    using var vo = MediaFactory.MFCreateMediaType();
    vo.Set(k, vMajor); vo.Set(st, h264); vo.Set(fs, ((ulong)AW << 32) | (uint)AH);
    vo.Set(fr, ((ulong)AFPS << 32) | 1u); vo.Set(br, 20_000_000u); vo.Set(il, 2u); vo.Set(par, ((ulong)1 << 32) | 1u);
    int vs = w.AddStream(vo);
    using var vi = MediaFactory.MFCreateMediaType();
    vi.Set(k, vMajor); vi.Set(st, argb32); vi.Set(fs, ((ulong)AW << 32) | (uint)AH);
    vi.Set(fr, ((ulong)AFPS << 32) | 1u); vi.Set(il, 2u); vi.Set(par, ((ulong)1 << 32) | 1u);
    w.SetInputMediaType(vs, vi, null);
    Console.WriteLine("  video: H.264 <== ARGB32 accepted");

    // ---- audio: which AAC output-type shape does the encoder actually accept? ----
    // Hypothesis under test: AvgBytesPerSecond / BlockAlignment are PCM concepts and are INVALID on a
    // compressed output type, which would make the encoder refuse every input type offered to it.
    Guid pcm16 = FieldGuid("AudioFormatGuids", "Pcm");
    int asIdx = -1;

    (string name, Action<IMFMediaType> build)[] shapes =
    {
        ("A+B+profile(orig)", mt => { mt.Set(nch,(uint)CH); mt.Set(sps,(uint)RATE); mt.Set(bps,16u); mt.Set(ba,(uint)(CH*2)); mt.Set(abr,(uint)(RATE*CH*2)); try{ mt.Set(aacp,0x29u);}catch{} }),
        ("A+B only        ", mt => { mt.Set(nch,(uint)CH); mt.Set(sps,(uint)RATE); mt.Set(bps,16u); mt.Set(ba,(uint)(CH*2)); mt.Set(abr,(uint)(RATE*CH*2)); }),
        ("profile only    ", mt => { mt.Set(nch,(uint)CH); mt.Set(sps,(uint)RATE); mt.Set(bps,16u); try{ mt.Set(aacp,0x29u);}catch{} }),
        ("nch+sps+bps only", mt => { mt.Set(nch,(uint)CH); mt.Set(sps,(uint)RATE); mt.Set(bps,16u); }),
    };

    string? winner = null;
    foreach (var (name, build) in shapes)
    {
        try
        {
            using var ao = MediaFactory.MFCreateMediaType();
            ao.Set(k, aMajor); ao.Set(st, aac); build(ao);
            asIdx = w.AddStream(ao);
            Console.WriteLine($"  OUTPUT {name}: AddStream OK");
            winner = name; break;
        }
        catch (Exception ex) { Console.WriteLine($"  OUTPUT {name}: AddStream REFUSED ({ex.Message.Split(':')[0]})"); }
    }
    if (winner is null) { Console.WriteLine("  no output shape worked; aborting audio"); }
    else
    {
        (string nm, Guid sub, uint bBits, uint baV, uint abrV)[] inputs =
        {
            ("float32 32-bit", aFloat, 32u, (uint)(CH*4), (uint)(RATE*CH*4)),
            ("PCM16 16-bit  ", pcm16,  16u, (uint)(CH*2), (uint)(RATE*CH*2)),
        };
        foreach (var (nm, sub, bBits, baV, abrV) in inputs)
        {
            try
            {
                using var ai = MediaFactory.MFCreateMediaType();
                ai.Set(k, aMajor); ai.Set(st, sub); ai.Set(nch,(uint)CH); ai.Set(sps,(uint)RATE);
                ai.Set(bps, bBits); ai.Set(ba, baV); ai.Set(abr, abrV);
                w.SetInputMediaType(asIdx, ai, null);
                Console.WriteLine($"  INPUT  {nm} via output[{winner.Trim()}]: ACCEPTED");
                break;
            }
            catch (Exception ex) { Console.WriteLine($"  INPUT  {nm} via output[{winner.Trim()}]: REFUSED ({ex.Message.Split(':')[0]})"); }
        }
    }

    w.BeginWriting();
    byte[] frame = new byte[AW * AH * 4];
    float[] audio = new float[RATE / 100 * CH];
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int vf = 0, af = 0;
    long vs0 = 0, as0 = 0;
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        // one video frame
        for (int i = 0; i < frame.Length; i += 4) { frame[i] = (byte)(i & 0xFF); frame[i+1] = 90; frame[i+2] = (byte)(vf & 0xFF); frame[i+3] = 255; }
        using (var b = MediaFactory.MFCreateMemoryBuffer(frame.Length))
        {
            b.Lock(out IntPtr ptr, out _, out _);
            System.Runtime.InteropServices.Marshal.Copy(frame, 0, ptr, frame.Length);
            b.Unlock(); b.CurrentLength = frame.Length;
            using var s = MediaFactory.MFCreateSample(); s.AddBuffer(b);
            s.SampleTime = vs0; s.SampleDuration = 10_000_000L / AFPS;
            w.WriteSample(vs, s);
        }
        vs0 += 10_000_000L / AFPS; vf++;

        // one 10 ms audio chunk
        for (int i = 0; i < audio.Length; i++) audio[i] = (float)Math.Sin(2 * Math.PI * 440 * (i + af * audio.Length) / RATE) * 0.25f;
        var ab = new byte[audio.Length * 4];
        Buffer.BlockCopy(audio, 0, ab, 0, ab.Length);
        using (var b = MediaFactory.MFCreateMemoryBuffer(ab.Length))
        {
            b.Lock(out IntPtr ptr, out _, out _);
            System.Runtime.InteropServices.Marshal.Copy(ab, 0, ptr, ab.Length);
            b.Unlock(); b.CurrentLength = ab.Length;
            using var s = MediaFactory.MFCreateSample(); s.AddBuffer(b);
            s.SampleTime = as0; s.SampleDuration = 1_000_000L;
            w.WriteSample(asIdx, s);
        }
        as0 += 1_000_000L; af++;
    }
    w.Finalize();
    MediaFactory.MFShutdown();
    Console.WriteLine($"  wrote {vf} video frames + {af} audio chunks ({af * 10} ms) in {sw.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"  file: {new FileInfo(outPath).Length:n0} bytes -> {outPath}");
}

if (args[0] == "--list")
{
    foreach (string tn in new[] { "VideoFormatGuids", "MediaTypeGuids", "TranscodeAttributeKeys", "TranscodeContainerTypeGuids" })
    {
        var t = typeof(MediaFactory).Assembly.GetTypes().FirstOrDefault(x => x.IsPublic && x.Name == tn);
        Console.WriteLine($"=== {tn}");
        if (t is null) { Console.WriteLine("    MISSING"); continue; }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static).Take(30))
            Console.WriteLine("    " + f.Name);
    }
    return;
}

bool bgraInput = args.Contains("--bgra");
bool fmpeg4    = args.Contains("--fmpeg4");
int seconds    = int.Parse(args[1]);
string outPath = args[2];
if (File.Exists(outPath)) File.Delete(outPath);

static Guid FieldGuid(string typeName, params string[] names)
{
    var t = typeof(MediaFactory).Assembly.GetTypes().FirstOrDefault(x => x.IsPublic && x.Name == typeName)
            ?? throw new InvalidOperationException($"type {typeName} not found");
    foreach (string n in names)
    {
        var f = t.GetField(n, BindingFlags.Public | BindingFlags.Static);
        if (f is not null && f.FieldType == typeof(Guid)) return (Guid)f.GetValue(null)!;
    }
    throw new InvalidOperationException($"{typeName}: none of [{string.Join(", ", names)}]. Available: " +
        string.Join(", ", t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(Guid)).Select(f => f.Name)));
}

Guid gVideoMajor = FieldGuid("MediaTypeGuids", "Video");
Guid gH264       = FieldGuid("VideoFormatGuids", "H264");
Guid gNv12       = FieldGuid("VideoFormatGuids", "NV12");
Guid gArgb32     = bgraInput ? FieldGuid("VideoFormatGuids", "Argb32", "ARGB32", "Rgb32") : Guid.Empty;
Guid gFmpeg4     = fmpeg4 ? FieldGuid("TranscodeContainerTypeGuids", "Fmpeg4") : Guid.Empty;

var mtMajor     = MediaTypeAttributeKeys.MajorType;
var mtSubtype   = MediaTypeAttributeKeys.Subtype;
var mtFrameSize = MediaTypeAttributeKeys.FrameSize;
var mtFrameRate = MediaTypeAttributeKeys.FrameRate;
var mtBitrate   = MediaTypeAttributeKeys.AvgBitrate;
var mtInterlace = MediaTypeAttributeKeys.InterlaceMode;
var mtPAR       = MediaTypeAttributeKeys.PixelAspectRatio;

Console.WriteLine($"SPIKE-MF  {W}x{H}@{FPS}  input={(bgraInput ? "ARGB32" : "NV12")}  container={(fmpeg4 ? "FMPEG4 (fragmented)" : "MPEG4")}");

MediaFactory.MFStartup(false);

IMFSinkWriter writer;
IMFAttributes? creationAttrs = null;
try
{
    if (fmpeg4)
    {
        creationAttrs = MediaFactory.MFCreateAttributes(1);
        creationAttrs.Set(TranscodeAttributeKeys.TranscodeContainertype, gFmpeg4);
        Console.WriteLine("  set container type = Fmpeg4");
    }
    writer = MediaFactory.MFCreateSinkWriterFromURL(outPath, null, creationAttrs);
}
catch (Exception ex)
{
    Console.WriteLine($"  CREATE FAILED with the requested container: {ex.Message.Split('\n')[0]}");
    MediaFactory.MFShutdown();
    return;
}
Console.WriteLine("  sink writer created");

using var outType = MediaFactory.MFCreateMediaType();
outType.Set(mtMajor, gVideoMajor);
outType.Set(mtSubtype, gH264);
outType.Set(mtFrameSize, ((ulong)W << 32) | (uint)H);
outType.Set(mtFrameRate, ((ulong)FPS << 32) | 1u);
outType.Set(mtBitrate, 20_000_000u);
outType.Set(mtInterlace, 2u);
outType.Set(mtPAR, ((ulong)1 << 32) | 1u);
int stream = writer.AddStream(outType);

using var inType = MediaFactory.MFCreateMediaType();
inType.Set(mtMajor, gVideoMajor);
inType.Set(mtSubtype, bgraInput ? gArgb32 : gNv12);
inType.Set(mtFrameSize, ((ulong)W << 32) | (uint)H);
inType.Set(mtFrameRate, ((ulong)FPS << 32) | 1u);
inType.Set(mtInterlace, 2u);
inType.Set(mtPAR, ((ulong)1 << 32) | 1u);
writer.SetInputMediaType(stream, inType, null);
Console.WriteLine($"  input media type accepted: {(bgraInput ? "ARGB32" : "NV12")}");

writer.BeginWriting();

byte[] bgra = new byte[W * H * 4];
byte[] nv12 = new byte[W * H * 3 / 2];
var convertWatch = new Stopwatch();
double convertMsTotal = 0, writeMsTotal = 0; long convertCount = 0;
var overall = Stopwatch.StartNew();
int frames = 0;
var lastReport = 0;

while (overall.Elapsed.TotalSeconds < seconds)
{
    MakeSyntheticFrame(bgra, W, H, frames);
    byte[] payload = bgra;
    if (!bgraInput)
    {
        convertWatch.Restart(); BgraToNv12(bgra, nv12, W, H); convertWatch.Stop();
        convertMsTotal += convertWatch.Elapsed.TotalMilliseconds; convertCount++;
        payload = nv12;
    }

    var t = Stopwatch.StartNew();
    using var buffer = MediaFactory.MFCreateMemoryBuffer(payload.Length);
    buffer.Lock(out IntPtr p, out _, out _);
    System.Runtime.InteropServices.Marshal.Copy(payload, 0, p, payload.Length);
    buffer.Unlock();
    buffer.CurrentLength = payload.Length;
    using var sample = MediaFactory.MFCreateSample();
    sample.AddBuffer(buffer);
    long dur = 10_000_000L / FPS;
    sample.SampleTime = frames * dur;
    sample.SampleDuration = dur;
    writer.WriteSample(stream, sample);
    t.Stop();
    writeMsTotal += t.Elapsed.TotalMilliseconds;

    frames++;
    if ((int)overall.Elapsed.TotalSeconds / 2 != lastReport)
    {
        lastReport = (int)overall.Elapsed.TotalSeconds / 2;
        Console.WriteLine($"   t={overall.Elapsed.TotalSeconds,4:F1}s frames={frames,6}  file={new FileInfo(outPath).Length,12:n0} B");
    }
}

void Finish()
{
    writer.Finalize();
    MediaFactory.MFShutdown();
    double wall = overall.Elapsed.TotalSeconds;
    var fi = new FileInfo(outPath);
    Console.WriteLine();
    Console.WriteLine("=== RESULT ===");
    Console.WriteLine($"  frames written : {frames}  ({frames / wall:F1} fps over {wall:F2}s)");
    Console.WriteLine($"  file           : {fi.Length:n0} bytes");
    if (convertCount > 0)
        Console.WriteLine($"  BGRA->NV12     : {convertMsTotal / convertCount:F3} ms/frame ({100 * convertMsTotal / (wall * 1000):F1}% of wall)");
    else
        Console.WriteLine("  BGRA->NV12     : not needed (sink accepted ARGB32 directly)");
    Console.WriteLine($"  WriteSample    : {writeMsTotal / Math.Max(1, frames):F3} ms/frame");
    Console.WriteLine($"  output         : {outPath}");
}

Finish();

static void MakeSyntheticFrame(byte[] buf, int w, int h, int n)
{
    int shift = (n * 3) % w;
    for (int y = 0; y < h; y++)
    {
        int row = y * w * 4;
        for (int x = 0; x < w; x++)
        {
            int i = row + x * 4;
            buf[i + 0] = (byte)((x + shift) & 0xFF);
            buf[i + 1] = (byte)(y & 0xFF);
            buf[i + 2] = (byte)((x + y + n * 2) & 0xFF);
            buf[i + 3] = 255;
        }
    }
}

static void BgraToNv12(byte[] bgra, byte[] nv12, int w, int h)
{
    int ySize = w * h;
    for (int y = 0; y < h; y++)
    {
        int src = y * w * 4, dst = y * w;
        for (int x = 0; x < w; x++)
        {
            int i = src + x * 4;
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            nv12[dst + x] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
        }
    }
    for (int y = 0; y < h / 2; y++)
    {
        int dst = ySize + y * w;
        for (int x = 0; x < w / 2; x++)
        {
            int i = (y * 2) * w * 4 + (x * 2) * 4;
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            nv12[dst + x * 2] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
            nv12[dst + x * 2 + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
        }
    }
}
