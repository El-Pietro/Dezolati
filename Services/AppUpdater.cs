using System.Text.Json;

namespace Dezolati.Services
{
    /// <summary>Nová verze z GitHub Releases.</summary>
    public sealed record AppRelease(string Tag, Version Version, string Notes, string ApkUrl, long ApkSize);

    /// <summary>
    /// Aktualizace mimo Google Play: poslední Release na GitHubu → stáhnout APK → instalátor Androidu.
    /// Nic nečte z Firestore. GitHub bez přihlášení dovolí 60 dotazů za hodinu na IP, kontrolujeme max. jednou za 30 min.
    ///
    /// Vydání nové verze:
    ///  1. zvýšit ApplicationDisplayVersion (např. 3.1) a ApplicationVersion v Dezolati.csproj,
    ///  2. publish (podepsat STEJNÝM klíčem),
    ///  3. na GitHubu Releases → New release, tag "v3.1", do popisu co je nového,
    ///     přiložit cz.elpietro.dezolati-Signed.apk → Publish release.
    /// </summary>
    public static class AppUpdater
    {
        public const string Repo = "El-Pietro/Dezolati";
        public static string ReleasesPage => $"https://github.com/{Repo}/releases";

        private const string KeyLastCheck = "upd_last_check";
        private const string KeySnoozedTag = "upd_snoozed_tag";
        private const string KeySnoozedAt = "upd_snoozed_at";
        private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan SnoozeFor = TimeSpan.FromDays(1);

        private static readonly HttpClient Http = CreateHttp();

        /// <summary>Stejný klient (s User-Agentem) pro seznam novinek.</summary>
        internal static HttpClient GitHubHttp => Http;

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Dezolati-app"); // GitHub API bez User-Agenta odmítá
            return http;
        }

        /// <summary>Aktuální verze appky (ApplicationDisplayVersion, např. 3.0).</summary>
        public static Version CurrentVersion => ParseVersion(AppInfo.Current.VersionString) ?? new Version(0, 0);

        public static string CurrentVersionText => $"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";

        /// <summary>Aktualizace se umí nainstalovat jen na Androidu.</summary>
        public static bool IsSupported => DeviceInfo.Platform == DevicePlatform.Android;

        /// <summary>
        /// Je venku novější verze? null = není / nejde zjistit.
        /// manual = ručně z Nastavení: ignoruje odklad i interval a chyby vyhazuje.
        /// </summary>
        public static async Task<AppRelease?> CheckAsync(bool manual = false)
        {
            if (!IsSupported) return null;

            if (!manual)
            {
                var last = Preferences.Get(KeyLastCheck, 0L);
                if (last > 0 && DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc) < CheckEvery) return null;
            }

            try
            {
                var release = await FetchLatestAsync();
                Preferences.Set(KeyLastCheck, DateTime.UtcNow.Ticks);
                if (release == null || release.Version <= CurrentVersion) return null;

                if (!manual && IsSnoozed(release.Tag)) return null;
                return release;
            }
            catch when (!manual)
            {
                return null; // automatická kontrola nikoho neotravuje chybou
            }
        }

        /// <summary>„Později“ = s touhle verzí dát den pokoj.</summary>
        public static void Snooze(AppRelease release)
        {
            Preferences.Set(KeySnoozedTag, release.Tag);
            Preferences.Set(KeySnoozedAt, DateTime.UtcNow.Ticks);
        }

        private static bool IsSnoozed(string tag)
        {
            if (Preferences.Get(KeySnoozedTag, "") != tag) return false;
            var at = Preferences.Get(KeySnoozedAt, 0L);
            return at > 0 && DateTime.UtcNow - new DateTime(at, DateTimeKind.Utc) < SnoozeFor;
        }

        private static async Task<AppRelease?> FetchLatestAsync()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null; // žádný release (nebo soukromé repo)
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var version = ParseVersion(tag);
            if (version == null) return null;

            // APK v přílohách: přednost má podepsaný
            string? url = null;
            long size = 0;
            var bestScore = -1;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) continue;
                var score = name.Contains("Signed", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                if (score <= bestScore) continue;
                bestScore = score;
                url = asset.GetProperty("browser_download_url").GetString();
                size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            }
            if (url == null) return null;

            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new AppRelease(tag, version, notes.Trim(), url, size);
        }

        /// <summary>„v3.1“, „3.1“, „3.1.0“ → Version(3,1). Neplatné → null.</summary>
        public static Version? ParseVersion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim().TrimStart('v', 'V');
            var end = 0;
            while (end < t.Length && (char.IsDigit(t[end]) || t[end] == '.')) end++;
            t = t[..end].Trim('.');
            if (t.Length == 0) return null;
            if (!t.Contains('.')) t += ".0";
            // 3.0 a 3.0.0 musí být stejné (Version jinak bere chybějící část jako -1)
            return Version.TryParse(t, out var v)
                ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0))
                : null;
        }

        /// <summary>Stáhne APK do mezipaměti. progress = 0..1 (když je známá velikost).</summary>
        public static async Task<string> DownloadAsync(AppRelease release, IProgress<double>? progress = null)
        {
            var dir = Path.Combine(FileSystem.CacheDirectory, "updates");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"dezolati-{release.Version}.apk");
            foreach (var old in Directory.GetFiles(dir, "*.apk"))
                if (old != path) try { File.Delete(old); } catch { }

            // Na mobilních datech se spojení umí utrhnout (na Androidu jako ObjectDisposed na Java.IO streamu).
            // Proto několik pokusů a navázání tam, kde se skončilo (HTTP Range), ne stahování od nuly.
            const int attempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // pokud by telefon mezitím zhasl, Android rád utne síť – po dobu stahování nechat displej svítit
                    MainThread.BeginInvokeOnMainThread(() => { try { DeviceDisplay.Current.KeepScreenOn = true; } catch { } });
                    await DownloadPartAsync(release, path, progress);
                    return path;
                }
                catch (Exception ex) when (attempt < attempts && IsTransient(ex))
                {
                    System.Diagnostics.Debug.WriteLine($"Stahování přerušeno ({attempt}/{attempts}): {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    throw new IOException("Stahování se několikrát přerušilo – asi slabý signál. Zkus to znovu na Wi-Fi.", ex);
                }
                finally
                {
                    MainThread.BeginInvokeOnMainThread(() => { try { DeviceDisplay.Current.KeepScreenOn = false; } catch { } });
                }
            }
        }

        private static bool IsTransient(Exception ex) =>
            ex is ObjectDisposedException or IOException or TimeoutException || Net.IsNetworkError(ex);

        /// <summary>Stáhne (nebo dotáhne) APK do souboru. Hotový soubor se pozná podle velikosti.</summary>
        private static async Task DownloadPartAsync(AppRelease release, string path, IProgress<double>? progress)
        {
            long have = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (release.ApkSize > 0 && have == release.ApkSize) { progress?.Report(1); return; }
            if (release.ApkSize > 0 && have > release.ApkSize) { File.Delete(path); have = 0; }

            using var req = new HttpRequestMessage(HttpMethod.Get, release.ApkUrl);
            if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (have > 0 && resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // soubor už je celý (nebo nesedí) → začít znovu
                File.Delete(path);
                throw new IOException("Neplatný rozsah, stahuju znovu.");
            }
            resp.EnsureSuccessStatusCode();

            // server rozsah nepodporuje (200 místo 206) → od začátku
            var resumed = have > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!resumed) have = 0;
            var total = release.ApkSize > 0 ? release.ApkSize : have + (resp.Content.Headers.ContentLength ?? 0);

            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = new FileStream(path, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write);
            var buffer = new byte[81920];
            long done = have;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
            await dst.FlushAsync();

            if (release.ApkSize > 0 && done < release.ApkSize)
                throw new IOException("Stahování skončilo předčasně.");
        }

        /// <summary>
        /// Může appka spustit instalaci? (Android 8+: „Instalace neznámých aplikací“ pro Dezoláti.)
        /// Když ne, otevře rovnou příslušné nastavení a vrátí false.
        /// </summary>
        public static bool EnsureInstallPermission()
        {
#if ANDROID
            var ctx = Android.App.Application.Context;
            if (OperatingSystem.IsAndroidVersionAtLeast(26) && ctx.PackageManager?.CanRequestPackageInstalls() == false)
            {
                var intent = new Android.Content.Intent(
                    Android.Provider.Settings.ActionManageUnknownAppSources,
                    Android.Net.Uri.Parse("package:" + ctx.PackageName));
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                ctx.StartActivity(intent);
                return false;
            }
#endif
            return true;
        }

        /// <summary>Otevře instalátor Androidu se staženým APK.</summary>
        public static Task InstallAsync(string apkPath) =>
            Launcher.Default.OpenAsync(new OpenFileRequest("Aktualizace Dezoláti",
                new ReadOnlyFile(apkPath, "application/vnd.android.package-archive")));
    }
}
