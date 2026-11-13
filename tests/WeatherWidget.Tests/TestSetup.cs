using System.Net;
using System.Runtime.CompilerServices;

namespace WeatherWidget.Tests;

internal static class TestSetup
{
    /// <summary>Runs before any test: keep the app's logger out of the real %LOCALAPPDATA% log.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("WEATHERWIDGET_LOG_DIR", Path.Combine(Path.GetTempPath(), "WeatherWidget.Tests", "logs"));

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>Answers HTTP calls by host, so provider parsing/fallback is tested without the network.</summary>
internal sealed class FakeHttp(Dictionary<string, Func<HttpResponseMessage>> byHost) : HttpMessageHandler
{
    public List<string> Requested { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri!.Host;
        Requested.Add(host);
        return Task.FromResult(byHost.TryGetValue(host, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Fail() => new(HttpStatusCode.InternalServerError);
}
