using SolPnl.Contracts.Models;

namespace SolPnl.Core.Site;

/// <summary>
/// Sunrise, sunset, solar noon and sun position from NOAA's "General Solar Position Calculations" (accurate to about a
/// minute for most latitudes), plus a clear-sky irradiance estimate (Haurwitz model). Entirely local: no network.
/// </summary>
public static class SunCalculator
{
    private const double SunriseZenith = 90.833; // includes atmospheric refraction and the sun's radius
    private const double CivilZenith = 96.0;

    public static SunTimes Calculate(DateOnly date, double latitude, double longitude, TimeZoneInfo zone)
    {
        var noon = SolarNoonUtc(date, longitude, zone);
        var rise = EventUtc(date, latitude, longitude, zone, SunriseZenith, rising: true);
        var set = EventUtc(date, latitude, longitude, zone, SunriseZenith, rising: false);
        var dawn = EventUtc(date, latitude, longitude, zone, CivilZenith, rising: true);
        var dusk = EventUtc(date, latitude, longitude, zone, CivilZenith, rising: false);

        TimeSpan dayLength;
        if (rise is { } r && set is { } s) dayLength = s - r;
        else dayLength = ElevationDegrees(noon, latitude, longitude) > 0 ? TimeSpan.FromHours(24) : TimeSpan.Zero; // polar day / night

        return new SunTimes(date, Local(rise, zone), Local(set, zone), TimeZoneInfo.ConvertTime(noon, zone), dayLength,
            Local(dawn, zone), Local(dusk, zone));
    }

    /// <summary>Sun elevation above the horizon in degrees (negative below) at an instant.</summary>
    public static double ElevationDegrees(DateTimeOffset time, double latitude, double longitude)
    {
        var utc = time.UtcDateTime;
        var (eqTime, decl) = Orbit(utc.DayOfYear, utc.TimeOfDay.TotalHours, DateTime.IsLeapYear(utc.Year));
        var trueSolarMinutes = utc.TimeOfDay.TotalMinutes + eqTime + 4 * longitude;
        var hourAngle = Rad(trueSolarMinutes / 4 - 180);
        var lat = Rad(latitude);
        var cosZenith = Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(hourAngle);
        return 90 - Deg(Math.Acos(Math.Clamp(cosZenith, -1, 1)));
    }

    /// <summary>Clear-sky global horizontal irradiance (W/m²) at an instant, from the Haurwitz model.</summary>
    public static double ClearSkyIrradiance(DateTimeOffset time, double latitude, double longitude)
    {
        var cosZ = Math.Sin(Rad(ElevationDegrees(time, latitude, longitude)));
        return cosZ <= 0 ? 0 : 1098 * cosZ * Math.Exp(-0.057 / cosZ);
    }

    /// <summary>Clear-sky sunlight on a horizontal surface over a local day, in kWh/m² (10-minute integration).</summary>
    public static double ClearSkyIrradiation(DateOnly date, double latitude, double longitude, TimeZoneInfo zone)
    {
        var start = LocalMidnightUtc(date, zone);
        var end = LocalMidnightUtc(date.AddDays(1), zone);
        var step = TimeSpan.FromMinutes(10);
        double wattHours = 0;
        for (var t = start; t < end; t += step)
            wattHours += ClearSkyIrradiance(t + step / 2, latitude, longitude) * step.TotalHours;
        return Math.Round(wattHours / 1000, 3);
    }

    public static DateTimeOffset LocalMidnightUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static DateTimeOffset SolarNoonUtc(DateOnly date, double longitude, TimeZoneInfo zone)
    {
        // Refine once: the equation of time depends (slightly) on the time of day itself.
        var minutes = 720 - 4 * longitude;
        for (var i = 0; i < 2; i++)
        {
            var (eqTime, _) = Orbit(DayOfYear(date), minutes / 60, DateTime.IsLeapYear(date.Year));
            minutes = 720 - 4 * longitude - eqTime;
        }
        return UtcAt(date, minutes);
    }

    private static DateTimeOffset? EventUtc(DateOnly date, double latitude, double longitude, TimeZoneInfo zone, double zenith, bool rising)
    {
        var minutes = 720 - 4 * longitude; // first guess: solar noon
        for (var i = 0; i < 3; i++)
        {
            var (eqTime, decl) = Orbit(DayOfYear(date), minutes / 60, DateTime.IsLeapYear(date.Year));
            var lat = Rad(latitude);
            var cosHa = Math.Cos(Rad(zenith)) / (Math.Cos(lat) * Math.Cos(decl)) - Math.Tan(lat) * Math.Tan(decl);
            if (cosHa is > 1 or < -1) return null; // sun never reaches this zenith on this day
            var ha = Deg(Math.Acos(cosHa));
            minutes = 720 - 4 * (longitude + (rising ? ha : -ha)) - eqTime;
        }

        // The event belongs to the UTC date computed from the local date; keep it on the requested local day.
        var result = UtcAt(date, minutes);
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(result, zone).DateTime);
        if (localDate != date) result = result.AddDays(date.DayNumber - localDate.DayNumber);
        return result;
    }

    /// <summary>Equation of time (minutes) and solar declination (radians) for a day of year and UTC hour.</summary>
    private static (double EqTime, double Declination) Orbit(int dayOfYear, double utcHour, bool leapYear)
    {
        var gamma = 2 * Math.PI / (leapYear ? 366 : 365) * (dayOfYear - 1 + (utcHour - 12) / 24);
        var eqTime = 229.18 * (0.000075 + 0.001868 * Math.Cos(gamma) - 0.032077 * Math.Sin(gamma)
                               - 0.014615 * Math.Cos(2 * gamma) - 0.040849 * Math.Sin(2 * gamma));
        var decl = 0.006918 - 0.399912 * Math.Cos(gamma) + 0.070257 * Math.Sin(gamma)
                   - 0.006758 * Math.Cos(2 * gamma) + 0.000907 * Math.Sin(2 * gamma)
                   - 0.002697 * Math.Cos(3 * gamma) + 0.00148 * Math.Sin(3 * gamma);
        return (eqTime, decl);
    }

    private static DateTimeOffset UtcAt(DateOnly date, double minutesUtc) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddMinutes(minutesUtc);

    private static DateTimeOffset? Local(DateTimeOffset? utc, TimeZoneInfo zone) =>
        utc is { } t ? TimeZoneInfo.ConvertTime(t, zone) : null;

    private static int DayOfYear(DateOnly date) => date.DayOfYear;
    private static double Rad(double degrees) => degrees * Math.PI / 180;
    private static double Deg(double radians) => radians * 180 / Math.PI;
}
