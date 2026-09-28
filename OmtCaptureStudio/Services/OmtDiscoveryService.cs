using System.Diagnostics;
using libomtnet;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

public class OmtDiscoveryService : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly OMTDiscovery _discovery;
    private readonly HashSet<string> _knownAddresses = new();
    private bool _disposed;

    public event Action<List<OmtSourceInfo>>? SourcesUpdated;

    public OmtDiscoveryService()
    {
        _discovery = OMTDiscovery.GetInstance();
        _timer = new System.Timers.Timer(1500);
        _timer.Elapsed += (s, e) => CheckSources();
    }

    public void Start()
    {
        _timer.Start();
        Task.Run(() => CheckSources(false));
    }

    public void Stop()
    {
        _timer.Stop();
    }

    private bool _hasDiscoveredOnce;

    public void RefreshNow()
    {
        lock (_knownAddresses)
        {
            _hasDiscoveredOnce = false;
        }
        Task.Run(() => CheckSources(forceNotify: true));
    }

    private void CheckSources(bool forceNotify = false)
    {
        try
        {
            string[] addresses = _discovery.GetAddresses();
            var list = new List<OmtSourceInfo>();

            if (addresses != null)
            {
                foreach (var addr in addresses)
                {
                    if (string.IsNullOrWhiteSpace(addr)) continue;
                    list.Add(OmtSourceInfo.Parse(addr, isManual: false));
                }
            }

            lock (_knownAddresses)
            {
                var newSet = list.Select(s => s.Address).ToHashSet();
                if (!forceNotify && _hasDiscoveredOnce && newSet.SetEquals(_knownAddresses))
                {
                    return; // No change in discovered sources, avoid disturbing UI
                }

                _knownAddresses.Clear();
                foreach (var a in newSet) _knownAddresses.Add(a);
                _hasDiscoveredOnce = true;
            }

            SourcesUpdated?.Invoke(list);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OMT Discovery Error] {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
