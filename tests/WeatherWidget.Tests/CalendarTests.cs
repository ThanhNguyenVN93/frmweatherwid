using frmweatherwid;
using Xunit;

namespace WeatherWidget.Tests;

public class LunarCalendarTests
{
    [Theory]
    [InlineData(2024, 2, 10, 1, 1, false, "Giáp Thìn")]   // Tết 2024
    [InlineData(2025, 1, 29, 1, 1, false, "Ất Tỵ")]       // Tết 2025
    [InlineData(2026, 2, 17, 1, 1, false, "Bính Ngọ")]    // Tết 2026
    [InlineData(2027, 2, 6, 1, 1, false, "Đinh Mùi")]     // Tết 2027
    [InlineData(2007, 2, 17, 1, 1, false, "Đinh Hợi")]    // Vietnam's Tết — China's is 18/2 (UTC+7 vs UTC+8)
    [InlineData(1985, 1, 21, 1, 1, false, "Ất Sửu")]      // Vietnam's Tết — China's is 20/2
    [InlineData(2023, 3, 22, 1, 2, true, "Quý Mão")]      // first day of leap month 2
    [InlineData(2025, 7, 25, 1, 6, true, "Ất Tỵ")]        // first day of leap month 6
    [InlineData(2026, 9, 25, 15, 8, false, "Bính Ngọ")]   // Mid-Autumn 2026
    [InlineData(2026, 11, 13, 5, 10, false, "Bính Ngọ")]
    public void Converts_known_dates(int y, int m, int d, int lunarDay, int lunarMonth, bool leap, string yearName)
    {
        var l = LunarCalendar.FromSolar(new DateTime(y, m, d));
        Assert.Equal((lunarDay, lunarMonth, leap, yearName), (l.Day, l.Month, l.Leap, l.YearName));
    }
}

public class SunCalcTests
{
    [Fact]
    public void Hanoi_matches_published_times()
    {
        var sun = SunCalc.For(new DateOnly(2026, 11, 13), 21.0245, 105.8412, TimeSpan.FromHours(7));
        Assert.NotNull(sun);
        Assert.InRange(sun.Value.Rise, new DateTime(2026, 11, 13, 6, 3, 0), new DateTime(2026, 11, 13, 6, 7, 0));   // 06:05
        Assert.InRange(sun.Value.Set,  new DateTime(2026, 11, 13, 17, 14, 0), new DateTime(2026, 11, 13, 17, 18, 0)); // 17:16
    }

    [Theory]
    [InlineData(6, 21)]   // midnight sun
    [InlineData(12, 21)]  // polar night
    public void Polar_day_and_night_have_no_sunrise(int month, int day) =>
        Assert.Null(SunCalc.For(new DateOnly(2026, month, day), 69.65, 18.96, TimeSpan.FromHours(1)));
}
