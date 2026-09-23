# AX Video Player checks

Use the .NET 10 SDK on Windows:

```powershell
dotnet run --project AXVideoPlayerSR.Tests/AXVideoPlayer.Tests.csproj
```

Add `-- --github` to exercise the live GitHub release check.
Add `-- --subtitles "C:\path\to\video-with-two-subtitles.mp4"` to verify VLC discovers and switches between two embedded subtitle tracks.

The automated checks cover seek and resume decisions, switching media, audio delay conversion, closing while a frame is active, release version comparison, and recovery from a broken or incomplete tool ZIP. The download check uses a local HTTP server and does not contact the tool vendors.

These are logic and download tests. Playback quality, subtitle rendering, and device specific GPU behavior still require a real media and hardware smoke test.
