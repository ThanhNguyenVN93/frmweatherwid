using frmweatherwid;
using Xunit;

namespace WeatherWidget.Tests;

/// <summary>Provider parsing and the fallback chain, on real responses captured in Fixtures/ (no network).</summary>
public class WeatherServiceTests
{
    private const string OpenMeteo = "api.open-meteo.com", Met = "api.met.no", Wttr = "wttr.in", Aqi = "air-quality-api.open-meteo.com";

    private static (WeatherService Service, FakeHttp Http) Create(Dictionary<string, Func<HttpResponseMessage>> hosts)
    {
        var fake = new FakeHttp(hosts);
        return (new WeatherService(new HttpClient(fake)), fake);
    }

    [Fact]
    public async Task Open_Meteo_is_used_first_and_fully_parsed()
    {
        var (svc, http) = Create(new() { [OpenMeteo] = () => FakeHttp.Json(TestSetup.Fixture("openmeteo.json")) });
        var w = await svc.GetCurrentAsync(21.0245, 105.8412);

        Assert.NotNull(w);
        Assert.Equal("Open-Meteo", w.Source);
        Assert.InRange(w.TempC, -60, 60);
        Assert.InRange(w.Humidity, 0, 100);
        Assert.Equal(WeatherService.ForecastDays, w.Daily.Count);
        Assert.All(w.Daily, d => Assert.True(d.MinC <= d.MaxC));
        Assert.All(w.Daily, d => Assert.NotNull(d.Sunrise));
        Assert.Equal(TimeSpan.FromHours(7), w.UtcOffset);
        Assert.Equal([OpenMeteo], http.Requested);
    }

    [Fact]
    public async Task Falls_back_to_MET_Norway_and_computes_sunrise_itself()
    {
        var (svc, http) = Create(new()
        {
            [OpenMeteo] = FakeHttp.Fail,
            [Met]       = () => FakeHttp.Json(TestSetup.Fixture("met.json")),
        });
        var w = await svc.GetCurrentAsync(21.0245, 105.8412);

        Assert.NotNull(w);
        Assert.Equal("MET Norway", w.Source);
        Assert.NotEmpty(w.Daily);
        Assert.All(w.Daily, d => Assert.NotNull(d.Sunrise)); // MET has none → SunCalc filled them in
        Assert.Equal([OpenMeteo, Met], http.Requested);
    }

    [Fact]
    public async Task Falls_back_to_wttr_when_both_others_fail()
    {
        var (svc, _) = Create(new()
        {
            [OpenMeteo] = FakeHttp.Fail,
            [Met]       = FakeHttp.Fail,
            [Wttr]      = () => FakeHttp.Json(TestSetup.Fixture("wttr.json")),
        });
        var w = await svc.GetCurrentAsync(21.0245, 105.8412);

        Assert.NotNull(w);
        Assert.Equal("wttr.in", w.Source);
        Assert.Equal(3, w.Daily.Count);
        Assert.All(w.Daily, d => Assert.NotNull(d.Sunrise));
    }

    [Fact]
    public async Task Returns_null_when_every_provider_fails()
    {
        var (svc, _) = Create(new() { [OpenMeteo] = FakeHttp.Fail, [Met] = FakeHttp.Fail, [Wttr] = FakeHttp.Fail });
        Assert.Null(await svc.GetCurrentAsync(21.0245, 105.8412));
    }

    [Fact]
    public async Task A_failed_provider_is_skipped_on_the_next_refresh()
    {
        var (svc, http) = Create(new()
        {
            [OpenMeteo] = FakeHttp.Fail,
            [Met]       = () => FakeHttp.Json(TestSetup.Fixture("met.json")),
        });
        await svc.GetCurrentAsync(21.0245, 105.8412);
        http.Requested.Clear();
        await svc.GetCurrentAsync(21.0245, 105.8412);
        Assert.Equal([Met], http.Requested); // Open-Meteo is cooling down — no 8 s wait again
    }

    [Fact]
    public async Task Fallback_sources_use_the_city_time_zone()
    {
        var (svc, _) = Create(new() { [OpenMeteo] = FakeHttp.Fail, [Met] = () => FakeHttp.Json(TestSetup.Fixture("met.json")) });
        var w = await svc.GetCurrentAsync(35.68, 139.69, "Asia/Tokyo");
        Assert.Equal(TimeSpan.FromHours(9), w!.UtcOffset);
    }

    [Fact]
    public async Task Aqi_is_read_and_failures_are_null()
    {
        var (ok, _) = Create(new() { [Aqi] = () => FakeHttp.Json("""{"current":{"us_aqi":93.4}}""") });
        Assert.Equal(93, await ok.GetUsAqiAsync(21, 105));

        var (bad, _) = Create(new() { [Aqi] = FakeHttp.Fail });
        Assert.Null(await bad.GetUsAqiAsync(21, 105));
    }

    [Fact]
    public void Unknown_time_zone_falls_back_to_this_PC() =>
        Assert.Equal(TimeZoneInfo.Local, WeatherService.ResolveZone("Not/AZone"));
}
