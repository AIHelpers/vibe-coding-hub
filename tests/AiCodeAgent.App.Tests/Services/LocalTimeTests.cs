using System;
using System.Globalization;
using AiCodeAgent.App.Services;
using Xunit;

namespace AiCodeAgent.App.Tests.Services;

public class LocalTimeTests
{
    // Fixed offsets instead of IANA ids, so the tests behave the same on Windows and Linux.
    private static readonly TimeZoneInfo Plus9 = TimeZoneInfo.CreateCustomTimeZone("Test+09", TimeSpan.FromHours(9), "Test+09", "Test+09");
    private static readonly TimeZoneInfo Minus5 = TimeZoneInfo.CreateCustomTimeZone("Test-05", TimeSpan.FromHours(-5), "Test-05", "Test-05");

    private static readonly DateTime Utc = new(2026, 10, 27, 12, 30, 33, DateTimeKind.Utc);

    [Fact]
    public void ToLocal_ConvertsUtcToTheZone()
    {
        var local = LocalTime.ToLocal(Utc, Plus9);

        Assert.Equal(new DateTime(2026, 10, 27, 21, 30, 33), local);
    }

    [Fact]
    public void ToLocal_ReadsUnspecifiedKindAsUtc_LikeAValueReloadedFromJson()
    {
        var reloaded = DateTime.SpecifyKind(Utc, DateTimeKind.Unspecified);

        Assert.Equal(LocalTime.ToLocal(Utc, Minus5), LocalTime.ToLocal(reloaded, Minus5));
        Assert.Equal(new DateTime(2026, 10, 27, 7, 30, 33), LocalTime.ToLocal(reloaded, Minus5));
    }

    [Fact]
    public void ToLocal_LocalKindIsConvertedThroughUtc()
    {
        var localKind = Utc.ToLocalTime(); // what DateTime.Now-based values carry

        Assert.Equal(LocalTime.ToLocal(Utc), LocalTime.ToLocal(localKind));
    }

    [Fact]
    public void ToLocal_DefaultsToThisMachinesZone()
    {
        Assert.Equal(TimeZoneInfo.ConvertTimeFromUtc(Utc, TimeZoneInfo.Local), LocalTime.ToLocal(Utc));
    }

    [Fact]
    public void FormatShortTime_UsesTheShortTimeOfDayInTheZone()
    {
        Assert.Equal("21:30", LocalTime.FormatShortTime(Utc, Plus9, CultureInfo.InvariantCulture));
        Assert.Equal("07:30", LocalTime.FormatShortTime(Utc, Minus5, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CheckpointAndChatTimestamps_OfTheSameMoment_ShowTheSameClockTime()
    {
        // A chat message keeps its UTC value in the model; a checkpoint is read back from disk without a kind.
        var chatMessage = Utc;
        var checkpoint = DateTime.SpecifyKind(Utc, DateTimeKind.Unspecified);

        var chatText = LocalTime.FormatShortTime(chatMessage, Plus9, CultureInfo.InvariantCulture);
        var checkpointText = LocalTime.FormatShortTime(checkpoint, Plus9, CultureInfo.InvariantCulture);

        Assert.Equal(chatText, checkpointText);
    }
}
