using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AXVideoPlayerInstaller;

internal static class Program
{
    private const string ProductName = "AX Video Player";
    private const string Publisher = "AX";
    private const string AppExeName = "AXVideoPlayer.exe";
    private const string UninstallKeyName = "AXVideoPlayerV22";
    private const string PreviousV20UninstallKeyName = "AXVideoPlayerSRFGV20";
    private const string PreviousUninstallKeyName = "AXVideoPlayerSRFGV14";
    private const string PreviousV13UninstallKeyName = "AXVideoPlayerSRFGV13";
    private const string PreviousV11UninstallKeyName = "SRFGV11";
    private const string PreviousSrfgUninstallKeyName = "AXVideoPlayerSRFG";
    private const string LegacyUninstallKeyName = "AXVideoPlayerBase";
    private const string CapabilitiesRootKeyPath = @"Software\AXVideoPlayerV22";
    private const string CapabilitiesKeyPath = CapabilitiesRootKeyPath + @"\Capabilities";
    private const string PreviousV20CapabilitiesRootKeyPath = @"Software\AXVideoPlayerSRFGV20";
    private const string PreviousV14CapabilitiesRootKeyPath = @"Software\AXVideoPlayerSRFGV14";
    private const string PreviousV13CapabilitiesRootKeyPath = @"Software\AXVideoPlayerSRFGV13";
    private const string PreviousV11CapabilitiesRootKeyPath = @"Software\SRFGV11";
    private const string PreviousCapabilitiesRootKeyPath = @"Software\AXVideoPlayerSRFG";
    private const string LegacyCapabilitiesRootKeyPath = @"Software\AXVideoPlayerBase";
    private const string RegisteredAppName = "AX Video Player";
    private const string PreviousV20RegisteredAppName = "AX Video Player SRFG V2.0";
    private const string PreviousRegisteredAppName = "AX Video Player SRFG V1.4";
    private const string PreviousV13RegisteredAppName = "AX Video Player SRFG V1.3";
    private const string PreviousV11RegisteredAppName = "SRFG V1.1";
    private const string PreviousSrfgRegisteredAppName = "AXVideoPlayerSRFG";
    private const string LegacyRegisteredAppName = "AX Video Player";
    private static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
        ".rm", ".rmvb", ".mpg", ".mpeg", ".mpe", ".m2v", ".ts", ".m2ts",
        ".mts", ".vob", ".ogv", ".ogm", ".asf", ".divx", ".f4v", ".3gp",
        ".3g2", ".mxf", ".dv"
    };
    private static readonly string[] AudioExtensions =
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma"
    };

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            RunUninstall();
            return;
        }

        if (!IsAdministrator())
        {
            MessageBox.Show("Please run this installer as administrator.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Application.Run(new InstallerForm());
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RunUninstall()
    {
        if (!IsAdministrator())
        {
            MessageBox.Show("Administrator permission is required to uninstall " + ProductName + ".", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        DialogResult result = MessageBox.Show("Uninstall " + ProductName + " from:\n\n" + installDir + "?", ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
            return;

        try
        {
            RemoveShortcuts();
            UnregisterFileAssociations();
            UnregisterUninstallEntry();
            NotifyShellAssociationsChanged();

            string quotedDir = Quote(installDir);
            var info = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 2 > nul & rmdir /s /q " + quotedDir)
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(info);
            MessageBox.Show(ProductName + " has been uninstalled.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Uninstall failed.\n\n" + ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private sealed class InstallerForm : Form
    {
        private readonly Panel _content = new() { Dock = DockStyle.Fill };
        private readonly Button _back = new() { Text = "Back", Width = 90, Enabled = false };
        private readonly Button _next = new() { Text = "Next", Width = 90 };
        private readonly Button _cancel = new() { Text = "Cancel", Width = 90 };
        private readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI", 18, FontStyle.Bold), ForeColor = Color.White };
        private readonly Label _subtitle = new() { AutoSize = true, Font = new Font("Segoe UI", 9), ForeColor = Color.Gainsboro };
        private readonly PictureBox _logo = new() { SizeMode = PictureBoxSizeMode.Zoom, Width = 56, Height = 56 };
        private int _page;
        private CheckBox? _agree;
        private TextBox? _installPath;
        private ProgressBar? _progress;
        private Label? _progressText;
        private Label? _progressPercent;

        public InstallerForm()
        {
            Text = ProductName + " Setup";
            Width = 760;
            Height = 540;
            MinimumSize = new Size(700, 500);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BackColor = Color.FromArgb(34, 34, 34);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            _logo.Image = LoadLogo();

            _content.Dock = DockStyle.None;
            _content.BackColor = Color.FromArgb(34, 34, 34);

            var header = new Panel { Height = 92, BackColor = Color.FromArgb(24, 24, 24), Padding = new Padding(22, 16, 22, 12) };
            Controls.Add(header);
            header.Controls.Add(_logo);
            _logo.Left = 22;
            _logo.Top = 18;
            header.Controls.Add(_title);
            _title.Left = 92;
            _title.Top = 18;
            header.Controls.Add(_subtitle);
            _subtitle.Left = 94;
            _subtitle.Top = 54;

            Controls.Add(_content);

            var footer = new Panel { Height = 66, BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(16) };
            Controls.Add(footer);
            footer.Controls.Add(_cancel);
            footer.Controls.Add(_next);
            footer.Controls.Add(_back);
            ConfigureButton(_back, enabled: false);
            ConfigureButton(_next);
            ConfigureButton(_cancel);
            _cancel.Top = _next.Top = _back.Top = 16;
            Layout += (_, _) =>
            {
                header.SetBounds(0, 0, ClientSize.Width, 92);
                footer.SetBounds(0, Math.Max(92, ClientSize.Height - 66), ClientSize.Width, 66);
                _content.SetBounds(0, 92, ClientSize.Width, Math.Max(0, ClientSize.Height - 158));
                _cancel.Left = footer.Width - _cancel.Width - 18;
                _next.Left = _cancel.Left - _next.Width - 10;
                _back.Left = _next.Left - _back.Width - 10;
            };

            _back.Click += (_, _) => ShowPage(Math.Max(0, _page - 1));
            _next.Click += async (_, _) => await NextAsync();
            _cancel.Click += (_, _) => Close();

            ShowPage(0);
        }

        private static void ConfigureButton(Button button, bool enabled = true)
        {
            button.Enabled = enabled;
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(245, 245, 245);
            button.ForeColor = Color.FromArgb(24, 24, 24);
            button.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 210);
            button.FlatAppearance.BorderSize = 1;
            button.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            button.UseVisualStyleBackColor = false;
        }

        private async Task NextAsync()
        {
            if (_page == 0)
            {
                ShowPage(1);
                return;
            }

            if (_page == 1)
            {
                if (_agree?.Checked != true)
                {
                    MessageBox.Show("Please accept the license agreement to continue.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                ShowPage(2);
                return;
            }

            if (_page == 2)
            {
                string path = _installPath?.Text.Trim() ?? "";
                if (!ValidateInstallPath(path))
                    return;

                ShowPage(3);
                await InstallAsync(path);
                ShowPage(4);
                return;
            }

            Close();
        }

        private void ShowPage(int page)
        {
            _page = page;
            _content.Controls.Clear();
            _back.Enabled = page is > 0 and < 3;
            _cancel.Enabled = page < 3;
            _next.Enabled = page != 3;
            _next.Text = page == 4 ? "Finish" : page == 2 ? "Install" : "Next";

            if (page == 0)
                BuildWelcomePage();
            else if (page == 1)
                BuildLicensePage();
            else if (page == 2)
                BuildLocationPage();
            else if (page == 3)
                BuildProgressPage();
            else
                BuildFinishPage();
        }

        private void BuildWelcomePage()
        {
            _title.Text = "Install " + ProductName;
            _subtitle.Text = "A polished setup package for " + ProductName + ".";
            AddBodyText("This installer will copy the application to your chosen installation folder, create Desktop and Start Menu shortcuts, register uninstall information, and register supported video file types with Windows Default Apps.");
        }

        private void BuildLicensePage()
        {
            _title.Text = "License Agreement";
            _subtitle.Text = "Please review the terms before installing.";

            _agree = new CheckBox
            {
                Text = "I accept the license agreement",
                ForeColor = Color.White,
                Dock = DockStyle.Bottom,
                Height = 42,
                Padding = new Padding(22, 0, 0, 10)
            };
            _content.Controls.Add(_agree);

            var text = new RichTextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(18, 18, 18),
                ForeColor = Color.WhiteSmoke,
                Font = new Font("Segoe UI", 10),
                Text = LicenseText(),
                Dock = DockStyle.Fill,
                Margin = new Padding(22)
            };
            _content.Controls.Add(text);
        }

        private void BuildLocationPage()
        {
            _title.Text = "Choose Install Location";
            _subtitle.Text = "Choose any valid installation drive and folder.";

            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22), BackColor = Color.FromArgb(34, 34, 34) };
            _content.Controls.Add(panel);
            var label = new Label { Text = "Install folder:", ForeColor = Color.White, AutoSize = true, Top = 28, Left = 22 };
            panel.Controls.Add(label);

            _installPath = new TextBox
            {
                Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName),
                Left = 22,
                Top = 58,
                Width = 560,
                Font = new Font("Segoe UI", 10),
                ForeColor = Color.Black,
                BackColor = Color.White
            };
            panel.Controls.Add(_installPath);

            var browse = new Button { Text = "Browse...", Left = 595, Top = 56, Width = 100, Height = 27 };
            ConfigureButton(browse);
            browse.Click += (_, _) =>
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Choose where to install " + ProductName,
                    SelectedPath = _installPath.Text,
                    UseDescriptionForTitle = true
                };

                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _installPath.Text = dialog.SelectedPath;
            };
            panel.Controls.Add(browse);
        }

        private void BuildProgressPage()
        {
            _title.Text = "Installing";
            _subtitle.Text = "Please wait while setup installs the application.";

            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22), BackColor = Color.FromArgb(34, 34, 34) };
            _content.Controls.Add(panel);
            _progressText = new Label
            {
                ForeColor = Color.White,
                AutoSize = false,
                Text = "Preparing...",
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 28,
                Font = new Font("Segoe UI", 10)
            };
            panel.Controls.Add(_progressText);
            _progressPercent = new Label
            {
                ForeColor = Color.Gainsboro,
                AutoSize = false,
                Text = "0%",
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 24,
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            };
            panel.Controls.Add(_progressPercent);
            _progress = new ProgressBar { Height = 26, Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous };
            panel.Controls.Add(_progress);
            void LayoutProgress()
            {
                int width = Math.Min(620, Math.Max(360, panel.ClientSize.Width - 80));
                int left = Math.Max(20, (panel.ClientSize.Width - width) / 2);
                int top = Math.Max(70, (panel.ClientSize.Height - 96) / 2);
                _progressText.SetBounds(left, top, width, 28);
                _progress.SetBounds(left, top + 38, width, 26);
                _progressPercent.SetBounds(left, top + 70, width, 24);
            }

            panel.Resize += (_, _) => LayoutProgress();
            panel.HandleCreated += (_, _) => LayoutProgress();
            LayoutProgress();
        }

        private void BuildFinishPage()
        {
            _title.Text = "Installation Complete";
            _subtitle.Text = ProductName + " is ready to use.";
            AddBodyText("Setup created the Desktop shortcut, Start Menu shortcut, uninstall registration, and Windows Default Apps file associations.");
        }

        private async Task InstallAsync(string installDir)
        {
            await Task.Run(() =>
            {
                Step(5, "Creating install folder...");
                Directory.CreateDirectory(installDir);

                Step(18, "Extracting application files...");
                string tempZip = Path.Combine(Path.GetTempPath(), "AXVideoPlayerPayload_" + Guid.NewGuid().ToString("N") + ".zip");
                using (Stream resource = GetRequiredResource("Payload.zip"))
                using (FileStream output = File.Create(tempZip))
                    resource.CopyTo(output);

                ZipFile.ExtractToDirectory(tempZip, installDir, true);
                File.Delete(tempZip);

                Step(55, "Installing uninstaller...");
                File.Copy(Application.ExecutablePath, Path.Combine(installDir, "Uninstall.exe"), true);

                string appPath = Path.Combine(installDir, AppExeName);
                Step(65, "Creating shortcuts...");
                CreateShortcuts(appPath);

                Step(76, "Registering Windows uninstall entry...");
                RegisterUninstallEntry(installDir, appPath);

                Step(88, "Registering video file types...");
                RegisterFileAssociations(appPath);
                NotifyShellAssociationsChanged();

                Step(100, "Done.");
            });
        }

        private void Step(int value, string text)
        {
            BeginInvoke(new Action(() =>
            {
                if (_progress != null)
                    _progress.Value = Math.Max(_progress.Minimum, Math.Min(_progress.Maximum, value));
                if (_progressText != null)
                    _progressText.Text = value.ToString("0") + "% - " + text;
                if (_progressPercent != null)
                    _progressPercent.Text = value.ToString("0") + "%";
            }));
        }

        private bool ValidateInstallPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show("Choose an install folder.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                MessageBox.Show("Choose a valid install folder.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string root = Path.GetPathRoot(fullPath) ?? "";
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(fullPath))
            {
                MessageBox.Show("Choose a full folder path on a valid installation drive.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string trimmedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmedPath, trimmedRoot, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Choose a folder under the drive, not the drive root.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            return true;
        }

        private void AddBodyText(string text)
        {
            var label = new Label
            {
                Text = text,
                ForeColor = Color.WhiteSmoke,
                Font = new Font("Segoe UI", 11),
                AutoSize = false,
                Dock = DockStyle.Fill,
                Padding = new Padding(24),
                TextAlign = ContentAlignment.TopLeft
            };
            _content.Controls.Add(label);
        }
    }

    private static Stream GetRequiredResource(string name)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        if (resourceName == null)
            throw new InvalidOperationException("Missing installer resource: " + name);

        return assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("Could not load installer resource: " + name);
    }

    private static Image? LoadLogo()
    {
        try
        {
            using Stream stream = GetRequiredResource("logo.png");
            return Image.FromStream(stream);
        }
        catch
        {
            return null;
        }
    }

    private static void CreateShortcuts(string appPath)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        string startFolder = Path.Combine(programs, ProductName);
        Directory.CreateDirectory(startFolder);

        CreateShortcut(Path.Combine(desktop, ProductName + ".lnk"), appPath, Path.GetDirectoryName(appPath)!);
        CreateShortcut(Path.Combine(startFolder, ProductName + ".lnk"), appPath, Path.GetDirectoryName(appPath)!);
        CreateShortcut(Path.Combine(startFolder, "Uninstall " + ProductName + ".lnk"), Path.Combine(Path.GetDirectoryName(appPath)!, "Uninstall.exe"), Path.GetDirectoryName(appPath)!);
    }

    private static void RemoveShortcuts()
    {
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), ProductName + ".lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), PreviousV20RegisteredAppName + ".lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), PreviousRegisteredAppName + ".lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), PreviousV13RegisteredAppName + ".lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), PreviousV11RegisteredAppName + ".lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), PreviousSrfgRegisteredAppName + ".lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "AX Video Player.lnk"));
        string startFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName);
        TryDelete(Path.Combine(startFolder, ProductName + ".lnk"));
        TryDelete(Path.Combine(startFolder, "Uninstall " + ProductName + ".lnk"));
        try
        {
            if (Directory.Exists(startFolder) && !Directory.EnumerateFileSystemEntries(startFolder).Any())
                Directory.Delete(startFolder);
        }
        catch { }
        string previousV20StartFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), PreviousV20RegisteredAppName);
        TryDelete(Path.Combine(previousV20StartFolder, PreviousV20RegisteredAppName + ".lnk"));
        TryDelete(Path.Combine(previousV20StartFolder, "Uninstall " + PreviousV20RegisteredAppName + ".lnk"));
        try
        {
            if (Directory.Exists(previousV20StartFolder) && !Directory.EnumerateFileSystemEntries(previousV20StartFolder).Any())
                Directory.Delete(previousV20StartFolder);
        }
        catch { }
        string previousStartFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), PreviousRegisteredAppName);
        TryDelete(Path.Combine(previousStartFolder, PreviousRegisteredAppName + ".lnk"));
        TryDelete(Path.Combine(previousStartFolder, "Uninstall " + PreviousRegisteredAppName + ".lnk"));
        try
        {
            if (Directory.Exists(previousStartFolder) && !Directory.EnumerateFileSystemEntries(previousStartFolder).Any())
                Directory.Delete(previousStartFolder);
        }
        catch { }
        string previousV13StartFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), PreviousV13RegisteredAppName);
        TryDelete(Path.Combine(previousV13StartFolder, PreviousV13RegisteredAppName + ".lnk"));
        TryDelete(Path.Combine(previousV13StartFolder, "Uninstall " + PreviousV13RegisteredAppName + ".lnk"));
        try
        {
            if (Directory.Exists(previousV13StartFolder) && !Directory.EnumerateFileSystemEntries(previousV13StartFolder).Any())
                Directory.Delete(previousV13StartFolder);
        }
        catch { }
        string previousV11StartFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), PreviousV11RegisteredAppName);
        TryDelete(Path.Combine(previousV11StartFolder, PreviousV11RegisteredAppName + ".lnk"));
        TryDelete(Path.Combine(previousV11StartFolder, "Uninstall " + PreviousV11RegisteredAppName + ".lnk"));
        try
        {
            if (Directory.Exists(previousV11StartFolder) && !Directory.EnumerateFileSystemEntries(previousV11StartFolder).Any())
                Directory.Delete(previousV11StartFolder);
        }
        catch { }
        string previousSrfgStartFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), PreviousSrfgRegisteredAppName);
        TryDelete(Path.Combine(previousSrfgStartFolder, PreviousSrfgRegisteredAppName + ".lnk"));
        TryDelete(Path.Combine(previousSrfgStartFolder, "Uninstall " + PreviousSrfgRegisteredAppName + ".lnk"));
        try
        {
            if (Directory.Exists(previousSrfgStartFolder) && !Directory.EnumerateFileSystemEntries(previousSrfgStartFolder).Any())
                Directory.Delete(previousSrfgStartFolder);
        }
        catch { }
        string legacyStartFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "AX Video Player");
        TryDelete(Path.Combine(legacyStartFolder, "AX Video Player.lnk"));
        TryDelete(Path.Combine(legacyStartFolder, "Uninstall AX Video Player.lnk"));
        try
        {
            if (Directory.Exists(legacyStartFolder) && !Directory.EnumerateFileSystemEntries(legacyStartFolder).Any())
                Directory.Delete(legacyStartFolder);
        }
        catch { }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = targetPath + ",0";
        shortcut.Description = ProductName;
        shortcut.Save();
    }

    private static void RegisterUninstallEntry(string installDir, string appPath)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName);
        key.SetValue("DisplayName", ProductName);
        key.SetValue("DisplayVersion", "2.3");
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", Quote(appPath) + ",0");
        key.SetValue("UninstallString", Quote(Path.Combine(installDir, "Uninstall.exe")) + " /uninstall");
        key.SetValue("QuietUninstallString", Quote(Path.Combine(installDir, "Uninstall.exe")) + " /uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", EstimateSizeKb(installDir), RegistryValueKind.DWord);
    }

    private static void UnregisterUninstallEntry()
    {
        TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName);
        TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + PreviousV20UninstallKeyName);
        TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + PreviousUninstallKeyName);
        TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + PreviousV13UninstallKeyName);
        TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + PreviousV11UninstallKeyName);
        TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + PreviousSrfgUninstallKeyName);
        TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + LegacyUninstallKeyName);
    }

    private static int EstimateSizeKb(string directory)
    {
        try
        {
            long bytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
            return (int)Math.Min(int.MaxValue, Math.Max(1, bytes / 1024));
        }
        catch
        {
            return 1;
        }
    }

    private static void RegisterFileAssociations(string appPath)
    {
        using RegistryKey registeredApps = Registry.LocalMachine.CreateSubKey(@"Software\RegisteredApplications");
        registeredApps.SetValue(RegisteredAppName, CapabilitiesKeyPath);

        using RegistryKey capabilities = Registry.LocalMachine.CreateSubKey(CapabilitiesKeyPath);
        capabilities.SetValue("ApplicationName", RegisteredAppName);
        capabilities.SetValue("ApplicationIcon", Quote(appPath) + ",0");
        capabilities.SetValue("ApplicationCompany", Publisher);
        capabilities.SetValue("ApplicationDescription", "Play video files with " + ProductName + ".");

        using RegistryKey associations = capabilities.CreateSubKey("FileAssociations");
        foreach (string extension in VideoExtensions.Concat(AudioExtensions))
        {
            string progId = GetProgId(extension);
            associations.SetValue(extension, progId);
            RegisterProgId(extension, progId, appPath);
        }

        using RegistryKey applications = Registry.LocalMachine.CreateSubKey(@"Software\Classes\Applications\" + AppExeName);
        applications.SetValue("ApplicationName", RegisteredAppName);
        applications.SetValue("ApplicationIcon", Quote(appPath) + ",0");
        applications.SetValue("FriendlyAppName", RegisteredAppName);
        using RegistryKey command = applications.CreateSubKey(@"shell\open\command");
        command.SetValue("", Quote(appPath) + " \"%1\"");
    }

    private static void RegisterProgId(string extension, string progId, string appPath)
    {
        using RegistryKey progKey = Registry.LocalMachine.CreateSubKey(@"Software\Classes\" + progId);
        progKey.SetValue("", ProductName + " " + extension.TrimStart('.').ToUpperInvariant() + " File");
        progKey.SetValue("FriendlyTypeName", ProductName + " " + extension.TrimStart('.').ToUpperInvariant() + " File");
        progKey.SetValue("AppUserModelID", "AX.VideoPlayer.SRFG");
        using (RegistryKey icon = progKey.CreateSubKey("DefaultIcon"))
            icon.SetValue("", Quote(appPath) + ",0");
        using (RegistryKey command = progKey.CreateSubKey(@"shell\open\command"))
            command.SetValue("", Quote(appPath) + " \"%1\"");

        using RegistryKey extKey = Registry.LocalMachine.CreateSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids");
        extKey.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    private static void UnregisterFileAssociations()
    {
        using (RegistryKey? registeredApps = Registry.LocalMachine.OpenSubKey(@"Software\RegisteredApplications", true))
        {
            registeredApps?.DeleteValue(RegisteredAppName, false);
            registeredApps?.DeleteValue(PreviousV20RegisteredAppName, false);
            registeredApps?.DeleteValue(PreviousRegisteredAppName, false);
            registeredApps?.DeleteValue(PreviousV13RegisteredAppName, false);
            registeredApps?.DeleteValue(PreviousV11RegisteredAppName, false);
            registeredApps?.DeleteValue(PreviousSrfgRegisteredAppName, false);
            registeredApps?.DeleteValue(LegacyRegisteredAppName, false);
        }

        TryDeleteSubKeyTree(Registry.LocalMachine, CapabilitiesRootKeyPath);
        TryDeleteSubKeyTree(Registry.LocalMachine, PreviousV20CapabilitiesRootKeyPath);
        TryDeleteSubKeyTree(Registry.LocalMachine, PreviousV14CapabilitiesRootKeyPath);
        TryDeleteSubKeyTree(Registry.LocalMachine, PreviousV13CapabilitiesRootKeyPath);
        TryDeleteSubKeyTree(Registry.LocalMachine, PreviousV11CapabilitiesRootKeyPath);
        TryDeleteSubKeyTree(Registry.LocalMachine, PreviousCapabilitiesRootKeyPath);
        TryDeleteSubKeyTree(Registry.LocalMachine, LegacyCapabilitiesRootKeyPath);
        TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Classes\Applications\" + AppExeName);

        foreach (string extension in VideoExtensions)
        {
            string progId = GetProgId(extension);
            TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Classes\" + progId);
            TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Classes\" + GetPreviousV20ProgId(extension));
            TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Classes\" + GetPreviousV14ProgId(extension));
            TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Classes\" + GetPreviousV13ProgId(extension));
            TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Classes\" + GetPreviousV11ProgId(extension));
            TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Classes\" + GetPreviousSrfgProgId(extension));
            TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Classes\" + GetPreviousProgId(extension));
            TryDeleteSubKeyTree(Registry.LocalMachine, @"Software\Classes\" + GetLegacyProgId(extension));
            using RegistryKey? extKey = Registry.LocalMachine.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids", true);
            extKey?.DeleteValue(progId, false);
            extKey?.DeleteValue(GetPreviousV20ProgId(extension), false);
            extKey?.DeleteValue(GetPreviousV14ProgId(extension), false);
            extKey?.DeleteValue(GetPreviousV13ProgId(extension), false);
            extKey?.DeleteValue(GetPreviousV11ProgId(extension), false);
            extKey?.DeleteValue(GetPreviousSrfgProgId(extension), false);
            extKey?.DeleteValue(GetPreviousProgId(extension), false);
            extKey?.DeleteValue(GetLegacyProgId(extension), false);
        }
    }

    private static void TryDeleteSubKeyTree(RegistryKey root, string path)
    {
        try { root.DeleteSubKeyTree(path, false); }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static string LicenseText()
    {
        return """
DISCLAIMER OF WARRANTY AND LIMITATION OF LIABILITY

This software is provided "AS IS", without warranty of any kind, express or implied, including but not limited to the warranties of merchantability, fitness for a particular purpose, and non-infringement.

In no event shall the author(s) or copyright holder(s) be liable for any claim, damages, or other liability, whether in an action of contract, tort, or otherwise, arising from, out of, or in connection with the software or the use or other dealings in the software.
""";
    }

    private static string Quote(string value) => "\"" + value.Trim('"') + "\"";

    private static string GetProgId(string extension) => "AXVideoPlayerV22." + extension.TrimStart('.').ToUpperInvariant();

    private static string GetPreviousV20ProgId(string extension) => "AXVideoPlayerSRFGV20." + extension.TrimStart('.').ToUpperInvariant();

    private static string GetPreviousV14ProgId(string extension) => "AXVideoPlayerSRFGV14." + extension.TrimStart('.').ToUpperInvariant();

    private static string GetPreviousV13ProgId(string extension) => "AXVideoPlayerSRFGV13." + extension.TrimStart('.').ToUpperInvariant();

    private static string GetPreviousV11ProgId(string extension) => "SRFGV11." + extension.TrimStart('.').ToUpperInvariant();

    private static string GetPreviousSrfgProgId(string extension) => "AXVideoPlayerSRFG." + extension.TrimStart('.').ToUpperInvariant();

    private static string GetPreviousProgId(string extension) => "AXVideoPlayerBase." + extension.TrimStart('.').ToUpperInvariant();

    private static string GetLegacyProgId(string extension) => "AXVideoPlayerBase" + extension;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void NotifyShellAssociationsChanged()
    {
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        const uint SHCNF_IDLIST = 0x0000;
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }
}
