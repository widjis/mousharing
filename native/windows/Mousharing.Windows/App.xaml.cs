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
            UnhandledException += (_, error) => { Console.Error.WriteLine(error.Exception); Environment.Exit(1); };
            smokeTimeout = new System.Threading.Timer(_ => { Console.Error.WriteLine("WinUI smoke test timed out before Loaded verification."); Environment.Exit(2); }, null, 12_000, Timeout.Infinite);
        }
        else instanceMutex = new Mutex(true, @"Local\Mousharing.Native.Windows", out ownsInstance);
        try { InitializeComponent(); }
        catch (Exception error) { if (smokeTest) { Console.Error.WriteLine(error); Environment.Exit(1); } throw; }
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
                    catch (Exception error) { Console.Error.WriteLine(error); Environment.Exit(1); }
                };
            }
            window.Activate();
        }
        catch (Exception error) { if (smokeTest) { Console.Error.WriteLine(error); Environment.Exit(1); } throw; }
    }
}
