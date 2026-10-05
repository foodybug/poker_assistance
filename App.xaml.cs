using System.Configuration;
using System.Data;
using System.Windows;

namespace PokerAssistant;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
        void Log(string msg)
        {
            try { System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n"); } catch { }
        }

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Log($"[FATAL] AppDomain UnhandledException: {args.ExceptionObject}");
            MessageBox.Show($"오류가 발생하여 프로그램을 시작할 수 없습니다:\r\n{args.ExceptionObject}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            Log($"[ERROR] DispatcherUnhandledException: {args.Exception}");
            MessageBox.Show($"UI 스레드 오류가 발생했습니다:\r\n{args.Exception.Message}\r\n{args.Exception.StackTrace}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        Log("App.OnStartup started");

        if (e.Args.Length > 0 && e.Args[0] == "--test")
        {
            Log("Running verification tests...");
            int result = Tests.VerificationTest.RunAllTests();
            Log($"Tests completed with exit code: {result}");
            Environment.Exit(result);
            return;
        }

        Exit += (s, args) =>
        {
            Log($"App.Exit event fired with code: {args.ApplicationExitCode}");
        };

        base.OnStartup(e);
        Log("base.OnStartup completed");
    }
}

