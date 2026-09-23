# AX Video Player V2.3

AX Video Player is a Windows video and music player with playlists, resume playback, audio and video controls, and optional super resolution tools.

[Download the V2.3 Windows installer](https://github.com/XuzzEngineer/AXVideoPlayer/releases/download/v2.3.0/AXVideoPlayerSetup-V2.3.exe)

V2.3 adds a selectable subtitle-track menu for videos with multiple embedded subtitles. It also improves playback error logging, retries incomplete AI-tool downloads, and adds a manual GitHub update check under **Tools → Updates**. The player does not prompt for updates automatically.

To build from source on Windows, install the .NET 10 SDK and run:

```powershell
dotnet build AXVideoPlayer.csproj -c Release
dotnet run --project AXVideoPlayerSR.Tests/AXVideoPlayer.Tests.csproj -c Release
```

The installer contains a self-contained Windows build and LibVLC. Optional AI and FFmpeg tools are downloaded when first needed. Generated binaries and third-party payloads are not stored in this source repository. See [TESTING.md](TESTING.md) for optional network and subtitle-track checks.

## Disclaimer of warranty and limitation of liability

This software is provided "AS IS", without warranty of any kind, express or implied, including but not limited to the warranties of merchantability, fitness for a particular purpose, and non-infringement. In no event shall the author(s) or copyright holder(s) be liable for any claim, damages, or other liability, whether in an action of contract, tort, or otherwise, arising from, out of, or in connection with the software or the use or other dealings in the software.
