using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AXVideoPlayer;

internal static class ToolArchiveDownloader
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(15) };
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    internal static async Task DownloadAndExtractAsync(string url, string zipPath, string destinationDirectory, string[] requiredExecutables, CancellationToken cancellationToken)
    {
        await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DownloadAndExtractCoreAsync(url, zipPath, destinationDirectory, requiredExecutables, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private static async Task DownloadAndExtractCoreAsync(string url, string zipPath, string destinationDirectory, string[] requiredExecutables, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        string stagingDirectory = destinationDirectory + ".staging";

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!IsValidZip(zipPath, requiredExecutables))
                {
                    File.Delete(zipPath);
                    string partialPath = zipPath + ".partial";
                    try
                    {
                        using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        await using (FileStream target = new(partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

                        if (response.Content.Headers.ContentLength is long expected && new FileInfo(partialPath).Length != expected)
                            throw new InvalidDataException("The tool download was incomplete.");
                        if (!IsValidZip(partialPath, requiredExecutables))
                            throw new InvalidDataException("The tool download is not a valid ZIP archive.");
                        File.Move(partialPath, zipPath, overwrite: true);
                    }
                    finally
                    {
                        File.Delete(partialPath);
                    }
                }

                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
                Directory.CreateDirectory(stagingDirectory);
                ZipFile.ExtractToDirectory(zipPath, stagingDirectory);
                foreach (string required in requiredExecutables)
                    if (!Directory.EnumerateFiles(stagingDirectory, required, SearchOption.AllDirectories).Any())
                        throw new InvalidDataException("The tool archive is missing " + required + ".");

                if (Directory.Exists(destinationDirectory))
                    Directory.Delete(destinationDirectory, recursive: true);
                Directory.Move(stagingDirectory, destinationDirectory);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                File.Delete(zipPath);
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
                if (attempt == 3)
                    throw new IOException("Could not download or extract the required tool after three attempts.", ex);
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsValidZip(string path, string[] requiredExecutables)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            return archive.Entries.Count > 0 && requiredExecutables.All(required =>
                archive.Entries.Any(entry => string.Equals(Path.GetFileName(entry.FullName), required, StringComparison.OrdinalIgnoreCase)));
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
    }
}
