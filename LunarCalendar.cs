namespace frmweatherwid
{
    internal readonly record struct LunarDate(int Day, int Month, int Year, bool Leap)
    {
        private static readonly string[] Can = ["Giáp", "Ất", "Bính", "Đinh", "Mậu", "Kỷ", "Canh", "Tân", "Nhâm", "Quý"];
        private static readonly string[] Chi = ["Tý", "Sửu", "Dần", "Mão", "Thìn", "Tỵ", "Ngọ", "Mùi", "Thân", "Dậu", "Tuất", "Hợi"];

        /// <summary>Can Chi of the lunar year, e.g. 2026 → "Bính Ngọ".</summary>
        public string YearName => $"{Can[(Year + 6) % 10]} {Chi[(Year + 8) % 12]}";
    }

    /// <summary>
    /// Vietnamese lunar calendar (Hồ Ngọc Đức's algorithm, astronomical new moons and solar terms).
    /// Computed for UTC+7 on purpose: .NET's ChineseLunisolarCalendar uses China's UTC+8, which puts some
    /// Vietnamese month starts — and a few Tết days, e.g. 2007 — on a different date.
    /// </summary>
    internal static class LunarCalendar
    {
        private const double VietnamTimeZone = 7.0;

        public static LunarDate FromSolar(DateTime date) => FromSolar(date.Day, date.Month, date.Year, VietnamTimeZone);

        private static LunarDate FromSolar(int dd, int mm, int yy, double tz)
        {
            long dayNumber = JdFromDate(dd, mm, yy);
            long k = (long)Math.Floor((dayNumber - 2415021.076998695) / 29.530588853);
            long monthStart = NewMoonDay(k + 1, tz);
            if (monthStart > dayNumber) monthStart = NewMoonDay(k, tz);

            long a11 = LunarMonth11(yy, tz), b11 = a11;
            int lunarYear;
            if (a11 >= monthStart)
            {
                lunarYear = yy;
                a11 = LunarMonth11(yy - 1, tz);
            }
            else
            {
                lunarYear = yy + 1;
                b11 = LunarMonth11(yy + 1, tz);
            }

            int lunarDay = (int)(dayNumber - monthStart + 1);
            int diff = (int)Math.Floor((monthStart - a11) / 29.0);
            bool leap = false;
            int lunarMonth = diff + 11;
            if (b11 - a11 > 365) // this lunar year has a leap month
            {
                int leapDiff = LeapMonthOffset(a11, tz);
                if (diff >= leapDiff)
                {
                    lunarMonth = diff + 10;
                    leap = diff == leapDiff;
                }
            }
            if (lunarMonth > 12) lunarMonth -= 12;
            if (lunarMonth >= 11 && diff < 4) lunarYear--;
            return new LunarDate(lunarDay, lunarMonth, lunarYear, leap);
        }

        /// <summary>Julian day number of a Gregorian date.</summary>
        private static long JdFromDate(int dd, int mm, int yy)
        {
            int a = (14 - mm) / 12;
            long y = yy + 4800 - a;
            long m = mm + 12 * a - 3;
            long jd = dd + (153 * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045;
            if (jd < 2299161) jd = dd + (153 * m + 2) / 5 + 365 * y + y / 4 - 32083;
            return jd;
        }

        /// <summary>Day number of the k-th new moon after 1900-01-01, in local time.</summary>
        private static long NewMoonDay(long k, double tz)
        {
            double T = k / 1236.85, T2 = T * T, T3 = T2 * T, dr = Math.PI / 180;
            double jd1 = 2415020.75933 + 29.53058868 * k + 0.0001178 * T2 - 0.000000155 * T3;
            jd1 += 0.00033 * Math.Sin((166.56 + 132.87 * T - 0.009173 * T2) * dr);
            double M   = 359.2242 + 29.10535608 * k - 0.0000333 * T2 - 0.00000347 * T3;
            double Mpr = 306.0253 + 385.81691806 * k + 0.0107306 * T2 + 0.00001236 * T3;
            double F   = 21.2964 + 390.67050646 * k - 0.0016528 * T2 - 0.00000239 * T3;
            double c1 = (0.1734 - 0.000393 * T) * Math.Sin(M * dr) + 0.0021 * Math.Sin(2 * dr * M);
            c1 = c1 - 0.4068 * Math.Sin(Mpr * dr) + 0.0161 * Math.Sin(dr * 2 * Mpr);
            c1 -= 0.0004 * Math.Sin(dr * 3 * Mpr);
            c1 = c1 + 0.0104 * Math.Sin(dr * 2 * F) - 0.0051 * Math.Sin(dr * (M + Mpr));
            c1 = c1 - 0.0074 * Math.Sin(dr * (M - Mpr)) + 0.0004 * Math.Sin(dr * (2 * F + M));
            c1 = c1 - 0.0004 * Math.Sin(dr * (2 * F - M)) - 0.0006 * Math.Sin(dr * (2 * F + Mpr));
            c1 = c1 + 0.0010 * Math.Sin(dr * (2 * F - Mpr)) + 0.0005 * Math.Sin(dr * (2 * Mpr + M));
            double deltaT = T < -11
                ? 0.001 + 0.000839 * T + 0.0002261 * T2 - 0.00000845 * T3 - 0.000000081 * T * T3
                : -0.000278 + 0.000265 * T + 0.000262 * T2;
            return (long)Math.Floor(jd1 + c1 - deltaT + 0.5 + tz / 24);
        }

        /// <summary>Which of the 12 solar-term sectors (0…11) the sun is in at local midnight of that day.</summary>
        private static int SunLongitudeSector(long jdn, double tz)
        {
            double T = (jdn - 2451545.5 - tz / 24) / 36525, T2 = T * T, dr = Math.PI / 180;
            double M  = 357.52910 + 35999.05030 * T - 0.0001559 * T2 - 0.00000048 * T * T2;
            double L0 = 280.46645 + 36000.76983 * T + 0.0003032 * T2;
            double DL = (1.914600 - 0.004817 * T - 0.000014 * T2) * Math.Sin(dr * M);
            DL = DL + (0.019993 - 0.000101 * T) * Math.Sin(dr * 2 * M) + 0.000290 * Math.Sin(dr * 3 * M);
            double L = (L0 + DL) * dr;
            L -= Math.PI * 2 * Math.Floor(L / (Math.PI * 2));
            return (int)Math.Floor(L / Math.PI * 6);
        }

        /// <summary>Start of lunar month 11 (the one containing the winter solstice) of year <paramref name="yy"/>.</summary>
        private static long LunarMonth11(int yy, double tz)
        {
            long off = JdFromDate(31, 12, yy) - 2415021;
            long k = (long)Math.Floor(off / 29.530588853);
            long nm = NewMoonDay(k, tz);
            if (SunLongitudeSector(nm, tz) >= 9) nm = NewMoonDay(k - 1, tz);
            return nm;
        }

        /// <summary>Which month after month 11 is the leap month (the first one without a major solar term).</summary>
        private static int LeapMonthOffset(long a11, double tz)
        {
            long k = (long)Math.Floor((a11 - 2415021.076998695) / 29.530588853 + 0.5);
            int i = 1, last;
            int arc = SunLongitudeSector(NewMoonDay(k + i, tz), tz);
            do
            {
                last = arc;
                i++;
                arc = SunLongitudeSector(NewMoonDay(k + i, tz), tz);
            } while (arc != last && i < 14);
            return i - 1;
        }
    }
}
