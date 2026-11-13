using System.Security.Cryptography;
using System.Text.Json;

namespace frmweatherwid
{
    internal sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size, string? Sha256);

    /// <summary>
    /// Self-update from GitHub Releases (public repo, no token):
    ///   check latest release → download WeatherWidget.exe next to the running exe (*.new)
    ///   → verify size + SHA-256 → swap files (running exe renamed to *.old) → restart.
    /// Only the published single-file exe updates itself; dev builds (bin\...) never do.
    /// </summary>
    internal sealed class UpdateService(HttpClient http)
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/ThanhNguyenVN93/frmweatherwid/releases/latest";
        private const string AssetName        = "WeatherWidget.exe";

        public static Version CurrentVersion { get; } = ParseVersion(Application.ProductVersion) ?? new Version(0, 0, 0);

        private static string? ExePath => Environment.ProcessPath;

        /// <summary>
        /// True only for the single-file publish named WeatherWidget.exe.
        /// A normal build (bin\...) has WeatherWidget.dll next to the exe; the single-file bundle doesn't.
        /// </summary>
        public static bool CanSelfUpdate =>
            ExePath is { } p &&
            string.Equals(Path.GetFileName(p), AssetName, StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(Path.ChangeExtension(p, ".dll"));

        /// <summary>Newer release info, or null when already up to date.</summary>
        public async Task<UpdateInfo?> CheckAsync(string? skipTag = null, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var resp = await http.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            var root = doc.RootElement;

            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pr) && pr.GetBoolean()) return null;

            var tag     = root.GetProperty("tag_name").GetString() ?? "";
            var version = ParseVersion(tag);
            if (version is null || version <= CurrentVersion) return null;
            if (!string.IsNullOrEmpty(skipTag) && string.Equals(tag, skipTag, StringComparison.OrdinalIgnoreCase)) return null; // rolled back here before

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                string? sha = asset.TryGetProperty("digest", out var dg) && dg.GetString() is { } s &&
                              s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? s[7..] : null;
                return new UpdateInfo(version, tag,
                    asset.GetProperty("browser_download_url").GetString()!,
                    asset.GetProperty("size").GetInt64(), sha);
            }
            return null; // release without the exe
        }

        /// <summary>Downloads the new exe next to the current one and verifies it. Returns the *.new path.</summary>
        public async Task<string> DownloadAsync(UpdateInfo info, CancellationToken ct = default)
        {
            var target = ExePath + ".new";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                using (var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
                    await using var dst = File.Create(target);
                    await src.CopyToAsync(dst, cts.Token);
                }

                if (new FileInfo(target).Length != info.Size)
                    throw new InvalidDataException("Downloaded size does not match the release asset.");

                if (info.Sha256 is { } expected)
                {
                    await using var fs = File.OpenRead(target);
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, cts.Token));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("SHA-256 mismatch.");
                }
                return target;
            }
            catch
            {
                TryDelete(target);
                throw;
            }
        }

        /// <summary>
        /// Swaps the files. A running exe can't be overwritten but can be renamed, so:
        /// current → *.old, *.new → current. Rolls back if the second move fails. Returns the exe path to start.
        /// </summary>
        public static string Apply(string newFile)
        {
            var exe = ExePath!;
            var old = exe + ".old";
            TryDelete(old);
            File.Move(exe, old);
            try
            {
                File.Move(newFile, exe);
            }
            catch
            {
                File.Move(old, exe); // put the working exe back
                throw;
            }
            return exe;
        }

        /// <summary>Removes leftovers of a previous update (the old exe may still be exiting — retry a bit).</summary>
        public static async Task CleanupAsync()
        {
            if (ExePath is not { } exe) return;
            TryDelete(exe + ".new");
            TryDelete(exe + ".bad");
            for (int i = 0; i < 10 && File.Exists(exe + ".old"); i++)
            {
                TryDelete(exe + ".old");
                await Task.Delay(1000);
            }
        }

        internal static Version? ParseVersion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var s = text.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(['+', '-', ' ']);
            if (cut >= 0) s = s[..cut];
            return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
