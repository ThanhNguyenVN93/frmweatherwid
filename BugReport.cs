using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace frmweatherwid
{
    /// <summary>
    /// Opens a pre-filled "New issue" page on GitHub (version, OS, language, recent log lines).
    /// Nothing is sent automatically — the user sees and can edit everything before submitting.
    /// </summary>
    internal static class BugReport
    {
        private const string NewIssueUrl = "https://github.com/ThanhNguyenVN93/weather-widget/issues/new";
        private const int MaxUrlLength   = 7500; // browsers/GitHub get unhappy with much longer URLs

        public static void Open(Exception? ex = null)
        {
            string title = ex is null ? "" : $"[Bug] {ex.GetType().Name}: {Truncate(ex.Message, 80)}";

            for (int logLines = 40; logLines >= 0; logLines -= 10)
            {
                var url = $"{NewIssueUrl}?labels=bug&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(Body(ex, logLines))}";
                if (url.Length <= MaxUrlLength || logLines == 0)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch (Exception e)
                    {
                        AppLog.Warn("Couldn't open the bug report page", e);
                    }
                    return;
                }
            }
        }

        private static string Body(Exception? ex, int logLines)
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.T("### Mô tả lỗi", "### What happened"));
            sb.AppendLine(L.T("<!-- Bạn đang làm gì, lỗi xảy ra thế nào? Có thể đính kèm ảnh chụp. -->",
                              "<!-- What were you doing, what went wrong? Screenshots welcome. -->"));
            sb.AppendLine();
            sb.AppendLine("### Environment");
            sb.AppendLine($"- Weather Widget: v{UpdateService.CurrentVersion.ToString(3)}");
            sb.AppendLine($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"- .NET: {Environment.Version}");
            sb.AppendLine($"- Language: {L.Code}");
            if (ex is not null)
            {
                sb.AppendLine();
                sb.AppendLine("### Error");
                sb.AppendLine("```");
                sb.AppendLine(Truncate(ex.ToString(), 2500));
                sb.AppendLine("```");
            }
            var log = logLines > 0 ? AppLog.Tail(logLines) : "";
            if (log.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Log");
                sb.AppendLine("```");
                sb.AppendLine(log);
                sb.AppendLine("```");
            }
            return sb.ToString();
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
