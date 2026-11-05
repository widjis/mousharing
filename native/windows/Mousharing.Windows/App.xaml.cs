using Microsoft.UI.Xaml;
using Mousharing.Windows.Interop;

namespace Mousharing.Windows;

public partial class App : Application
{
    private MainWindow? window;
    private readonly Mutex? instanceMutex;
    private readonly bool smokeTest = Environment.GetCommandLineArgs().Contains("--ui-smoke-test", StringComparer.Ordinal);
    private System.Threading.Timer? smokeTimeout;
    private readonly bool ownsInstance;
    public App()
    {
        if (smokeTest)
        {
            ownsInstance = true;
            DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
            DebugSettings.XamlResourceReferenceFailed += (_, error) => Console.Error.WriteLine("XAML resource lookup: " + error.Message);
            UnhandledException += (_, error) => { LogSmokeException(error.Exception); Environment.Exit(1); };
            smokeTimeout = new System.Threading.Timer(_ => { Console.Error.WriteLine("WinUI smoke test timed out before Loaded verification."); Environment.Exit(2); }, null, 12_000, Timeout.Infinite);
        }
        else instanceMutex = new Mutex(true, @"Local\Mousharing.Native.Windows", out ownsInstance);
        try { InitializeComponent(); }
        catch (Exception error) { if (smokeTest) { LogSmokeException(error); Environment.Exit(1); } throw; }
    }
    private static void LogSmokeException(Exception error)
    {
        Console.Error.WriteLine($"WinUI startup failure HRESULT=0x{error.HResult:X8}: {error}");
        for (Exception? current = error; current is not null; current = current.InnerException)
            foreach (System.Collections.DictionaryEntry item in current.Data)
                Console.Error.WriteLine($"Exception.Data[{item.Key}]: {item.Value}");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!ownsInstance)
        {
            nint existing = NativeMethods.FindWindow(null, "Mousharing");
            if (existing != 0) { NativeMethods.ShowWindow(existing, 9); NativeMethods.SetForegroundWindow(existing); }
            Exit();
            return;
        }
        try
        {
            window = new MainWindow(smokeTest);
            if (smokeTest)
            {
                if (window.Content is not FrameworkElement content) throw new InvalidOperationException("UI root missing");
                content.Loaded += async (_, _) =>
                {
                    try
                    {
                        await Task.Delay(500);
                        window.VerifySmokeAndClose();
                        smokeTimeout?.Dispose();
                        Environment.Exit(0);
                    }
                    catch (Exception error) { LogSmokeException(error); Environment.Exit(1); }
                };
            }
            window.Activate();
        }
        catch (Exception error) { if (smokeTest) { LogSmokeException(error); Environment.Exit(1); } throw; }
    }
}
