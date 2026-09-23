using System;
using System.IO;
using System.Threading;

namespace AXVideoPlayer
{
    internal sealed class CoalescedTextFileWriter : IDisposable
    {
        private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

        private readonly string _filePath;
        private readonly object _gate = new();
        private readonly object _writeGate = new();
        private readonly Timer _timer;
        private string? _pendingText;
        private bool _disposed;

        public CoalescedTextFileWriter(string filePath)
        {
            _filePath = filePath;
            _timer = new Timer(WritePendingCallback);
        }

        public void QueueWrite(string text)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _pendingText = text;
                _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Flush()
        {
            string? text;
            lock (_gate)
            {
                text = _pendingText;
                _pendingText = null;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            if (text != null)
                TryWrite(text);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            Flush();
            _timer.Dispose();
        }

        private void WritePendingCallback(object? state)
        {
            string? text;
            lock (_gate)
            {
                if (_disposed)
                    return;

                text = _pendingText;
                _pendingText = null;
            }

            if (text != null)
                TryWrite(text);
        }

        private void TryWrite(string text)
        {
            try
            {
                lock (_writeGate)
                {
                    File.WriteAllText(_filePath, text);
                }
            }
            catch
            {
                // Persistence is convenience state; never interrupt playback for write failures.
            }
        }
    }
}
