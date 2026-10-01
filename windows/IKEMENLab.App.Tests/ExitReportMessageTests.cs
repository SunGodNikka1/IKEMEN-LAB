using IKEMENLab.App.Services;
using Xunit;

namespace IKEMENLab.App.Tests;

/// <summary>
/// App.OnExit shows only ExitReport.Message, so anything this drops is a warning the user never sees. Unfinished
/// shutdowns and cleanup problems are independent: cleanup can finish and still fail, and that failure used to
/// produce a null message and a silent shutdown.
/// </summary>
public class ExitReportMessageTests
{
    [Fact]
    public void CompletedShutdownWithAProblemStillReportsIt()
    {
        var report = new ExitReport(0, new[] { "sandbox could not be deleted" });

        Assert.NotNull(report.Message);
        Assert.Contains("sandbox could not be deleted", report.Message);
        Assert.True(report.Complete);
    }

    [Fact]
    public void NothingToReportMeansNoMessage()
    {
        Assert.Null(new ExitReport(0, Array.Empty<string>()).Message);
    }

    [Fact]
    public void UnfinishedAndProblemsAreBothSummarised()
    {
        var report = new ExitReport(2, new[] { "engine process still running", "sandbox could not be deleted" });
        var message = report.Message;

        Assert.NotNull(message);
        Assert.Contains("2 combo playback cleanup(s)", message);
        Assert.Contains("2 cleanup problems", message);
        Assert.Contains("engine process still running", message);
        Assert.Contains("sandbox could not be deleted", message);
    }

    [Fact]
    public void UnfinishedAloneIsStillReported()
    {
        var message = new ExitReport(1, Array.Empty<string>()).Message;

        Assert.NotNull(message);
        Assert.Contains("1 combo playback cleanup(s)", message);
    }
}
