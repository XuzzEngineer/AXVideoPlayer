using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AXVideoPlayer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (e.Args.Length > 0)
            window.OpenFilesFromCommandLine(e.Args);
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        e.Handled = true;
        MessageBox.Show("AX Video Player hit an error.\n\n" + e.Exception.Message, "AX Video Player", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            LogException(exception);
    }

    internal static void LogException(Exception exception)
    {
        try
        {
            string path = AppStoragePaths.GetUserDataFilePath("AXVideoPlayer.error.log");
            File.AppendAllText(path, DateTime.Now.ToString("u") + Environment.NewLine + exception + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
        }
    }
}
