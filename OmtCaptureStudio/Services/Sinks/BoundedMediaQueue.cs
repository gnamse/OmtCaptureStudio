using System;
using System.Collections.Generic;
using System.Threading;

namespace OmtCaptureStudio.Services.Sinks;

public class BoundedMediaQueue<T> : IDisposable
{
    private readonly Queue<T> _queue = new();
    private readonly object _lock = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly int _capacity;
    private readonly Action<T> _onDrop;
    private long _droppedCount;
    private bool _isAddingCompleted;
    private bool _disposed;

    public BoundedMediaQueue(int capacity, Action<T> onDrop)
    {
        _capacity = capacity;
        _onDrop = onDrop ?? throw new ArgumentNullException(nameof(onDrop));
    }

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public void Enqueue(T item)
    {
        lock (_lock)
        {
            if (_disposed || _isAddingCompleted)
            {
                try { _onDrop(item); } catch { }
                Interlocked.Increment(ref _droppedCount);
                return;
            }

            if (_queue.Count >= _capacity)
            {
                var dropped = _queue.Dequeue();
                try { _onDrop(dropped); } catch { }
                Interlocked.Increment(ref _droppedCount);
            }

            _queue.Enqueue(item);
            try { _signal.Set(); } catch { }
        }
    }

    public bool TryDequeue(out T item)
    {
        lock (_lock)
        {
            if (_queue.Count > 0)
            {
                item = _queue.Dequeue();
                return true;
            }
            item = default!;
            return false;
        }
    }

    public void CompleteAdding()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _isAddingCompleted = true;
            try { _signal.Set(); } catch { }
        }
    }

    public bool Wait(int millisecondsTimeout)
    {
        try
        {
            return _signal.WaitOne(millisecondsTimeout);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public bool IsAddingCompleted
    {
        get
        {
            lock (_lock)
            {
                return _isAddingCompleted;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _isAddingCompleted = true;
            try { _signal.Set(); } catch { }
            while (_queue.Count > 0)
            {
                var item = _queue.Dequeue();
                try
                {
                    _onDrop(item);
                }
                catch { }
            }
        }
        try { _signal.Dispose(); } catch { }
    }
}
