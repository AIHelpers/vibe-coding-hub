using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Serilog;

namespace AiCodeAgent.App.Services;

/// <summary>A user-facing notice about an exception that was logged and handled.</summary>
/// <param name="Message">Short text for the toast.</param>
/// <param name="Details">Full exception text, for the "Copy details" action.</param>
public sealed record CrashNotice(string Message, string Details);

/// <summary>
/// Last-resort exception handling. Logs every exception nobody else caught, keeps the app running when that is
/// safe, and tells the user through <see cref="NoticeRaised"/> (always raised on the UI thread).
/// </summary>
public static class CrashReporter
{
    private const string UserMessage = "Something went wrong. Details copied to log.";

    private static readonly object Gate = new();
    private static bool _processHooksInstalled;
    private static bool _uiHookInstalled;

    /// <summary>Raised on the UI thread for each handled exception the user should know about.</summary>
    public static event Action<CrashNotice>? NoticeRaised;

    /// <summary>Hooks process-wide handlers (AppDomain and unobserved tasks). Safe to call more than once.</summary>
    public static void InstallProcessHandlers()
    {
        lock (Gate)
        {
            if (_processHooksInstalled) return;
            _processHooksInstalled = true;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            OnFatal(e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()), e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => OnUnobservedTask(e);
    }

    /// <summary>Hooks the UI dispatcher. Call once from framework initialization, on the UI thread.</summary>
    public static void InstallUiHandler()
    {
        lock (Gate)
        {
            if (_uiHookInstalled) return;
            _uiHookInstalled = true;
        }

        Dispatcher.UIThread.UnhandledException += (_, e) =>
            e.Handled = HandleUiException(e.Exception, "UI thread");
    }

    /// <summary>Logs a UI-thread exception. Returns true when the app can keep running.</summary>
    internal static bool HandleUiException(Exception ex, string origin)
    {
        Log.Error(ex, "Unhandled exception on {Origin}", origin);

        // After running out of memory, carrying on is not safe: let the process fail visibly.
        if (ex is OutOfMemoryException)
            return false;

        Notify(new CrashNotice(UserMessage, ex.ToString()));
        return true;
    }

    /// <summary>Logs a faulted task nobody awaited, so it cannot terminate the process.</summary>
    internal static void OnUnobservedTask(UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
        Notify(new CrashNotice(UserMessage, e.Exception.ToString()));
    }

    private static void OnFatal(Exception ex, bool isTerminating)
    {
        Log.Fatal(ex, "Unhandled exception (terminating: {IsTerminating})", isTerminating);
        if (isTerminating)
            Log.CloseAndFlush();
    }

    private static void Notify(CrashNotice notice)
    {
        var handler = NoticeRaised;
        if (handler is null)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            handler(notice);
        else
            Dispatcher.UIThread.Post(() => handler(notice));
    }
}
