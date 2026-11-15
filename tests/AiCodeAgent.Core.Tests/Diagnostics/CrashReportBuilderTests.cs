using AiCodeAgent.Core.Audit;
using AiCodeAgent.Core.Diagnostics;
using AiCodeAgent.Core.Updates;
using Xunit;

namespace AiCodeAgent.Core.Tests.Diagnostics;

public class CrashReportBuilderTests
{
    private static readonly CrashReportContext Context = new("1.2.0", "Ubuntu 24.04", ".NET 10.0.0", "X64");

    // No environment values and no extra secrets: only the shape-based rules apply, so tests do not depend on the machine.
    private static readonly SecretRedactor NoEnv = new(new Dictionary<string, string>());

    private static CrashReport Build(string text, string? home = "/home/alice") => CrashReportBuilder.Build(text, Context, NoEnv, home);

    private const string Stack =
        "System.InvalidOperationException: binding failed\n" +
        "   at AiCodeAgent.App.ViewModels.ChatViewModel.Send() in /home/alice/src/App/ChatViewModel.cs:line 42\n" +
        "   at Avalonia.Threading.Dispatcher.Invoke()";

    [Fact]
    public void Title_StartsWithCrashAndTheFirstLine()
    {
        Assert.Equal("Crash: System.InvalidOperationException: binding failed", Build(Stack).Title);
    }

    [Fact]
    public void Title_IsShortenedWhenTheMessageIsLong()
    {
        var report = Build("System.Exception: " + new string('x', 500));

        Assert.True(report.Title.Length <= 120);
        Assert.EndsWith("…", report.Title);
    }

    [Fact]
    public void Body_HasTheEnvironmentAndTheException()
    {
        var body = Build(Stack).Body;

        Assert.Contains("Version: 1.2.0", body);
        Assert.Contains("Ubuntu 24.04 (X64)", body);
        Assert.Contains(".NET 10.0.0", body);
        Assert.Contains("binding failed", body);
        Assert.Contains("Nothing has been sent", body);
    }

    [Fact]
    public void TheHomeFolderIsReplacedByATilde()
    {
        var report = Build(Stack);

        Assert.DoesNotContain("alice", report.Body);
        Assert.Contains("~/src/App/ChatViewModel.cs", report.Body);
    }

    [Theory]
    [InlineData(@"at X() in C:\Users\Bob Smith\src\a.cs", "Bob")]
    [InlineData(@"at X() in C:\Users\Bob Smith\src\a.cs", "Smith")]
    [InlineData(@"at X() in c:/users/carol/src/a.cs", "carol")]
    [InlineData("at X() in /Users/dave/src/a.cs", "dave")]
    [InlineData("at X() in /home/erin/src/a.cs", "erin")]
    public void UserNamesInUserFoldersAreRemovedEvenWithoutAKnownHome(string line, string userName)
    {
        var report = Build("System.Exception: boom\n   " + line, home: null);

        Assert.DoesNotContain(userName, report.Body);
        Assert.Contains("<user>", report.Body);
    }

    [Fact]
    public void WindowsHomeFolderWithBackslashesIsReplaced()
    {
        var report = Build(@"System.Exception: x" + "\n" + @"   at Y() in C:\Users\frank\repo\a.cs", home: @"C:\Users\frank");

        Assert.DoesNotContain("frank", report.Body);
        Assert.Contains(@"~\repo\a.cs", report.Body);
    }

    [Fact]
    public void SecretsAreRemovedFromTheTextAndTheLink()
    {
        var report = Build("System.Exception: call failed with key sk-ant-abcdefghijklmnop1234 and Authorization: Bearer abcdefghijklmnopqrstuv");

        Assert.DoesNotContain("sk-ant-abcdefghijklmnop1234", report.Body);
        Assert.DoesNotContain("abcdefghijklmnopqrstuv", report.Body);
        Assert.DoesNotContain("abcdefghijklmnop1234", report.IssueUrl);
        Assert.Contains(SecretRedactor.Mask, report.Body);
    }

    [Fact]
    public void ConfiguredSecretValuesAreRemoved()
    {
        var redactor = new SecretRedactor(new Dictionary<string, string> { ["MY_API_KEY"] = "hunter2hunter2" });

        var report = CrashReportBuilder.Build("System.Exception: failed for hunter2hunter2", Context, redactor, "/home/alice");

        Assert.DoesNotContain("hunter2hunter2", report.Body);
        Assert.DoesNotContain("hunter2hunter2", report.Title);
    }

    [Fact]
    public void TheLinkOpensANewIssueOnGitHubAndPassesTheSafetyCheckOfTheLauncher()
    {
        var report = Build(Stack);

        Assert.StartsWith("https://github.com/AIHelpers/vibe-coding-hub/issues/new?", report.IssueUrl);
        Assert.True(UpdateUrlGuard.IsSafe(report.IssueUrl));
        Assert.Contains("title=" + Uri.EscapeDataString(report.Title), report.IssueUrl);
        Assert.Contains("body=" + Uri.EscapeDataString(report.Body), report.IssueUrl);
    }

    [Fact]
    public void AShortExceptionIsNotTruncated()
    {
        var report = Build(Stack);

        Assert.False(report.Truncated);
        Assert.DoesNotContain("shortened", report.Body);
    }

    [Fact]
    public void ALongStackIsCutToFitTheLinkAndSaysSo()
    {
        var lines = Enumerable.Range(0, 400).Select(i => $"   at Some.Namespace.SomeType.Method{i}(String argument) in /home/alice/src/File{i}.cs:line {i}");
        var report = Build("System.Exception: deep\n" + string.Join("\n", lines));

        Assert.True(report.Truncated);
        Assert.True(report.IssueUrl.Length <= CrashReportBuilder.MaxUrlLength);
        Assert.Contains("shortened", report.Body);
        Assert.Contains("System.Exception: deep", report.Body);
        Assert.Contains("Method0(", report.Body);
    }

    [Fact]
    public void TheCutKeepsWholeLines()
    {
        var lines = Enumerable.Range(0, 400).Select(i => $"   at Type.Method{i}() in /home/alice/src/File{i}.cs:line {i}");
        var report = Build("System.Exception: deep\n" + string.Join("\n", lines));

        var kept = report.Body.Split('\n').Where(l => l.StartsWith("   at Type.Method", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(kept);
        Assert.All(kept, l => Assert.Matches(@"line \d+\r?$", l));
    }

    [Fact]
    public void AMultiByteHeavyTextStillFitsAndNeverSplitsASurrogatePair()
    {
        // Every emoji is two UTF-16 chars and many bytes once percent-encoded.
        var report = Build("System.Exception: " + string.Concat(Enumerable.Repeat("😀", 3000)));

        Assert.True(report.IssueUrl.Length <= CrashReportBuilder.MaxUrlLength);
        Assert.DoesNotContain('\uFFFD', report.Body);
        for (var i = 0; i < report.Body.Length; i++)
        {
            if (char.IsHighSurrogate(report.Body[i]))
            {
                Assert.True(i + 1 < report.Body.Length && char.IsLowSurrogate(report.Body[i + 1]));
                i++;
            }
            else
                Assert.False(char.IsLowSurrogate(report.Body[i]));
        }
    }

    [Fact]
    public void BackticksInTheExceptionCannotCloseTheCodeFence()
    {
        var report = Build("System.Exception: ``` inside ````");

        var fenceLines = report.Body.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length >= 3 && l.All(c => c == '`')).ToList();
        Assert.Equal(2, fenceLines.Count);
        Assert.All(fenceLines, f => Assert.True(f.Length >= 5));
    }

    [Fact]
    public void AnEmptyExceptionStillGivesAUsableReport()
    {
        var report = Build("");

        Assert.Equal("Crash: Unhandled exception", report.Title);
        Assert.True(UpdateUrlGuard.IsSafe(report.IssueUrl));
    }

    [Fact]
    public void RedactionTwiceChangesNothing()
    {
        var once = Build(Stack).Body;
        var twice = Build(Stack).Body;

        Assert.Equal(once, twice);
    }
}
