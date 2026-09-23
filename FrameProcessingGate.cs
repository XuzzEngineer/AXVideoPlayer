using System;

namespace AXVideoPlayer;

internal sealed class FrameProcessingGate
{
    private readonly object _sync = new();
    private bool _closed;
    private int _active;

    internal bool IsClosed { get { lock (_sync) return _closed; } }
    internal int ActiveCount { get { lock (_sync) return _active; } }

    internal bool TryEnter()
    {
        lock (_sync)
        {
            if (_closed) return false;
            _active++;
            return true;
        }
    }

    internal void Leave()
    {
        lock (_sync)
        {
            if (_active == 0) throw new InvalidOperationException("No frame is active.");
            _active--;
        }
    }

    internal void Close()
    {
        lock (_sync) _closed = true;
    }
}
