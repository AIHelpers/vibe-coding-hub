using AiCodeAgent.Core.Audit;
using AiCodeAgent.Core.Diagnostics;

namespace AiCodeAgent.App.Services;

/// <summary>
/// The "Send report" action of the crash toast (E9-S6): opens a prefilled GitHub issue in the browser. The app itself sends
/// nothing; the user reviews and submits the issue on GitHub, or closes the page.
/// </summary>
public static class CrashReportSender
{
    /// <summary>Returns false when the browser could not be opened.</summary>
    public static bool Send(CrashNotice notice, IUrlLauncher launcher, CrashReportContext? context = null, SecretRedactor? redactor = null)
    {
        var report = CrashReportBuilder.Build(
            notice.Details,
            context ?? CrashReportContext.FromCurrentProcess(AppInfo.CurrentVersion.ToString()),
            redactor);
        return launcher.Open(report.IssueUrl);
    }
}
