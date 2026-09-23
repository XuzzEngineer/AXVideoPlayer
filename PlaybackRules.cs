using System;
using System.IO;

namespace AXVideoPlayer;

internal static class PlaybackRules
{
    internal static long ClampSeekTime(long lengthMs, long requestedMs) =>
        lengthMs <= 0 ? 0 : Math.Clamp(requestedMs, 0, lengthMs - 1);

    internal static bool ShouldClearSubtitle(int currentIndex, int nextIndex, bool keepCurrentSubtitle) =>
        currentIndex != nextIndex && !keepCurrentSubtitle;

    internal static long AudioDelayMicroseconds(long delayMs) => Math.Clamp(delayMs, -2000, 2000) * 1000;

    internal static bool IsSameMedia(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }
}
