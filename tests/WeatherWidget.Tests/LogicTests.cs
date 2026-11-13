using System.Text.Json;
using System.Xml.Linq;
using frmweatherwid;
using Xunit;

namespace WeatherWidget.Tests;

public class AlertRulesTests
{
    private static WeatherData Data(int? rainInMinutes, bool rainingNow = false)
    {
        var w = new WeatherData(25, 80, 10, 27, WeatherKind.Cloudy, "test", []) { RainingNow = rainingNow };
        return w with { RainStartsAt = rainInMinutes is { } m ? w.LocalNow.AddMinutes(m) : null };
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(0, 0)]
    [InlineData(60, 60)]
    public void Warns_when_rain_starts_within_the_hour(int minutes, int expected) =>
        Assert.Equal(expected, AlertRules.RainAlert(Data(minutes), null));

    [Fact] public void No_warning_beyond_an_hour() => Assert.Null(AlertRules.RainAlert(Data(90), null));
    [Fact] public void No_warning_without_rain() => Assert.Null(AlertRules.RainAlert(Data(null), null));
    [Fact] public void No_warning_while_already_raining() => Assert.Null(AlertRules.RainAlert(Data(10, rainingNow: true), null));

    [Fact]
    public void Same_rain_event_is_warned_only_once()
    {
        var data = Data(30);
        Assert.Null(AlertRules.RainAlert(data, data.RainStartsAt!.Value.AddMinutes(-15)));  // already warned about this shower
        Assert.Equal(30, AlertRules.RainAlert(data, data.RainStartsAt!.Value.AddHours(-3))); // a new one later in the day
    }

    [Fact]
    public void Aqi_warns_once_then_rearms_after_air_clears()
    {
        bool alerted = false, alert;
        (alert, alerted) = AlertRules.AqiAlert(120, alerted); Assert.False(alert);
        (alert, alerted) = AlertRules.AqiAlert(160, alerted); Assert.True(alert);
        (alert, alerted) = AlertRules.AqiAlert(180, alerted); Assert.False(alert); // still bad — don't nag
        (alert, alerted) = AlertRules.AqiAlert(130, alerted); Assert.False(alert); // between thresholds — still armed off
        (alert, alerted) = AlertRules.AqiAlert(90, alerted);  Assert.False(alert); // clean air re-arms
        (alert, alerted) = AlertRules.AqiAlert(155, alerted); Assert.True(alert);
    }
}

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V2.0", "2.0.0")]
    [InlineData("v1.2.3-beta", "1.2.3")]
    [InlineData("v1.2.3+abc", "1.2.3")]
    public void Parses_release_tags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("latest")]
    public void Rejects_non_versions(string? tag) => Assert.Null(UpdateService.ParseVersion(tag));
}

public class UninstallerTests
{
    [Fact]
    public void Command_cleans_everything_and_works_without_the_exe()
    {
        var cmd = Uninstaller.Command(@"C:\Apps\WeatherWidget.exe");
        Assert.StartsWith("cmd.exe /d /c ", cmd);
        Assert.Contains("taskkill /f /im WeatherWidget.exe", cmd);
        Assert.Contains(@"reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v FrmWeatherWidget /f", cmd);
        Assert.Contains(@"reg delete ""HKCU\Software\FrmWeatherWidget"" /f", cmd);
        Assert.Contains(@"%LOCALAPPDATA%\WeatherWidget", cmd);
        Assert.Contains(@"del /f /q ""C:\Apps\WeatherWidget.exe""", cmd);
        Assert.EndsWith(@"reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WeatherWidget"" /f >nul 2>&1", cmd);
        Assert.DoesNotContain(" && ", cmd); // every step runs even if an earlier one fails
    }

    [Fact]
    public void Dev_builds_never_delete_their_exe() => Assert.DoesNotContain("del /f", Uninstaller.Command(null));
}

public class ChangelogTests
{
    /// <summary>Guards releases: the version in the csproj must have its "What's new" entry (vi + en).</summary>
    [Fact]
    public void Current_project_version_has_a_bilingual_entry()
    {
        var csproj = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "frmweatherwid.csproj");
        var version = Version.Parse(XDocument.Load(csproj).Descendants("Version").First().Value);
        var entry = Changelog.Entries.FirstOrDefault(e => e.Version == version);
        Assert.NotNull(entry);
        Assert.NotEmpty(entry.Vi);
        Assert.Equal(entry.Vi.Length, entry.En.Length);
    }

    [Fact]
    public void Entries_are_newest_first() =>
        Assert.Equal(Changelog.Entries.Select(e => e.Version).OrderByDescending(v => v), Changelog.Entries.Select(e => e.Version));
}

public class OpenMeteoRainTests
{
    [Fact]
    public void Finds_the_first_wet_quarter_hour()
    {
        using var doc = JsonDocument.Parse("""
            { "time": ["2026-11-13T10:00","2026-11-13T10:15","2026-11-13T10:30","2026-11-13T10:45"],
              "precipitation": [0.0, 0.0, 0.4, 1.2] }
            """);
        var start = WeatherService.ParseOpenMeteoRainStart(doc.RootElement, new DateTime(2026, 11, 13, 10, 5, 0));
        Assert.Equal(new DateTime(2026, 11, 13, 10, 30, 0), start);
    }

    [Fact]
    public void Dry_forecast_means_no_rain_start()
    {
        using var doc = JsonDocument.Parse("""{ "time": ["2026-11-13T10:00","2026-11-13T10:15"], "precipitation": [0.0, 0.05] }""");
        Assert.Null(WeatherService.ParseOpenMeteoRainStart(doc.RootElement, new DateTime(2026, 11, 13, 10, 0, 0)));
    }
}
