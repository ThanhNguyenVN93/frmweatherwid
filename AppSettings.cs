using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace frmweatherwid
{
    internal enum TempUnit { Celsius, Fahrenheit }
    internal enum WallpaperMode { Online, Folder, None }

    internal sealed record SavedCity(double Latitude, double Longitude, string Name);

    /// <summary>
    /// User settings in HKCU\Software\FrmWeatherWidget (same key as before, so nothing is lost).
    /// Every setter saves immediately and raises <see cref="Changed"/> — the Settings window applies live.
    /// </summary>
    internal static class AppSettings
    {
        private const string KeyPath = @"Software\FrmWeatherWidget";
        public const int MaxSavedCities = 8;

        public static event Action? Changed;

        public static TempUnit Unit
        {
            get => Get("TempUnit") == "F" ? TempUnit.Fahrenheit : TempUnit.Celsius;
            set => Set("TempUnit", value == TempUnit.Fahrenheit ? "F" : "C");
        }

        /// <summary>Widget opacity 0.5 … 1.0.</summary>
        public static double Opacity
        {
            get => double.TryParse(Get("Opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0.5, 1.0) : 0.95;
            set => Set("Opacity", Math.Clamp(value, 0.5, 1.0).ToString("0.00", CultureInfo.InvariantCulture));
        }

        public static WallpaperMode Wallpaper
        {
            get => Enum.TryParse<WallpaperMode>(Get("WallpaperMode"), out var m) ? m : WallpaperMode.Online;
            set => Set("WallpaperMode", value.ToString());
        }

        public static string WallpaperFolder
        {
            get => Get("WallpaperFolder") ?? "";
            set => Set("WallpaperFolder", value);
        }

        public static bool RainAlert
        {
            get => Get("RainAlert") != "0";
            set => Set("RainAlert", value ? "1" : "0");
        }

        public static bool AqiAlert
        {
            get => Get("AqiAlert") != "0";
            set => Set("AqiAlert", value ? "1" : "0");
        }

        /// <summary>Global show/hide hotkey (Keys with modifier flags); Keys.None = off. Default Ctrl+Alt+W.</summary>
        public static Keys Hotkey
        {
            get => int.TryParse(Get("Hotkey"), out var k) ? (Keys)k : Keys.Control | Keys.Alt | Keys.W;
            set => Set("Hotkey", ((int)value).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Vietnamese lunar date under the Gregorian date (default on).</summary>
        public static bool ShowLunar
        {
            get => Get("ShowLunar") != "0";
            set => Set("ShowLunar", value ? "1" : "0");
        }

        /// <summary>One widget on every monitor (default) or only on the primary one.</summary>
        public static bool AllMonitors
        {
            get => Get("AllMonitors") != "0";
            set => Set("AllMonitors", value ? "1" : "0");
        }

        public static List<SavedCity> SavedCities
        {
            get => Json<List<SavedCity>>("SavedCities") ?? [];
            set => Set("SavedCities", JsonSerializer.Serialize(value.Take(MaxSavedCities).ToList()));
        }

        /// <summary>Most-recently-used first; same place (≈1 km) isn't added twice.</summary>
        public static void RememberCity(SavedCity city)
        {
            var list = SavedCities;
            list.RemoveAll(c => Math.Abs(c.Latitude - city.Latitude) < 0.01 && Math.Abs(c.Longitude - city.Longitude) < 0.01);
            list.Insert(0, city);
            SavedCities = list;
        }

        public static void ForgetCity(SavedCity city)
        {
            var list = SavedCities;
            list.RemoveAll(c => c == city);
            SavedCities = list;
        }

        /// <summary>Saved widget top-left per monitor (Screen.DeviceName).</summary>
        public static Point? GetPosition(string screen) =>
            Json<Dictionary<string, int[]>>("Positions") is { } d && d.TryGetValue(screen, out var p) && p.Length == 2
                ? new Point(p[0], p[1]) : null;

        public static void SetPosition(string screen, Point p)
        {
            var d = Json<Dictionary<string, int[]>>("Positions") ?? [];
            d[screen] = [p.X, p.Y];
            Write("Positions", JsonSerializer.Serialize(d)); // no Changed event: dragging isn't a settings change
        }

        // ---------------------------------------------------------------- registry helpers

        private static string? Get(string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(name) as string;
            }
            catch
            {
                return null;
            }
        }

        private static void Set(string name, string value)
        {
            if (Get(name) == value) return;
            Write(name, value);
            Changed?.Invoke();
        }

        private static void Write(string name, string value)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key.SetValue(name, value);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Couldn't save setting {name}", ex);
            }
        }

        private static T? Json<T>(string name) where T : class
        {
            try
            {
                return Get(name) is { Length: > 0 } s ? JsonSerializer.Deserialize<T>(s) : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>°C / °F formatting. Data is always stored in °C and km/h.</summary>
    internal static class Units
    {
        public static bool F => AppSettings.Unit == TempUnit.Fahrenheit;

        public static double Convert(double c) => F ? c * 9 / 5 + 32 : c;

        /// <summary>"19°" (short) or "19.1°C" (precise).</summary>
        public static string Temp(double c, bool precise = false, bool suffix = false)
        {
            var v = Convert(c);
            var s = precise ? v.ToString("0.#", CultureInfo.CurrentCulture) : Math.Round(v).ToString(CultureInfo.CurrentCulture);
            return s + (suffix ? Symbol : "°");
        }

        public static string Symbol => F ? "°F" : "°C";

        public static string Wind(double kph) => F ? $"{kph * 0.621371:0} mph" : $"{kph:0} km/h";
    }
}
