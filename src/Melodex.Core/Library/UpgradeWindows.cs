using Melodex.Client;

namespace Melodex.Core.Library;

/// <summary>
/// When scheduled upgrades may be asked for: from <see cref="UpgradeSettings.From"/> to <see cref="UpgradeSettings.Until"/>
/// on each of <see cref="UpgradeSettings.Days"/>, in the settings' time zone. A window that ends earlier in the day than
/// it starts runs past midnight, and belongs to the day it starts on; one that ends when it starts lasts a day.
/// </summary>
public static class UpgradeWindows
{
    public static TimeZoneInfo Zone(string? id)
    {
        try
        {
            return id is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static bool Known(string? id)
    {
        try
        {
            return id is not null && TimeZoneInfo.FindSystemTimeZoneById(id) is not null;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>The window open at a moment, in UTC, or null when none is.</summary>
    public static (DateTime Start, DateTime End)? Current(UpgradeSettings settings, DateTime utcNow)
    {
        var zone = Zone(settings.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone));
        // Today's, or yesterday's if it runs past midnight.
        foreach (var day in new[] { today, today.AddDays(-1) })
            if (On(settings, day, zone) is { } window && window.Start <= utcNow && utcNow < window.End)
                return window;
        return null;
    }

    /// <summary>When the next window starts, after a moment; null when there are no days.</summary>
    public static DateTime? Next(UpgradeSettings settings, DateTime utcNow)
    {
        var zone = Zone(settings.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone));
        for (var i = 0; i <= 8; i++)
            if (On(settings, today.AddDays(i), zone) is { } window && window.Start > utcNow)
                return window.Start;
        return null;
    }

    private static (DateTime Start, DateTime End)? On(UpgradeSettings settings, DateOnly day, TimeZoneInfo zone)
    {
        if (!settings.Days.Contains(day.DayOfWeek))
            return null;
        var start = Utc(day.ToDateTime(settings.From), zone);
        var endDay = settings.Until > settings.From ? day : day.AddDays(1);
        return (start, Utc(endDay.ToDateTime(settings.Until), zone));
    }

    // A time the clocks skip, going forward, is taken as just after the jump.
    private static DateTime Utc(DateTime local, TimeZoneInfo zone)
    {
        for (var i = 0; zone.IsInvalidTime(local) && i < 4; i++)
            local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
    }
}
