using System.Globalization;
using System.Text.Json;

namespace frmweatherwid
{
    internal enum WeatherKind { Unknown, Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Thunder }

    internal sealed record DailyForecast(DateOnly Date, double MinC, double MaxC, WeatherKind Kind, int? RainChance);

    internal sealed record WeatherData(
        double TempC, double Humidity, double WindKph, double FeelsLikeC, WeatherKind Kind, string Source,
        IReadOnlyList<DailyForecast> Daily);

    /// <summary>
    /// Current weather with automatic fallback (all free, no API key):
    ///   1. Open-Meteo   – most complete (has apparent temperature)
    ///   2. MET Norway   – very reliable, global; feels-like computed locally
    ///   3. wttr.in      – last resort
    /// Each provider gets a short timeout so a dead endpoint doesn't block the UI for long.
    /// </summary>
    internal sealed class WeatherService
    {
        private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(8);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const int ForecastDays = 6; // today + 5
        private static readonly string[] MetSummaryBlocks = ["next_1_hours", "next_6_hours", "next_12_hours"];

        private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(15);

        private readonly HttpClient http;
        private readonly (string Name, Func<string, string, CancellationToken, Task<WeatherData>> Fetch)[] providers;
        private readonly DateTime[] cooldownUntil;

        public WeatherService(HttpClient http)
        {
            this.http = http;
            providers =
            [
                ("Open-Meteo", FetchOpenMeteoAsync),
                ("MET Norway", FetchMetNorwayAsync),
                ("wttr.in",    FetchWttrAsync),
            ];
            cooldownUntil = new DateTime[providers.Length];
        }

        /// <summary>Returns data from the first provider that answers, or null if all fail.</summary>
        public async Task<WeatherData?> GetCurrentAsync(double latitude, double longitude, CancellationToken ct = default)
        {
            var (lat, lon) = (Coord(latitude), Coord(longitude));

            // Providers that failed recently are skipped (unless every one is cooling down).
            var now   = DateTime.UtcNow;
            var order = Enumerable.Range(0, providers.Length).Where(i => cooldownUntil[i] <= now).ToList();
            if (order.Count == 0) order = [.. Enumerable.Range(0, providers.Length)];

            foreach (var i in order)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(ProviderTimeout);
                    var data = await providers[i].Fetch(lat, lon, cts.Token);
                    cooldownUntil[i] = DateTime.MinValue;
                    return data;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    cooldownUntil[i] = DateTime.UtcNow + FailureCooldown; // try next provider
                }
            }
            return null;
        }

        /// <summary>US AQI (0-500) from Open-Meteo air-quality, or null. Never throws.</summary>
        public async Task<int?> GetUsAqiAsync(double latitude, double longitude, CancellationToken ct = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ProviderTimeout);
                var url = $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={Coord(latitude)}&longitude={Coord(longitude)}&current=us_aqi";
                using var doc = await GetJsonAsync(url, cts.Token);
                var v = doc.RootElement.GetProperty("current").GetProperty("us_aqi");
                return v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Coord(double v) => v.ToString("0.####", Inv);

        // ---------------------------------------------------------------- providers

        private async Task<WeatherData> FetchOpenMeteoAsync(string lat, string lon, CancellationToken ct)
        {
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                      "&current=temperature_2m,relative_humidity_2m,wind_speed_10m,apparent_temperature,weather_code" +
                      "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
                      $"&timezone=auto&forecast_days={ForecastDays}&wind_speed_unit=kmh";
            using var doc = await GetJsonAsync(url, ct);
            var cur = doc.RootElement.GetProperty("current");
            return new WeatherData(
                cur.GetProperty("temperature_2m").GetDouble(),
                cur.GetProperty("relative_humidity_2m").GetDouble(),
                cur.GetProperty("wind_speed_10m").GetDouble(),
                cur.GetProperty("apparent_temperature").GetDouble(),
                FromWmo(cur.GetProperty("weather_code").GetInt32()),
                "Open-Meteo",
                SafeDaily(() => ParseOpenMeteoDaily(doc.RootElement.GetProperty("daily"))));
        }

        private static List<DailyForecast> ParseOpenMeteoDaily(JsonElement daily)
        {
            var dates = daily.GetProperty("time");
            var codes = daily.GetProperty("weather_code");
            var max   = daily.GetProperty("temperature_2m_max");
            var min   = daily.GetProperty("temperature_2m_min");
            daily.TryGetProperty("precipitation_probability_max", out var rain);

            var list = new List<DailyForecast>();
            for (int i = 0; i < dates.GetArrayLength(); i++)
            {
                if (max[i].ValueKind != JsonValueKind.Number || min[i].ValueKind != JsonValueKind.Number) continue;
                int? chance = rain.ValueKind == JsonValueKind.Array && rain[i].ValueKind == JsonValueKind.Number
                    ? (int)Math.Round(rain[i].GetDouble()) : null;
                list.Add(new DailyForecast(DateOnly.Parse(dates[i].GetString()!, Inv),
                    min[i].GetDouble(), max[i].GetDouble(),
                    codes[i].ValueKind == JsonValueKind.Number ? FromWmo(codes[i].GetInt32()) : WeatherKind.Unknown,
                    chance));
            }
            return list;
        }

        private async Task<WeatherData> FetchMetNorwayAsync(string lat, string lon, CancellationToken ct)
        {
            // Docs: https://api.met.no/weatherapi/locationforecast/2.0/documentation (requires a User-Agent)
            var url = $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={lat}&lon={lon}";
            using var doc = await GetJsonAsync(url, ct);
            var series  = doc.RootElement.GetProperty("properties").GetProperty("timeseries");
            var now     = series[0].GetProperty("data");
            var details = now.GetProperty("instant").GetProperty("details");

            double temp  = details.GetProperty("air_temperature").GetDouble();
            double rh    = details.GetProperty("relative_humidity").GetDouble();
            double windM = details.GetProperty("wind_speed").GetDouble(); // m/s

            return new WeatherData(temp, rh, windM * 3.6, ApparentTemperature(temp, rh, windM),
                                   FromMetSymbol(MetSymbol(now)), "MET Norway",
                                   SafeDaily(() => ParseMetDaily(series)));
        }

        private static string MetSymbol(JsonElement data)
        {
            foreach (var key in MetSummaryBlocks)
            {
                if (data.TryGetProperty(key, out var block) &&
                    block.TryGetProperty("summary", out var s) &&
                    s.TryGetProperty("symbol_code", out var code))
                    return code.GetString() ?? "";
            }
            return "";
        }

        /// <summary>MET gives a UTC time series — group it into local days: min/max temp, symbol nearest noon.</summary>
        private static List<DailyForecast> ParseMetDaily(JsonElement series)
        {
            var days = new SortedDictionary<DateOnly, (double Min, double Max, string Symbol, double NoonDist)>();
            foreach (var entry in series.EnumerateArray())
            {
                var local = DateTime.Parse(entry.GetProperty("time").GetString()!, Inv,
                                           DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ToLocalTime();
                var data  = entry.GetProperty("data");
                double t  = data.GetProperty("instant").GetProperty("details").GetProperty("air_temperature").GetDouble();
                var day   = DateOnly.FromDateTime(local);
                double noonDist = Math.Abs(local.Hour + local.Minute / 60.0 - 12);
                string sym = MetSymbol(data);

                if (!days.TryGetValue(day, out var d)) d = (t, t, "", double.MaxValue);
                d.Min = Math.Min(d.Min, t);
                d.Max = Math.Max(d.Max, t);
                if (sym.Length > 0 && noonDist < d.NoonDist) (d.Symbol, d.NoonDist) = (sym, noonDist);
                days[day] = d;
            }
            return days.Take(ForecastDays)
                       .Select(kv => new DailyForecast(kv.Key, kv.Value.Min, kv.Value.Max, FromMetSymbol(kv.Value.Symbol), null))
                       .ToList();
        }

        private async Task<WeatherData> FetchWttrAsync(string lat, string lon, CancellationToken ct)
        {
            var url = $"https://wttr.in/{lat},{lon}?format=j1";
            using var doc = await GetJsonAsync(url, ct);
            var cur = doc.RootElement.GetProperty("current_condition")[0];

            return new WeatherData(Num(cur, "temp_C"), Num(cur, "humidity"), Num(cur, "windspeedKmph"), Num(cur, "FeelsLikeC"),
                                   FromWwo((int)Num(cur, "weatherCode")), "wttr.in",
                                   SafeDaily(() => ParseWttrDaily(doc.RootElement.GetProperty("weather"))));
        }

        /// <summary>wttr.in: 3 days; condition taken from the noon slot (hourly[4] = 12:00).</summary>
        private static List<DailyForecast> ParseWttrDaily(JsonElement weather) =>
            weather.EnumerateArray().Select(day =>
            {
                var hourly = day.GetProperty("hourly");
                var noon   = hourly[Math.Min(4, hourly.GetArrayLength() - 1)];
                int rain   = hourly.EnumerateArray().Max(h => (int)Num(h, "chanceofrain"));
                return new DailyForecast(DateOnly.Parse(day.GetProperty("date").GetString()!, Inv),
                    Num(day, "mintempC"), Num(day, "maxtempC"), FromWwo((int)Num(noon, "weatherCode")), rain);
            }).ToList();

        private static double Num(JsonElement el, string name) => double.Parse(el.GetProperty(name).GetString()!, Inv);

        /// <summary>Forecast is a bonus — a parsing problem must never cost us the current weather.</summary>
        private static IReadOnlyList<DailyForecast> SafeDaily(Func<List<DailyForecast>> parse)
        {
            try { return parse(); }
            catch { return []; }
        }

        private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Australian BoM apparent temperature (same formula family Open-Meteo uses).</summary>
        private static double ApparentTemperature(double tempC, double rh, double windMs)
        {
            double e = rh / 100.0 * 6.105 * Math.Exp(17.27 * tempC / (237.7 + tempC)); // vapour pressure hPa
            return tempC + 0.33 * e - 0.70 * windMs - 4.00;
        }

        private static WeatherKind FromWmo(int code) => code switch
        {
            0                    => WeatherKind.Clear,
            1 or 2               => WeatherKind.PartlyCloudy,
            3                    => WeatherKind.Cloudy,
            45 or 48             => WeatherKind.Fog,
            >= 51 and <= 57      => WeatherKind.Drizzle,
            >= 61 and <= 67      => WeatherKind.Rain,
            >= 71 and <= 77      => WeatherKind.Snow,
            >= 80 and <= 82      => WeatherKind.Rain,
            85 or 86             => WeatherKind.Snow,
            >= 95                => WeatherKind.Thunder,
            _                    => WeatherKind.Unknown,
        };

        private static WeatherKind FromMetSymbol(string s)
        {
            if (s.Contains("thunder"))                         return WeatherKind.Thunder;
            if (s.Contains("snow") || s.Contains("sleet"))     return WeatherKind.Snow;
            if (s.StartsWith("lightrain"))                     return WeatherKind.Drizzle;
            if (s.Contains("rain"))                            return WeatherKind.Rain;
            if (s.StartsWith("fog"))                           return WeatherKind.Fog;
            if (s.StartsWith("clearsky"))                      return WeatherKind.Clear;
            if (s.StartsWith("fair") || s.StartsWith("partlycloudy")) return WeatherKind.PartlyCloudy;
            if (s.StartsWith("cloudy"))                        return WeatherKind.Cloudy;
            return WeatherKind.Unknown;
        }

        /// <summary>WorldWeatherOnline codes used by wttr.in.</summary>
        private static WeatherKind FromWwo(int code) => code switch
        {
            113                                      => WeatherKind.Clear,
            116                                      => WeatherKind.PartlyCloudy,
            119 or 122                               => WeatherKind.Cloudy,
            143 or 248 or 260                        => WeatherKind.Fog,
            176 or 263 or 266 or 281 or 284 or 293 or 353 => WeatherKind.Drizzle,
            296 or 299 or 302 or 305 or 308 or 356 or 359 => WeatherKind.Rain,
            200 or 386 or 389 or 392 or 395          => WeatherKind.Thunder,
            179 or 182 or 185 or 227 or 230 or (>= 311 and <= 350) or (>= 362 and <= 377) => WeatherKind.Snow,
            _                                        => WeatherKind.Unknown,
        };
    }
}
