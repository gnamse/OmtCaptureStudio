namespace OmtCaptureStudio.Models;

/// <summary>
/// A frame rate as an exact integer ratio. 59.94 is <c>60000/1001</c>, not a double that
/// drifts, and an integer setting N is <c>N/1</c> so existing call sites and settings stay valid.
/// </summary>
public readonly record struct RationalFrameRate
{
    /// <summary>How close a measured double must be to a known broadcast fraction before it snaps.</summary>
    private const double KnownRateSnapFps = 0.015;

    /// <summary>A clean integer such as 30.0 stays <c>30/1</c> rather than a continued fraction.</summary>
    private const double IntegerSnapFps = 0.0005;

    private static readonly (int Numerator, int Denominator)[] KnownRates =
    {
        (60000, 1001),
        (30000, 1001),
        (24000, 1001),
        (120000, 1001),
        (48000, 1001),
    };

    public int Numerator { get; }
    public int Denominator { get; }

    public RationalFrameRate(int numerator, int denominator)
    {
        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        if (numerator <= 0 || denominator <= 0)
        {
            Numerator = 0;
            Denominator = 0;
            return;
        }

        int divisor = GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public bool IsValid => Numerator > 0 && Denominator > 0;

    public double FramesPerSecond => IsValid ? (double)Numerator / Denominator : 0.0;

    public static RationalFrameRate FromInteger(int framesPerSecond)
        => framesPerSecond > 0 ? new RationalFrameRate(framesPerSecond, 1) : default;

    /// <summary>59.94 fps, the NTSC color rate.</summary>
    public static RationalFrameRate Ntsc5994 => new(60000, 1001);

    /// <summary>29.97 fps.</summary>
    public static RationalFrameRate Ntsc2997 => new(30000, 1001);

    /// <summary>23.976 fps.</summary>
    public static RationalFrameRate Ntsc23976 => new(24000, 1001);

    /// <summary>
    /// Recovers a ratio from a double. Exact integers stay <c>N/1</c>. Values within
    /// <see cref="KnownRateSnapFps"/> of a broadcast fraction (59.94, 29.97, 23.976) snap to that
    /// fraction. Anything else is a continued-fraction approximation and is NOT rounded to a
    /// nearby integer — a measured 29.2 must not become 30 or 29.97.
    /// </summary>
    public static RationalFrameRate FromDouble(double framesPerSecond)
    {
        if (double.IsNaN(framesPerSecond) || double.IsInfinity(framesPerSecond) || framesPerSecond <= 0)
        {
            return default;
        }

        double nearestInteger = Math.Round(framesPerSecond);
        if (nearestInteger >= 1 && nearestInteger <= 1000
            && Math.Abs(framesPerSecond - nearestInteger) <= IntegerSnapFps)
        {
            return FromInteger((int)nearestInteger);
        }

        for (int i = 0; i < KnownRates.Length; i++)
        {
            (int numerator, int denominator) = KnownRates[i];
            double exact = (double)numerator / denominator;
            if (Math.Abs(framesPerSecond - exact) <= KnownRateSnapFps)
            {
                return new RationalFrameRate(numerator, denominator);
            }
        }

        return Approximate(framesPerSecond, maxDenominator: 100_100);
    }

    /// <summary>
    /// Settings migration. A stored numerator/denominator wins. Otherwise a legacy integer fps
    /// becomes <c>fps/1</c>. Absent or invalid input stays invalid so the caller can apply its default.
    /// </summary>
    public static RationalFrameRate FromStored(int? legacyFramesPerSecond, int? numerator, int? denominator)
    {
        if (numerator is int storedNumerator && denominator is int storedDenominator
            && storedNumerator > 0 && storedDenominator > 0)
        {
            return new RationalFrameRate(storedNumerator, storedDenominator);
        }

        if (legacyFramesPerSecond is int legacy && legacy > 0)
        {
            return FromInteger(legacy);
        }

        return default;
    }

    /// <summary>
    /// Samples of <paramref name="sampleRate"/> that belong to <paramref name="frameIndex"/> so the
    /// long-term count is exact. 48000 Hz at 30/1 is 1600 every frame; 48000 Hz at 60000/1001
    /// alternates 800 and 801 and sums to exactly <c>48000 * 1001</c> samples per 60000 frames.
    /// </summary>
    public int SamplesForFrame(long frameIndex, int sampleRate)
    {
        if (!IsValid || sampleRate <= 0 || frameIndex < 0) return 0;
        long start = CumulativeSamples(frameIndex, sampleRate);
        long end = CumulativeSamples(frameIndex + 1, sampleRate);
        long count = end - start;
        if (count <= 0 || count > int.MaxValue) return 0;
        return (int)count;
    }

    /// <summary>Upper bound on <see cref="SamplesForFrame"/>, so one buffer can be rented for the life of the generator.</summary>
    public int MaxSamplesPerFrame(int sampleRate)
    {
        if (!IsValid || sampleRate <= 0) return 0;
        long floor = ((long)sampleRate * Denominator) / Numerator;
        if (floor >= int.MaxValue - 1) return int.MaxValue;
        return (int)floor + 1;
    }

    public override string ToString() => IsValid ? $"{Numerator}/{Denominator}" : "invalid";

    private long CumulativeSamples(long frameIndex, int sampleRate)
        => (long)(((Int128)frameIndex * sampleRate * Denominator) / Numerator);

    private static RationalFrameRate Approximate(double value, int maxDenominator)
    {
        long previousNumerator = 0;
        long previousDenominator = 1;
        long numerator = 1;
        long denominator = 0;
        double remainder = value;

        for (int iteration = 0; iteration < 32; iteration++)
        {
            long whole = (long)Math.Floor(remainder);
            long nextNumerator = whole * numerator + previousNumerator;
            long nextDenominator = whole * denominator + previousDenominator;
            if (nextDenominator > maxDenominator || nextNumerator > int.MaxValue || nextDenominator <= 0)
            {
                break;
            }

            previousNumerator = numerator;
            previousDenominator = denominator;
            numerator = nextNumerator;
            denominator = nextDenominator;

            double fraction = remainder - whole;
            if (fraction < 1e-12)
            {
                break;
            }

            remainder = 1.0 / fraction;
        }

        if (numerator <= 0 || denominator <= 0 || denominator > int.MaxValue)
        {
            return default;
        }

        return new RationalFrameRate((int)numerator, (int)denominator);
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            int next = a % b;
            a = b;
            b = next;
        }

        return a == 0 ? 1 : Math.Abs(a);
    }
}
