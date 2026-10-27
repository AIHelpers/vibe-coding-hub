using System;
using System.Globalization;

namespace AiCodeAgent.App.Services;

/// <summary>
/// Converts stored UTC timestamps to the user's local clock for display.
/// Data stays in UTC on disk and in models; only the display step converts, so a checkpoint and a
/// chat message created at the same moment show the same clock time.
/// </summary>
public static class LocalTime
{
    /// <summary>
    /// Converts <paramref name="value"/> to local time in <paramref name="zone"/> (default: this machine's zone).
    /// UTC and unspecified-kind values are read as UTC, because the app writes UTC; a value after a JSON round-trip
    /// has no kind, so this is the case that used to show raw UTC. Local-kind values are converted through UTC.
    /// </summary>
    public static DateTime ToLocal(DateTime value, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
    }

    /// <summary>Short time of day in the culture's format (e.g. "15:30" or "3:30 PM"), local to <paramref name="zone"/>.</summary>
    public static string FormatShortTime(DateTime value, TimeZoneInfo? zone = null, IFormatProvider? culture = null)
        => ToLocal(value, zone).ToString("t", culture ?? CultureInfo.CurrentCulture);
}
