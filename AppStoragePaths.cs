using System;
using System.IO;

namespace AXVideoPlayer
{
    internal static class AppStoragePaths
    {
        private const string AppDataFolderName = "AX Video Player Base";

        public static string GetUserDataFilePath(string fileName)
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            if (string.IsNullOrWhiteSpace(root))
                root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            string directory = Path.Combine(root, AppDataFolderName);
            Directory.CreateDirectory(directory);

            string newPath = Path.Combine(directory, fileName);
            TryMigrateOldBaseDirectoryFile(fileName, newPath);
            return newPath;
        }

        private static void TryMigrateOldBaseDirectoryFile(string fileName, string newPath)
        {
            try
            {
                if (File.Exists(newPath))
                    return;

                string oldPath = Path.Combine(AppContext.BaseDirectory, fileName);
                if (File.Exists(oldPath))
                    File.Copy(oldPath, newPath, false);
            }
            catch
            {
                // Migration is best-effort; the app can recreate defaults if this fails.
            }
        }
    }
}
