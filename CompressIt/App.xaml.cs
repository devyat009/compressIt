using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace CompressIt;

public partial class App : Application
{
    private const string MutexName = "CompressIt_SingleInstance_Mutex_v1";
    private const string PipeName = "CompressIt_SingleInstance_Pipe_v1";

    private Mutex? _instanceMutex;
    private CancellationTokenSource? _pipeCts;

    public static string? InitialFileToOpen { get; private set; }
    public static event Action<string>? ExternalFileRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        string? fileArg = e.Args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && File.Exists(a));

        _instanceMutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            if (!string.IsNullOrWhiteSpace(fileArg))
            {
                SendFileToRunningInstance(fileArg);
            }
            Shutdown();
            return;
        }

        InitialFileToOpen = fileArg;
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogCrash("UnhandledException", ex);
        };

        try
        {
            LibVLCSharp.Shared.Core.Initialize();
        }
        catch (Exception ex)
        {
            LogCrash("LibVLCInit", ex);
        }

        StartPipeListener();
        base.OnStartup(e);

        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                Wpf.Ui.Appearance.ApplicationTheme.Dark,
                Wpf.Ui.Controls.WindowBackdropType.Mica,
                false);
        }
        catch { }

        try
        {
            var win = new MainWindow();
            win.Show();
        }
        catch (Exception ex)
        {
            LogCrash("MainWindowCreateShow", ex);
            Shutdown(1);
        }
    }

    private static void LogCrash(string stage, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompressIt");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:O}] {stage}: {ex}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void SendFileToRunningInstance(string filePath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(filePath);
        }
        catch
        {
            // Ignore if existing instance is closing.
        }
    }

    private void StartPipeListener()
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server);
                    var path = await reader.ReadLineAsync(token);
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        Dispatcher.Invoke(() => ExternalFileRequested?.Invoke(path));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(250, token);
                }
            }
        }, token);
    }
}
