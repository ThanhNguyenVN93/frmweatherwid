using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace frmweatherwid
{
    internal enum WeatherKind { Unknown, Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Thunder }

    internal sealed record DailyForecast(DateOnly Date, double MinC, double MaxC, WeatherKind Kind, int? RainChance)
    {
        public DateTime? Sunrise { get; init; }   // location-local time
        public DateTime? Sunset  { get; init; }
        public double?   UvMax   { get; init; }
    }

    /// <summary>Sunrise/sunset from the NOAA approximation (±1–2 min, fine for a widget).</summary>
    internal static class SunCalc
    {
        private const double Zenith = 90.833; // official sunrise/sunset incl. refraction

        public static (DateTime Rise, DateTime Set)? For(DateOnly date, double lat, double lon, TimeSpan utcOffset)
        {
            var rise = Event(date, lat, lon, utcOffset, rising: true);
            var set  = Event(date, lat, lon, utcOffset, rising: false);
            return rise is { } r && set is { } s ? (r, s) : null; // null = polar day/night
        }

        private static DateTime? Event(DateOnly date, double lat, double lon, TimeSpan utcOffset, bool rising)
        {
            static double Rad(double d) => d * Math.PI / 180;
            static double Deg(double r) => r * 180 / Math.PI;
            static double Norm(double v, double max) => ((v % max) + max) % max;

            double lngHour = lon / 15;
            double t = date.DayOfYear + ((rising ? 6 : 18) - lngHour) / 24;
            double m = 0.9856 * t - 3.289;                                                     // mean anomaly
            double l = Norm(m + 1.916 * Math.Sin(Rad(m)) + 0.020 * Math.Sin(Rad(2 * m)) + 282.634, 360); // true longitude
            double ra = Norm(Deg(Math.Atan(0.91764 * Math.Tan(Rad(l)))), 360);                 // right ascension
            ra = (ra + (Math.Floor(l / 90) * 90 - Math.Floor(ra / 90) * 90)) / 15;
            double sinDec = 0.39782 * Math.Sin(Rad(l)), cosDec = Math.Cos(Math.Asin(sinDec));
            double cosH = (Math.Cos(Rad(Zenith)) - sinDec * Math.Sin(Rad(lat))) / (cosDec * Math.Cos(Rad(lat)));
            if (cosH is > 1 or < -1) return null;
            double h = (rising ? 360 - Deg(Math.Acos(cosH)) : Deg(Math.Acos(cosH))) / 15;
            double ut = Norm(h + ra - 0.06571 * t - 6.622 - lngHour, 24);
            double local = Norm(ut + utcOffset.TotalHours, 24);                                // stays on the same local date
            return date.ToDateTime(TimeOnly.MinValue).AddHours(local);
        }
    }

    internal sealed record HourlyForecast(DateTime Time, double TempC, WeatherKind Kind, bool IsDay, int? RainChance);

    internal sealed record WeatherData(
        double TempC, double Humidity, double WindKph, double FeelsLikeC, WeatherKind Kind, string Source,
        IReadOnlyList<DailyForecast> Daily)
    {
        public bool IsDay { get; init; } = true;
        public double? UvIndex { get; init; }
        public bool RainingNow { get; init; }
        /// <summary>First time (location-local) rain is expected within the next ~2 h, if any.</summary>
        public DateTime? RainStartsAt { get; init; }
        public IReadOnlyList<HourlyForecast> Hourly { get; init; } = [];
        /// <summary>UTC offset of the forecast location (Open-Meteo knows it; other sources assume this PC's zone).</summary>
        public TimeSpan UtcOffset { get; init; } = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);

        [JsonIgnore] public DateTime LocalNow => DateTime.UtcNow + UtcOffset;
        [JsonIgnore] public DailyForecast? Today => Daily.FirstOrDefault(d => d.Date == DateOnly.FromDateTime(LocalNow));
    }

    /// <summary>
    /// Current weather + forecast with automatic fallback (all free, no API key):
    ///   1. Open-Meteo   – most complete (feels-like, UV, sunrise/sunset, 15-minute rain)
    ///   2. MET Norway   – very reliable, global; feels-like computed locally
    ///   3. wttr.in      – last resort (3 days)
    /// Each provider gets a short timeout so a dead endpoint doesn't block the UI for long.
    /// Optional parts (forecast, hourly, sun…) are parsed defensively: a problem there never costs the current weather.
    /// </summary>
    internal sealed class WeatherService
    {
        private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(15);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const int ForecastDays = 6;  // today + 5
        public const int HourlyCount  = 24; // hours ahead
        private static readonly string[] MetSummaryBlocks = ["next_1_hours", "next_6_hours", "next_12_hours"];

        private readonly HttpClient http;
        private readonly (string Name, Func<string, string, TimeZoneInfo, CancellationToken, Task<WeatherData>> Fetch)[] providers;
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
        public async Task<WeatherData?> GetCurrentAsync(double latitude, double longitude, string? timeZone = null, CancellationToken ct = default)
        {
            var (lat, lon) = (Coord(latitude), Coord(longitude));
            var zone = ResolveZone(timeZone);

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
                    var data = await providers[i].Fetch(lat, lon, zone, cts.Token);
                    cooldownUntil[i] = DateTime.MinValue;
                    return FillSunTimes(data, latitude, longitude);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    AppLog.Warn($"Weather provider {providers[i].Name} failed", ex);
                    cooldownUntil[i] = DateTime.UtcNow + FailureCooldown; // try next provider
                }
            }
            AppLog.Warn("All weather providers failed");
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
            catch (Exception ex)
            {
                AppLog.Warn("AQI fetch failed", ex);
                return null;
            }
        }

        private static string Coord(double v) => v.ToString("0.####", Inv);

        /// <summary>IANA zone of the place (e.g. "Asia/Tokyo"); this PC's zone when unknown or not installed.</summary>
        internal static TimeZoneInfo ResolveZone(string? id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (Exception ex) { AppLog.Warn($"Unknown time zone {id}", ex); }
            }
            return TimeZoneInfo.Local;
        }

        private static DateTime LocalNow(TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);

        /// <summary>Providers without sunrise/sunset (MET Norway) get them computed locally — no network needed.</summary>
        private static WeatherData FillSunTimes(WeatherData data, double lat, double lon)
        {
            if (data.Daily.All(d => d.Sunrise is not null)) return data;
            var daily = data.Daily.Select(d => d.Sunrise is not null ? d
                : SunCalc.For(d.Date, lat, lon, data.UtcOffset) is { } sun ? d with { Sunrise = sun.Rise, Sunset = sun.Set } : d).ToList();
            return data with { Daily = daily };
        }

        // ================================================================ Open-Meteo

        private async Task<WeatherData> FetchOpenMeteoAsync(string lat, string lon, TimeZoneInfo zone, CancellationToken ct)
        {
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                      "&current=temperature_2m,relative_humidity_2m,wind_speed_10m,apparent_temperature,weather_code,is_day,uv_index,precipitation" +
                      "&hourly=temperature_2m,weather_code,precipitation_probability,is_day" +
                      "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,uv_index_max" +
                      "&minutely_15=precipitation&forecast_minutely_15=8" +
                      $"&timezone=auto&forecast_days={ForecastDays}&wind_speed_unit=kmh";
            using var doc = await GetJsonAsync(url, ct);
            var root = doc.RootElement;
            var cur  = root.GetProperty("current");
            var kind = FromWmo(cur.GetProperty("weather_code").GetInt32());
            var offset = TimeSpan.FromSeconds(root.TryGetProperty("utc_offset_seconds", out var o) ? o.GetInt32() : 0);
            var localNow = DateTime.UtcNow + offset;

            return new WeatherData(
                cur.GetProperty("temperature_2m").GetDouble(),
                cur.GetProperty("relative_humidity_2m").GetDouble(),
                cur.GetProperty("wind_speed_10m").GetDouble(),
                cur.GetProperty("apparent_temperature").GetDouble(),
                kind,
                "Open-Meteo",
                Safe(() => ParseOpenMeteoDaily(root.GetProperty("daily"))))
            {
                UtcOffset    = offset,
                IsDay        = NumOrNull(cur, "is_day") is not { } d || d >= 1,
                UvIndex      = NumOrNull(cur, "uv_index"),
                RainingNow   = (NumOrNull(cur, "precipitation") ?? 0) > 0.05 || IsWet(kind),
                RainStartsAt = SafeValue(() => ParseOpenMeteoRainStart(root.GetProperty("minutely_15"), localNow)),
                Hourly       = Safe(() => ParseOpenMeteoHourly(root.GetProperty("hourly"), localNow)),
            };
        }

        private static List<DailyForecast> ParseOpenMeteoDaily(JsonElement daily)
        {
            var dates = daily.GetProperty("time");
            var codes = daily.GetProperty("weather_code");
            var max   = daily.GetProperty("temperature_2m_max");
            var min   = daily.GetProperty("temperature_2m_min");
            daily.TryGetProperty("precipitation_probability_max", out var rain);
            daily.TryGetProperty("sunrise", out var sunrise);
            daily.TryGetProperty("sunset", out var sunset);
            daily.TryGetProperty("uv_index_max", out var uv);

            var list = new List<DailyForecast>();
            for (int i = 0; i < dates.GetArrayLength(); i++)
            {
                if (max[i].ValueKind != JsonValueKind.Number || min[i].ValueKind != JsonValueKind.Number) continue;
                list.Add(new DailyForecast(DateOnly.Parse(dates[i].GetString()!, Inv),
                    min[i].GetDouble(), max[i].GetDouble(),
                    codes[i].ValueKind == JsonValueKind.Number ? FromWmo(codes[i].GetInt32()) : WeatherKind.Unknown,
                    ArrInt(rain, i))
                {
                    Sunrise = ArrTime(sunrise, i),
                    Sunset  = ArrTime(sunset, i),
                    UvMax   = ArrNum(uv, i),
                });
            }
            return list;
        }

        private static List<HourlyForecast> ParseOpenMeteoHourly(JsonElement hourly, DateTime localNow)
        {
            var times = hourly.GetProperty("time");
            var temps = hourly.GetProperty("temperature_2m");
            var codes = hourly.GetProperty("weather_code");
            hourly.TryGetProperty("precipitation_probability", out var rain);
            hourly.TryGetProperty("is_day", out var isDay);

            var startHour = localNow.Date.AddHours(localNow.Hour);
            var list = new List<HourlyForecast>();
            for (int i = 0; i < times.GetArrayLength() && list.Count < HourlyCount; i++)
            {
                if (ArrTime(times, i) is not { } t || t < startHour) continue;
                if (temps[i].ValueKind != JsonValueKind.Number) continue;
                list.Add(new HourlyForecast(t, temps[i].GetDouble(),
                    codes[i].ValueKind == JsonValueKind.Number ? FromWmo(codes[i].GetInt32()) : WeatherKind.Unknown,
                    ArrNum(isDay, i) is not { } d || d >= 1,
                    ArrInt(rain, i)));
            }
            return list;
        }

        /// <summary>First 15-minute slot from now with ≥ 0.1 mm of rain.</summary>
        internal static DateTime? ParseOpenMeteoRainStart(JsonElement m15, DateTime localNow)
        {
            var times = m15.GetProperty("time");
            var precip = m15.GetProperty("precipitation");
            for (int i = 0; i < times.GetArrayLength(); i++)
            {
                if (ArrTime(times, i) is not { } t || t.AddMinutes(15) <= localNow) continue;
                if ((ArrNum(precip, i) ?? 0) >= 0.1) return t < localNow ? localNow : t;
            }
            return null;
        }

        // ================================================================ MET Norway

        private async Task<WeatherData> FetchMetNorwayAsync(string lat, string lon, TimeZoneInfo zone, CancellationToken ct)
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
            var symbol   = MetSymbol(now);
            var kind     = FromMetSymbol(symbol);

            return new WeatherData(temp, rh, windM * 3.6, ApparentTemperature(temp, rh, windM),
                                   kind, "MET Norway", Safe(() => ParseMetDaily(series, zone)))
            {
                UtcOffset    = zone.GetUtcOffset(DateTime.UtcNow),
                IsDay        = MetIsDay(symbol, LocalNow(zone)),
                RainingNow   = (MetPrecip(now) ?? 0) > 0.05 || IsWet(kind),
                RainStartsAt = SafeValue(() => ParseMetRainStart(series, zone)),
                Hourly       = Safe(() => ParseMetHourly(series, zone)),
            };
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

        private static double? MetPrecip(JsonElement data) =>
            data.TryGetProperty("next_1_hours", out var b) && b.TryGetProperty("details", out var d) ? NumOrNull(d, "precipitation_amount") : null;

        /// <summary>Symbols end in _day/_night/_polartwilight when it matters; otherwise guess from the clock.</summary>
        private static bool MetIsDay(string symbol, DateTime local) =>
            symbol.EndsWith("_night") ? false : symbol.EndsWith("_day") || symbol.EndsWith("_polartwilight") || local.Hour is >= 6 and < 18;

        private static DateTime MetLocalTime(JsonElement entry, TimeZoneInfo zone) =>
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.Parse(entry.GetProperty("time").GetString()!, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), zone);

        /// <summary>MET gives a UTC time series — group it into local days: min/max temp, symbol nearest noon.</summary>
        private static List<DailyForecast> ParseMetDaily(JsonElement series, TimeZoneInfo zone)
        {
            var days = new SortedDictionary<DateOnly, (double Min, double Max, string Symbol, double NoonDist)>();
            foreach (var entry in series.EnumerateArray())
            {
                var local = MetLocalTime(entry, zone);
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

        private static List<HourlyForecast> ParseMetHourly(JsonElement series, TimeZoneInfo zone)
        {
            var now   = LocalNow(zone);
            var start = now.Date.AddHours(now.Hour);
            var list  = new List<HourlyForecast>();
            foreach (var entry in series.EnumerateArray())
            {
                var t = MetLocalTime(entry, zone);
                if (t < start) continue;
                if (list.Count >= HourlyCount || t > start.AddHours(HourlyCount)) break;
                var data = entry.GetProperty("data");
                var sym  = MetSymbol(data);
                list.Add(new HourlyForecast(t,
                    data.GetProperty("instant").GetProperty("details").GetProperty("air_temperature").GetDouble(),
                    FromMetSymbol(sym), MetIsDay(sym, t), null));
            }
            return list;
        }

        /// <summary>MET has hourly precipitation amounts: first of the next two hours with rain.</summary>
        private static DateTime? ParseMetRainStart(JsonElement series, TimeZoneInfo zone)
        {
            var now = LocalNow(zone);
            for (int i = 0; i < Math.Min(3, series.GetArrayLength()); i++)
            {
                var entry = series[i];
                var t = MetLocalTime(entry, zone);
                if (t.AddHours(1) <= now) continue;
                if ((MetPrecip(entry.GetProperty("data")) ?? 0) >= 0.1) return t < now ? now : t;
            }
            return null;
        }

        // ================================================================ wttr.in

        private async Task<WeatherData> FetchWttrAsync(string lat, string lon, TimeZoneInfo zone, CancellationToken ct)
        {
            var url = $"https://wttr.in/{lat},{lon}?format=j1";
            using var doc = await GetJsonAsync(url, ct);
            var root = doc.RootElement;
            var cur  = root.GetProperty("current_condition")[0];
            var days = Safe(() => ParseWttrDaily(root.GetProperty("weather")));
            var now   = LocalNow(zone); // wttr.in reports times in the location's local time
            var today = days.FirstOrDefault(d => d.Date == DateOnly.FromDateTime(now));
            var kind = FromWwo((int)Str(cur, "weatherCode"));

            return new WeatherData(Str(cur, "temp_C"), Str(cur, "humidity"), Str(cur, "windspeedKmph"), Str(cur, "FeelsLikeC"),
                                   kind, "wttr.in", days)
            {
                UtcOffset  = zone.GetUtcOffset(DateTime.UtcNow),
                IsDay      = today is { Sunrise: { } sr, Sunset: { } ss } ? now >= sr && now < ss : now.Hour is >= 6 and < 18,
                UvIndex    = SafeValue<double>(() => Str(cur, "uvIndex")),
                RainingNow = IsWet(kind),
                Hourly     = Safe(() => ParseWttrHourly(root.GetProperty("weather"), days, now)),
            };
        }

        /// <summary>wttr.in: 3 days; condition from the noon slot (hourly[4] = 12:00); sunrise/sunset from "astronomy".</summary>
        private static List<DailyForecast> ParseWttrDaily(JsonElement weather) =>
            weather.EnumerateArray().Select(day =>
            {
                var date   = DateOnly.Parse(day.GetProperty("date").GetString()!, Inv);
                var hourly = day.GetProperty("hourly");
                var noon   = hourly[Math.Min(4, hourly.GetArrayLength() - 1)];
                int rain   = hourly.EnumerateArray().Max(h => (int)Str(h, "chanceofrain"));
                var astro  = day.TryGetProperty("astronomy", out var a) && a.GetArrayLength() > 0 ? a[0] : default;
                return new DailyForecast(date, Str(day, "mintempC"), Str(day, "maxtempC"), FromWwo((int)Str(noon, "weatherCode")), rain)
                {
                    Sunrise = WttrClock(astro, "sunrise", date),
                    Sunset  = WttrClock(astro, "sunset", date),
                    UvMax   = SafeValue<double>(() => Str(day, "uvIndex")),
                };
            }).ToList();

        /// <summary>3-hourly slots ("0", "300" … "2100") from today and tomorrow.</summary>
        private static List<HourlyForecast> ParseWttrHourly(JsonElement weather, List<DailyForecast> days, DateTime now)
        {
            var from = now.AddHours(-2);
            var list = new List<HourlyForecast>();
            foreach (var day in weather.EnumerateArray())
            {
                var date = DateOnly.Parse(day.GetProperty("date").GetString()!, Inv);
                var info = days.FirstOrDefault(d => d.Date == date);
                foreach (var h in day.GetProperty("hourly").EnumerateArray())
                {
                    var t = date.ToDateTime(TimeOnly.MinValue).AddHours((int)Str(h, "time") / 100);
                    if (t < from || t > now.AddHours(HourlyCount)) continue;
                    bool isDay = info is { Sunrise: { } sr, Sunset: { } ss } ? t >= sr && t < ss : t.Hour is >= 6 and < 18;
                    list.Add(new HourlyForecast(t, Str(h, "tempC"), FromWwo((int)Str(h, "weatherCode")), isDay, (int)Str(h, "chanceofrain")));
                }
            }
            return list;
        }

        private static DateTime? WttrClock(JsonElement astro, string name, DateOnly date)
        {
            if (astro.ValueKind != JsonValueKind.Object || !astro.TryGetProperty(name, out var v)) return null;
            return DateTime.TryParseExact(v.GetString(), "hh:mm tt", Inv, DateTimeStyles.None, out var t)
                ? date.ToDateTime(TimeOnly.FromDateTime(t)) : null;
        }

        // ================================================================ helpers

        private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }

        private static double Str(JsonElement el, string name) => double.Parse(el.GetProperty(name).GetString()!, Inv);

        private static double? NumOrNull(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        private static double? ArrNum(JsonElement arr, int i) =>
            arr.ValueKind == JsonValueKind.Array && i < arr.GetArrayLength() && arr[i].ValueKind == JsonValueKind.Number ? arr[i].GetDouble() : null;

        private static int? ArrInt(JsonElement arr, int i) => ArrNum(arr, i) is { } d ? (int)Math.Round(d) : null;

        private static DateTime? ArrTime(JsonElement arr, int i) =>
            arr.ValueKind == JsonValueKind.Array && i < arr.GetArrayLength() && arr[i].ValueKind == JsonValueKind.String &&
            DateTime.TryParse(arr[i].GetString(), Inv, DateTimeStyles.None, out var t) ? t : null;

        private static bool IsWet(WeatherKind k) => k is WeatherKind.Drizzle or WeatherKind.Rain or WeatherKind.Thunder;

        /// <summary>Optional parts: a parsing problem must never cost us the current weather.</summary>
        private static List<T> Safe<T>(Func<List<T>> parse)
        {
            try { return parse(); }
            catch (Exception ex) { AppLog.Warn($"Optional {typeof(T).Name} data skipped", ex); return []; }
        }

        private static T? SafeValue<T>(Func<T?> parse) where T : struct
        {
            try { return parse(); }
            catch { return null; }
        }

        private static T? SafeValue<T>(Func<T> parse) where T : struct
        {
            try { return parse(); }
            catch { return null; }
        }

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
