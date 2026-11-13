namespace frmweatherwid
{
    /// <summary>When to warn — pure functions, so they're unit-tested (AppController only shows the balloon).</summary>
    internal static class AlertRules
    {
        public const int RainWindowMinutes = 60;   // warn when rain starts within this window
        public const int AqiAlertLevel     = 151;  // "Unhealthy" and worse
        public const int AqiAlertReset     = 100;  // re-arm once it's back to Moderate

        /// <summary>
        /// Minutes until rain if we should warn now, otherwise null.
        /// Not while it's already raining, and only once per rain event (a start more than 2 h after the last warned one is new).
        /// </summary>
        public static int? RainAlert(WeatherData data, DateTime? lastAlertedStart)
        {
            if (data.RainingNow || data.RainStartsAt is not { } start) return null;
            var minutes = (int)Math.Round((start - data.LocalNow).TotalMinutes);
            if (minutes < 0 || minutes > RainWindowMinutes) return null;
            bool newEvent = lastAlertedStart is not { } prev || start > prev.AddHours(2);
            return newEvent ? minutes : null;
        }

        /// <summary>
        /// AQI warning with hysteresis: fires once when crossing <see cref="AqiAlertLevel"/>,
        /// re-arms only after it has dropped below <see cref="AqiAlertReset"/>. Returns (warn now, new "already warned" state).
        /// </summary>
        public static (bool Alert, bool Alerted) AqiAlert(int aqi, bool alreadyAlerted)
        {
            if (aqi < AqiAlertReset) return (false, false);
            if (aqi >= AqiAlertLevel && !alreadyAlerted) return (true, true);
            return (false, alreadyAlerted);
        }
    }
}
