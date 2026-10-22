using System;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using Xunit;

namespace AiCodeAgent.App.Tests.Services;

public class CrashReporterTests
{
    [Fact]
    public void HandleUiException_MarksOrdinaryExceptionAsHandled()
    {
        Assert.True(CrashReporter.HandleUiException(new InvalidOperationException("binding failed"), "test"));
    }

    [Fact]
    public void HandleUiException_DoesNotContinueAfterOutOfMemory()
    {
        Assert.False(CrashReporter.HandleUiException(new OutOfMemoryException(), "test"));
    }

    [Fact]
    public void OnUnobservedTask_ObservesTheException()
    {
        var args = new UnobservedTaskExceptionEventArgs(
            new AggregateException(new InvalidOperationException("background work failed")));

        CrashReporter.OnUnobservedTask(args);

        Assert.True(args.Observed);
    }
}
