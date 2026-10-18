using System;
using System.Collections.Generic;
using System.Linq;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Forensics;

namespace OmtCaptureStudio.ViewModels;

/// <summary>
/// NET-019: the ONE thing the view model binds from <see cref="AsioInputPairSelector"/> — the pair
/// options, which of them is chosen, the hint, whether the control shows at all, and an optional
/// status announcement. Same shape as the ADR-0001 broadcast projection: the module owns the state
/// and the ordering, the view model only copies the result onto its own observables.
/// </summary>
/// <param name="IsVisible">False when there is nothing to choose (non-ASIO device, or a failed probe).</param>
/// <param name="Options">The selectable pairs; empty when the control is hidden.</param>
/// <param name="SelectedPair">The pair that should be selected, or <c>null</c>.</param>
/// <param name="Hint">Tooltip text explaining the current state, including a failure reason.</param>
/// <param name="Announcement">A line for <c>StatusText</c>, or <c>null</c> when this update should not announce.</param>
public sealed record AsioPairProjection(
    bool IsVisible,
    IReadOnlyList<AsioInputPairOption> Options,
    AsioInputPairOption? SelectedPair,
    string? Hint,
    string? Announcement)
{
    /// <summary>The projection for "no ASIO device selected" — hidden, empty, silent.</summary>
    public static AsioPairProjection Hidden { get; } =
        new(false, Array.Empty<AsioInputPairOption>(), null, null, null);
}

/// <summary>
/// NET-019: the ASIO input-pair probe lifecycle, extracted from <c>MainWindowViewModel</c> so it can
/// be exercised without constructing the 2,700-line view model.
///
/// <para>It owns the lazy probe (never during construction, never opening a driver unless the
/// operator actually picks one), the per-driver result cache, the in-flight guard, the
/// remembered per-driver pair choice, the retry-on-dropdown-open, and the degrade path when a driver
/// is held by a DAW or has gone away. It emits ONE <see cref="AsioPairProjection"/> per change.</para>
///
/// <para><b>The suppression flag is now this module's detail, not the host's.</b> It used to be a
/// raw bool on the view model, hand-set and hand-reset at five separate sites, which is a shape that
/// silently breaks the moment someone adds a sixth assignment and forgets the reset: the picker then
/// stops reacting to the operator entirely. Here the flag is private and only reachable through
/// <see cref="SuppressUserChoice"/>, a scope that cannot be forgotten because the compiler owns the
/// release.</para>
///
/// <para>Everything it touches the outside world through is injected — the driver probe, the UI
/// thread hop and a driver re-enumeration fallback — so it is headless-constructible with no ASIO
/// driver, no Avalonia application and no real threads.</para>
/// </summary>
public sealed class AsioInputPairSelector
{
    private readonly Func<AsioDriverInfo, AsioPairProbeResult> _probe;
    private readonly Action<Action> _postToUi;
    private readonly Func<HostAudioCaptureOption?> _currentOption;
    private readonly Func<IReadOnlyList<AsioDriverInfo>> _enumerateDrivers;

    /// <summary>Drivers seen while the audio dropdown was built, so a later probe can resolve one.</summary>
    private readonly Dictionary<Guid, AsioDriverInfo> _driversByClsid = new();

    /// <summary>One probe result per driver; re-opening the picker never re-opens the driver.</summary>
    private readonly Dictionary<Guid, AsioPairProbeResult> _pairCache = new();

    /// <summary>Remembered per driver (not per dropdown rebuild), so a chosen 3/4 survives a rebuild.</summary>
    private readonly Dictionary<Guid, int> _chosenPairIndex = new();

    private readonly HashSet<Guid> _probeInFlight = new();

    /// <summary>Depth, not a bool: nested scopes must not un-suppress each other.</summary>
    private int _suppressDepth;

    private IReadOnlyList<AsioInputPairOption> _options = Array.Empty<AsioInputPairOption>();
    private AsioInputPairOption? _selected;
    private string? _hint;
    private bool _isVisible;

    /// <summary>Raised on the caller's thread (the UI thread) for every state change.</summary>
    public event Action<AsioPairProjection>? ProjectionChanged;

    /// <param name="probe">Reads a driver's input channels. In production this is <see cref="AsioInputChannelProbe.Probe(AsioDriverInfo)"/>.</param>
    /// <param name="postToUi">Hands a callback back to the UI thread; headless builds run it inline.</param>
    /// <param name="currentOption">Reads the operator's CURRENT audio selection, re-checked when a probe lands.</param>
    /// <param name="enumerateDrivers">Fallback re-enumeration when a clsid is not in the registry cache; may be null.</param>
    public AsioInputPairSelector(
        Func<AsioDriverInfo, AsioPairProbeResult> probe,
        Action<Action> postToUi,
        Func<HostAudioCaptureOption?> currentOption,
        Func<IReadOnlyList<AsioDriverInfo>>? enumerateDrivers = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        _currentOption = currentOption ?? throw new ArgumentNullException(nameof(currentOption));
        _enumerateDrivers = enumerateDrivers ?? (static () => Array.Empty<AsioDriverInfo>());
    }

    /// <summary>True while a programmatic selection is in progress, so the host must not react to it.</summary>
    public bool IsSuppressingUserChoice => _suppressDepth > 0;

    /// <summary>
    /// Suppresses the host's user-choice handler for the duration of the scope. Use it around EVERY
    /// programmatic <c>SelectedAsioInputPair</c> assignment — that assignment is indistinguishable
    /// from an operator pick, and without the scope the picker reacts to itself.
    /// </summary>
    public IDisposable SuppressUserChoice() => new Suppression(this);

    /// <summary>Records a driver the audio dropdown just enumerated, so a later probe can resolve it.</summary>
    public void RegisterDriver(AsioDriverInfo driver)
    {
        if (driver is null || driver.Clsid == Guid.Empty) return;
        _driversByClsid[driver.Clsid] = driver;
    }

    /// <summary>The remembered pair for a driver (0 when none was ever chosen).</summary>
    public int ChosenPairIndex(Guid clsid) =>
        _chosenPairIndex.TryGetValue(clsid, out int saved) ? saved : 0;

    /// <summary>Remembers an operator's pair choice for a driver, surviving dropdown rebuilds.</summary>
    public void RememberChoice(Guid clsid, int firstChannelIndex) => _chosenPairIndex[clsid] = firstChannelIndex;

    /// <summary>Display name for a driver, falling back to a generic label when it is unknown.</summary>
    public string DriverDisplayName(Guid clsid) =>
        _driversByClsid.TryGetValue(clsid, out AsioDriverInfo? driver) ? driver.DisplayName : "ASIO driver";

    /// <summary>
    /// Human label for a remembered pair index, used while rebuilding the audio dropdown. Derived
    /// from the cached probe when there is one, and from the index alone when there is not.
    /// </summary>
    public string PairText(Guid clsid, int firstChannelIndex)
    {
        if (_pairCache.TryGetValue(clsid, out AsioPairProbeResult? result))
        {
            foreach (AsioInputPairOption pair in BuildPairs(result.Channels))
            {
                if (pair.FirstChannelIndex == firstChannelIndex) return pair.DisplayName;
            }
        }

        return firstChannelIndex % 2 == 0
            ? $"In {firstChannelIndex + 1}/{firstChannelIndex + 2}"
            : $"In {firstChannelIndex + 1}";
    }

    /// <summary>
    /// Derives the selectable stereo pairs from the driver's input channel count: channels 0+1, 2+3,
    /// 4+5 … with a trailing odd channel offered on its own (the capture source duplicates it to
    /// L+R). The ONE pair-derivation in the codebase — pure, so a test can call it directly.
    /// </summary>
    public static IReadOnlyList<AsioInputPairOption> BuildPairs(IReadOnlyList<AsioInputChannel>? channels)
    {
        var pairs = new List<AsioInputPairOption>();
        if (channels == null) return pairs;

        int count = channels.Count;
        for (int i = 0; i + 1 < count; i += 2)
        {
            pairs.Add(new AsioInputPairOption(i, $"In {i + 1}/{i + 2}"));
        }

        if (count % 2 == 1)
        {
            pairs.Add(new AsioInputPairOption(count - 1, $"In {count}"));
        }

        return pairs;
    }

    /// <summary>
    /// Keeps the pair picker in step with the selected audio option. Purely reactive: the options
    /// list holds real driver data only once a probe reports in, which is what keeps the host
    /// constructible with no ASIO driver (and no Avalonia app) at all.
    /// </summary>
    public void SyncSelection(HostAudioCaptureOption? option)
    {
        Guid clsid = option != null && option.IsAsio && option.AsioDriverClsid.HasValue
            ? option.AsioDriverClsid.Value
            : Guid.Empty;

        if (clsid == Guid.Empty)
        {
            ClearState();
            return;
        }

        // Visible immediately so the control exists while the probe is still running; the hint tells
        // the operator what is happening (a driver can take a second to answer).
        _isVisible = true;

        if (_pairCache.TryGetValue(clsid, out AsioPairProbeResult? cached))
        {
            ApplyProbe(option!, clsid, cached, announce: false);
            return;
        }

        _hint = "Probing driver for its input channels…";
        Raise();
        BeginProbe(clsid);
    }

    /// <summary>
    /// Re-arms the probe when the operator opens the pair picker after an earlier failure. A DAW may
    /// have released the driver since the last attempt, so exactly one retry is allowed.
    /// </summary>
    public void RetryIfUnusable(HostAudioCaptureOption? option)
    {
        if (option is not { IsAsio: true } || option.AsioDriverClsid is not Guid clsid) return;

        bool usable = _pairCache.TryGetValue(clsid, out AsioPairProbeResult? cached) && cached.Channels.Count > 0;
        if (usable || _probeInFlight.Contains(clsid)) return;

        _pairCache.Remove(clsid);
        _hint = "Re-probing driver for its input channels…";
        Raise();
        BeginProbe(clsid);
    }

    private void BeginProbe(Guid clsid)
    {
        if (_probeInFlight.Contains(clsid)) return;

        if (!_driversByClsid.TryGetValue(clsid, out AsioDriverInfo? driver))
        {
            try
            {
                driver = _enumerateDrivers().FirstOrDefault(d => d.Clsid == clsid);
            }
            catch (Exception ex)
            {
                AppLogger.LogInfo($"[ASIO] could not resolve driver {clsid:B} for probing: {ex.Message}");
                driver = null;
            }
        }

        if (driver == null)
        {
            _hint = "Driver is no longer registered on this machine; using input pair 1/2.";
            Raise();
            return;
        }

        _probeInFlight.Add(clsid);
        AsioDriverInfo target = driver;

        try
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                AsioPairProbeResult result = _probe(target);
                _postToUi(() => CompleteProbe(clsid, result));
            });
        }
        catch (Exception ex)
        {
            _probeInFlight.Remove(clsid);
            AppLogger.LogInfo($"[ASIO] input probe could not be scheduled: {ex.Message}");
            _hint = "Input probe could not run; using input pair 1/2.";
            Raise();
        }
    }

    /// <summary>Called on the UI thread when a probe lands; drops the result if the operator moved on.</summary>
    private void CompleteProbe(Guid clsid, AsioPairProbeResult result)
    {
        _probeInFlight.Remove(clsid);
        _pairCache[clsid] = result;

        HostAudioCaptureOption? option = _currentOption();
        if (option == null || !option.IsAsio || option.AsioDriverClsid != clsid)
        {
            return; // The operator moved on to another device meanwhile; the cache serves them later.
        }

        ApplyProbe(option, clsid, result, announce: true);
    }

    /// <summary>Populates the pair list from the probe result and degrades gracefully when it failed.</summary>
    private void ApplyProbe(HostAudioCaptureOption option, Guid clsid, AsioPairProbeResult result, bool announce)
    {
        try
        {
            var pairs = BuildPairs(result.Channels);

            if (pairs.Count == 0)
            {
                // Degrade exactly as before: the single "ASIO · name (In 1/2)" entry keeps working,
                // the picker hides itself, and the reason is surfaced without breaking anything.
                string reason = string.IsNullOrWhiteSpace(result.Reason)
                    ? "the driver reported no input channels"
                    : result.Reason!;

                AppLogger.LogInfo($"[ASIO] input channel probe failed for '{option.DisplayName}': {reason}");

                // One raise per path, and it carries the WHOLE projection — options, selection,
                // visibility and hint move together so the host can never observe half an update.
                using (SuppressUserChoice())
                {
                    _options = pairs;
                    _selected = null;
                    _isVisible = false;
                    _hint = $"Input pairs unavailable: {reason}. Using input pair 1/2.";
                    Raise(announce ? $"ASIO input probe unavailable ({reason}); keeping input pair 1/2." : null);
                }

                return;
            }

            AppLogger.LogInfo($"[ASIO] {DriverDisplayName(clsid)} exposes {result.Channels.Count} input channel(s).");

            using (SuppressUserChoice())
            {
                _options = pairs;
                _selected =
                    pairs.FirstOrDefault(p => p.FirstChannelIndex == option.AsioFirstInputChannelIndex)
                    ?? pairs.FirstOrDefault();
                _isVisible = true;
                Raise(announce
                    ? $"ASIO driver exposes {result.Channels.Count} input channel(s): {pairs.Count} pair(s) available."
                    : null);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not apply ASIO input pairs: {ex.Message}", ex);
        }
    }

    private void ClearState()
    {
        using (SuppressUserChoice())
        {
            _isVisible = false;
            _options = Array.Empty<AsioInputPairOption>();
            _selected = null;
            _hint = null;
            Raise();
        }
    }

    private void Raise(string? announcement = null) =>
        ProjectionChanged?.Invoke(new AsioPairProjection(_isVisible, _options, _selected, _hint, announcement));

    /// <summary>Scope object for <see cref="SuppressUserChoice"/>; releases on dispose, even on a throw.</summary>
    private sealed class Suppression : IDisposable
    {
        private AsioInputPairSelector? _owner;

        public Suppression(AsioInputPairSelector owner)
        {
            _owner = owner;
            owner._suppressDepth++;
        }

        public void Dispose()
        {
            AsioInputPairSelector? owner = _owner;
            _owner = null;
            if (owner != null) owner._suppressDepth--;
        }
    }
}
