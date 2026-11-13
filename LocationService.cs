using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Devices.Geolocation;

namespace frmweatherwid
{
    internal enum LocationSource { Windows, IP, Manual, Cached, Default }

    internal sealed record GeoLocation(double Latitude, double Longitude, string Name, LocationSource Source)
    {
        /// <summary>IANA time zone of a manually chosen city (null = this PC's zone, right for auto-detected places).</summary>
        public string? TimeZone { get; init; }

        /// <summary>Rough distance in km (equirectangular — plenty for "did we move?").</summary>
        public double DistanceKm(GeoLocation other)
        {
            const double R = 6371;
            double x = (other.Longitude - Longitude) * Math.PI / 180 * Math.Cos((Latitude + other.Latitude) * Math.PI / 360);
            double y = (other.Latitude - Latitude) * Math.PI / 180;
            return Math.Sqrt(x * x + y * y) * R;
        }
    }

    /// <summary>A search hit from the city picker.</summary>
    internal sealed record CityResult(double Latitude, double Longitude, string Name, string Detail, string? TimeZone = null)
    {
        public override string ToString() => string.IsNullOrEmpty(Detail) ? Name : $"{Name} — {Detail}";
    }

    /// <summary>
    /// Where are we? Three layers, all free and key-less:
    ///   1. Manual city chosen by the user (if set, auto-detection is skipped)
    ///   2. Windows Location (Wi-Fi/GPS, needs Settings → Privacy → Location on)
    ///   3. IP geolocation (ipwho.is → ipinfo.io; city-level, shows the VPN exit when on a VPN)
    /// then the last auto-detected location, then Hanoi.
    /// </summary>
    internal sealed class LocationService(HttpClient http)
    {
        public static GeoLocation DefaultLocation => new(21.0245, 105.8412, L.T("Hà Nội", "Hanoi"), LocationSource.Default);

        private const string SettingsKeyPath = @"Software\FrmWeatherWidget";
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(6);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------ settings (registry)

        public GeoLocation? ManualLocation
        {
            get => ReadLocation("Manual", LocationSource.Manual);
            set
            {
                if (value is null) DeleteLocation("Manual");
                else WriteLocation("Manual", value);
            }
        }

        /// <summary>Last successfully auto-detected location — instant start and offline fallback.</summary>
        public GeoLocation? CachedLocation => ReadLocation("Last", LocationSource.Cached);

        // ------------------------------------------------------------------ resolve

        public async Task<GeoLocation> ResolveAsync(CancellationToken ct = default)
        {
            if (ManualLocation is { } manual) return manual;

            var found = await TryWindowsAsync(ct) ?? await TryIpAsync(ct);
            if (found is not null)
            {
                WriteLocation("Last", found);
                return found;
            }
            return CachedLocation ?? DefaultLocation;
        }

        private async Task<GeoLocation?> TryWindowsAsync(CancellationToken ct)
        {
            try
            {
                if (await Geolocator.RequestAccessAsync().AsTask(ct) != GeolocationAccessStatus.Allowed)
                    return null; // location turned off for desktop apps

                var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
                var pos = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10)).AsTask(ct);
                var p = pos.Coordinate.Point.Position;
                var name = await ReverseGeocodeAsync(p.Latitude, p.Longitude, ct) ?? FormatCoords(p.Latitude, p.Longitude);
                return new GeoLocation(p.Latitude, p.Longitude, name, LocationSource.Windows);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }

        private async Task<GeoLocation?> TryIpAsync(CancellationToken ct)
        {
            // ipwho.is
            try
            {
                using var doc = await GetJsonAsync("https://ipwho.is/?fields=success,latitude,longitude,city", ct);
                var r = doc.RootElement;
                if (r.GetProperty("success").GetBoolean())
                {
                    double lat = r.GetProperty("latitude").GetDouble(), lon = r.GetProperty("longitude").GetDouble();
                    var city = Str(r, "city");
                    return new GeoLocation(lat, lon, city.Length > 0 ? city : FormatCoords(lat, lon), LocationSource.IP);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }

            // ipinfo.io ("loc": "lat,lon")
            try
            {
                using var doc = await GetJsonAsync("https://ipinfo.io/json", ct);
                var r = doc.RootElement;
                var loc = r.GetProperty("loc").GetString()!.Split(',');
                double lat = double.Parse(loc[0], Inv), lon = double.Parse(loc[1], Inv);
                var city = r.TryGetProperty("city", out var c) ? c.GetString() : null;
                return new GeoLocation(lat, lon, string.IsNullOrEmpty(city) ? FormatCoords(lat, lon) : city, LocationSource.IP);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }

            return null;
        }

        // ------------------------------------------------------------------ geocoding

        /// <summary>City name for coordinates: BigDataCloud → Nominatim. Null if both fail.</summary>
        private async Task<string?> ReverseGeocodeAsync(double lat, double lon, CancellationToken ct)
        {
            string la = lat.ToString(Inv), lo = lon.ToString(Inv);
            try
            {
                using var doc = await GetJsonAsync(
                    $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={la}&longitude={lo}&localityLanguage={L.Code}", ct);
                var r = doc.RootElement;
                foreach (var key in (string[])["city", "locality", "principalSubdivision"])
                    if (r.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 } s) return s;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }

            try
            {
                using var doc = await GetJsonAsync(
                    $"https://nominatim.openstreetmap.org/reverse?lat={la}&lon={lo}&format=jsonv2&zoom=10&accept-language={L.Code}", ct);
                if (doc.RootElement.TryGetProperty("address", out var a))
                    foreach (var key in (string[])["city", "town", "village", "county", "state"])
                        if (a.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 } s) return s;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }

            return null;
        }

        /// <summary>City search for the picker: Open-Meteo geocoding → Nominatim.</summary>
        public async Task<List<CityResult>> SearchCitiesAsync(string query, CancellationToken ct = default)
        {
            var q = Uri.EscapeDataString(query.Trim());
            try
            {
                using var doc = await GetJsonAsync(
                    $"https://geocoding-api.open-meteo.com/v1/search?name={q}&count=10&language={L.Code}&format=json", ct);
                if (doc.RootElement.TryGetProperty("results", out var results))
                {
                    var list = results.EnumerateArray().Select(el => new CityResult(
                        el.GetProperty("latitude").GetDouble(),
                        el.GetProperty("longitude").GetDouble(),
                        el.GetProperty("name").GetString() ?? "",
                        string.Join(", ", new[] { Str(el, "admin1"), Str(el, "country") }.Where(s => s.Length > 0)),
                        Str(el, "timezone") is { Length: > 0 } tz ? tz : null))
                        .ToList();
                    if (list.Count > 0) return list;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }

            try
            {
                using var doc = await GetJsonAsync(
                    $"https://nominatim.openstreetmap.org/search?q={q}&format=jsonv2&limit=10&accept-language={L.Code}", ct);
                return doc.RootElement.EnumerateArray().Select(el =>
                {
                    var display = Str(el, "display_name");
                    var name    = Str(el, "name") is { Length: > 0 } n ? n : display.Split(',')[0];
                    var comma   = display.IndexOf(',');
                    return new CityResult(
                        double.Parse(Str(el, "lat"), Inv), double.Parse(Str(el, "lon"), Inv),
                        name, comma >= 0 ? display[(comma + 1)..].Trim() : "");
                }).ToList();
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }

            return [];
        }

        // ------------------------------------------------------------------ helpers

        private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HttpTimeout);
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
        }

        private static string Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static string FormatCoords(double lat, double lon) => $"{lat:0.00}, {lon:0.00}";

        private static GeoLocation? ReadLocation(string prefix, LocationSource source)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue(prefix + "Lat") is not string lat ||
                    key.GetValue(prefix + "Lon") is not string lon ||
                    key.GetValue(prefix + "Name") is not string name) return null;
                return new GeoLocation(double.Parse(lat, Inv), double.Parse(lon, Inv), name, source)
                {
                    TimeZone = key.GetValue(prefix + "Tz") as string,
                };
            }
            catch
            {
                return null;
            }
        }

        private static void WriteLocation(string prefix, GeoLocation loc)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
                key.SetValue(prefix + "Lat",  loc.Latitude.ToString("R", Inv));
                key.SetValue(prefix + "Lon",  loc.Longitude.ToString("R", Inv));
                key.SetValue(prefix + "Name", loc.Name);
                if (loc.TimeZone is { Length: > 0 } tz) key.SetValue(prefix + "Tz", tz);
                else key.DeleteValue(prefix + "Tz", throwOnMissingValue: false);
            }
            catch { }
        }

        private static void DeleteLocation(string prefix)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
                foreach (var suffix in (string[])["Lat", "Lon", "Name", "Tz"])
                    key.DeleteValue(prefix + suffix, throwOnMissingValue: false);
            }
            catch { }
        }
    }
}
