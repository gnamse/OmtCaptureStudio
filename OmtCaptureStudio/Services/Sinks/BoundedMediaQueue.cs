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

    public BoundedMediaQueue(int capacity, Action<T> onDrop)
    {
        _capacity = capacity;
        _onDrop = onDrop;
    }

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public void Enqueue(T item)
    {
        lock (_lock)
        {
            if (_isAddingCompleted)
            {
                _onDrop(item);
                return;
            }

            if (_queue.Count >= _capacity)
            {
                var dropped = _queue.Dequeue();
                _onDrop(dropped);
                Interlocked.Increment(ref _droppedCount);
            }

            _queue.Enqueue(item);
            _signal.Set();
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
            _isAddingCompleted = true;
            _signal.Set();
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
            _isAddingCompleted = true;
            _signal.Set();
            while (_queue.Count > 0)
            {
                _onDrop(_queue.Dequeue());
            }
        }
        try { _signal.Dispose(); } catch { }
    }
}
