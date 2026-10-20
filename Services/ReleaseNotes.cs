using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dezolati.Services
{
    /// <summary>Jedna vydaná verze pro „Co je nového“.</summary>
    public sealed record ReleaseNote(string Tag, Version? Version, DateTime? PublishedAt, string Text);

    /// <summary>
    /// Novinky z popisů vydání na GitHubu (Releases). Nic nečte z Firestore.
    /// Poslední stažený seznam se drží v telefonu, takže jde číst i bez signálu.
    /// </summary>
    public static class ReleaseNotes
    {
        private const string KeyCache = "release_notes_cache";
        private const string KeySeenVersion = "release_notes_seen";

        /// <summary>Seznam verzí, nejnovější první. Bez signálu vrátí poslední uložený (fromCache = true).</summary>
        public static async Task<(List<ReleaseNote> Notes, bool FromCache)> GetAsync()
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{AppUpdater.Repo}/releases?per_page=30");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await AppUpdater.GitHubHttp.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();
                var notes = Parse(json);
                Preferences.Set(KeyCache, json);
                return (notes, false);
            }
            catch
            {
                var cached = Preferences.Get(KeyCache, null as string);
                if (string.IsNullOrEmpty(cached)) throw;
                return (Parse(cached), true);
            }
        }

        private static List<ReleaseNote> Parse(string json)
        {
            var result = new List<ReleaseNote>();
            using var doc = JsonDocument.Parse(json);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                if (r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;

                var tag = r.GetProperty("tag_name").GetString() ?? "";
                DateTime? published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(p.GetString(), out var dt) ? dt.ToLocalTime() : null;
                var body = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                result.Add(new ReleaseNote(tag, AppUpdater.ParseVersion(tag), published, Clean(body)));
            }
            return result
                .OrderByDescending(n => n.Version ?? new Version(0, 0))
                .ThenByDescending(n => n.PublishedAt)
                .ToList();
        }

        /// <summary>Markdown z GitHubu → čistý text na lístek (nadpisy, tučné, odkazy, odrážky).</summary>
        public static string Clean(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return "";
            var sb = new StringBuilder();
            foreach (var raw in markdown.Replace("\r", "").Split('\n'))
            {
                var line = raw.TrimEnd();
                line = Regex.Replace(line, @"^\s{0,3}#{1,6}\s*", "");               // # nadpis
                line = Regex.Replace(line, @"^(\s*)[-*+]\s+", "$1• ");               // - odrážka
                line = Regex.Replace(line, @"!\[[^\]]*\]\([^)]*\)", "");              // obrázky pryč
                line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");           // [text](url) → text
                line = Regex.Replace(line, @"(\*\*|__)(.+?)\1", "$2");                // **tučně**
                line = Regex.Replace(line, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?!\w)", "$1"); // *kurzíva*
                line = line.Replace("`", "");
                if (Regex.IsMatch(line, @"^\s*([-*_]\s*){3,}$")) line = "";          // --- čára
                sb.AppendLine(line);
            }
            // víc prázdných řádků za sebou → jeden
            return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
        }

        /// <summary>
        /// Po aktualizaci jednou ukázat, co je nového v téhle verzi.
        /// Vrací novinky k ukázání (null = nic / už viděno / nejde stáhnout).
        /// </summary>
        public static async Task<ReleaseNote?> PendingForCurrentAsync()
        {
            var current = AppUpdater.CurrentVersion;
            var seen = AppUpdater.ParseVersion(Preferences.Get(KeySeenVersion, ""));
            if (seen != null && seen >= current) return null;

            List<ReleaseNote> notes;
            try { (notes, _) = await GetAsync(); }
            catch { return null; } // bez signálu → zkusí se příště

            var mine = notes.FirstOrDefault(n => n.Version == current);
            MarkSeen();   // jednou stačí, i když verze na GitHubu popis nemá
            return mine is { Text.Length: > 0 } ? mine : null;
        }

        public static void MarkSeen() =>
            Preferences.Set(KeySeenVersion, AppInfo.Current.VersionString);
    }
}
