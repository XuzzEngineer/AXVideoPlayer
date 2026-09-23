using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AXVideoPlayer;
using LibVLCSharp.Shared;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Failed: " + name);
    Console.WriteLine("PASS " + name);
}

Check(PlaybackRules.ClampSeekTime(10_000, -5) == 0, "seek before start");
Check(PlaybackRules.ClampSeekTime(10_000, 30_000) == 9_999, "seek beyond end");
Check(PlaybackRules.ClampSeekTime(0, 100) == 0, "seek before duration known");
Check(PlaybackRules.IsSameMedia("C:\\videos\\movie.mp4", "c:\\VIDEOS\\movie.mp4"), "resume same video");
Check(!PlaybackRules.IsSameMedia("C:\\videos\\one.mp4", "C:\\videos\\two.mp4"), "resume ignores switched video");
Check(PlaybackRules.ShouldClearSubtitle(0, 1, false), "switch clears subtitles");
Check(!PlaybackRules.ShouldClearSubtitle(0, 1, true), "switch preserves requested subtitle");
Check(PlaybackRules.AudioDelayMicroseconds(-250) == -250_000, "audio advances by 250 ms");
Check(PlaybackRules.AudioDelayMicroseconds(3000) == 2_000_000, "audio delay limit");
Check(!UpdateCheckService.CompareRelease("v2.2.0", new Version(2, 2, 0, 0)).Newer, "same GitHub release");
Check(UpdateCheckService.CompareRelease("v2.3.0", new Version(2, 2, 0, 0)).Newer, "new GitHub release");
Check(!UpdateCheckService.CompareRelease("v2.2.0", new Version(2, 3, 0, 0)).Newer, "older GitHub release");
if (args.Contains("--github"))
{
    var release = await UpdateCheckService.CheckAsync();
    Check(release.Message.Contains("latest release") || release.Message.Contains("available"), "live GitHub update check");
}
int subtitleArg = Array.IndexOf(args, "--subtitles");
if (subtitleArg >= 0)
{
    string path = args[subtitleArg + 1];
    Core.Initialize();
    using var vlc = new LibVLC("--aout=dummy", "--vout=dummy");
    using var player = new MediaPlayer(vlc);
    using var media = new Media(vlc, new Uri(path));
    player.Media = media;
    Check(player.Play(), "subtitle fixture starts playback");
    var tracks = Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>();
    for (int i = 0; i < 40; i++)
    {
        tracks = player.SpuDescription ?? tracks;
        if (tracks.Count(t => t.Id >= 0) >= 2) break;
        await Task.Delay(100);
    }
    var choices = tracks.Where(t => t.Id >= 0).ToArray();
    Console.WriteLine($"VLC state={player.State}, SpuCount={player.SpuCount}, tracks={string.Join(",", tracks.Select(t => $"{t.Id}:{t.Name}"))}");
    Check(choices.Length >= 2, "VLC exposes both subtitle tracks");
    Check(player.SetSpu(choices[0].Id), "first subtitle track selected");
    Check(player.SetSpu(choices[1].Id), "second subtitle track selected");
    for (int i = 0; i < 10 && player.Spu != choices[1].Id; i++) await Task.Delay(50);
    Check(player.Spu == choices[1].Id, "second subtitle remains active");
    player.Stop();
}

var gate = new FrameProcessingGate();
Check(gate.TryEnter(), "processing starts while open");
gate.Close();
Check(!gate.TryEnter() && gate.ActiveCount == 1, "close rejects new processing");
gate.Leave();
Check(gate.ActiveCount == 0, "in-flight processing finishes after close");

string root = Path.Combine(Path.GetTempPath(), "AXVideoPlayerTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    byte[] archiveBytes;
    using (var buffer = new MemoryStream())
    {
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = archive.CreateEntry("bin/tool.exe").Open())
            entry.Write(Encoding.UTF8.GetBytes("test tool"));
        archiveBytes = buffer.ToArray();
    }

    string zipPath = Path.Combine(root, "tool.zip");
    string installPath = Path.Combine(root, "installed");
    File.WriteAllText(zipPath, "broken cached download");

    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    int requests = 0;
    Task server = Task.Run(async () =>
    {
        while (requests < 2)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            requests++;
            int sent = requests == 1 ? archiveBytes.Length / 2 : archiveBytes.Length;
            byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {archiveBytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(archiveBytes.AsMemory(0, sent));
        }
    });
    await ToolArchiveDownloader.DownloadAndExtractAsync($"http://127.0.0.1:{port}/tool.zip", zipPath, installPath, new[] { "tool.exe" }, CancellationToken.None);
    await server;
    Check(requests == 2, "incomplete download retried");
    Check(File.Exists(Path.Combine(installPath, "bin", "tool.exe")), "valid tool extracted");
    Check(!File.Exists(zipPath + ".partial"), "partial file removed");
}
finally
{
    Directory.Delete(root, recursive: true);
}

Console.WriteLine("All playback and download tests passed.");
