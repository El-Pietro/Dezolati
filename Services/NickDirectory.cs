using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>Jeden člověk v adresáři: přezdívka a uid jeho kámošů.</summary>
    public sealed record DirectoryEntry(string Uid, string Nick, List<string> Friends, string? Version = null);

    /// <summary>Návrh „Možná znáš“: kdo a s kolika mými kámoši se kamarádí.</summary>
    public sealed record FriendSuggestion(string Uid, string Nick, int Mutual);

    /// <summary>
    /// Adresář všech uživatelů v JEDNOM dokumentu Directory/users: { uid: { N: přezdívka, F: [uid kámošů] } }.
    /// Stáhne se (1 čtení) až při hledání / „Možná znáš“ a drží se v telefonu max. 8 h → nejvýš 3 čtení denně.
    /// Hledání i návrhy se pak počítají v telefonu. Každý zapisuje jen svůj řádek (hlídají pravidla).
    /// </summary>
    public static class NickDirectory
    {
        private const string DocPath = "Directory/users";
        private const string FileName = "adresar.json";
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(8);

        private static Dictionary<string, DirectoryEntry>? _entries;
        private static DateTime _loadedAt = DateTime.MinValue;
        private static Task<Dictionary<string, DirectoryEntry>>? _loading;

        private static string FilePath => Path.Combine(FileSystem.AppDataDirectory, FileName);

        private sealed record CacheDto(DateTime At, List<DirectoryEntry> Entries);

        /// <summary>Adresář – z telefonu, když je mladší než 8 h, jinak 1 čtení. Bez signálu to, co je.</summary>
        public static Task<Dictionary<string, DirectoryEntry>> GetAsync(bool force = false)
        {
            if (!force && _entries != null && DateTime.UtcNow - _loadedAt < MaxAge)
                return Task.FromResult(_entries);
            return _loading is { IsCompleted: false } running ? running : (_loading = LoadAsync(force));
        }

        private static async Task<Dictionary<string, DirectoryEntry>> LoadAsync(bool force)
        {
            // 1) telefon
            if (_entries == null)
            {
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var dto = JsonSerializer.Deserialize<CacheDto>(await File.ReadAllTextAsync(FilePath));
                        if (dto != null)
                        {
                            _entries = dto.Entries.ToDictionary(e => e.Uid);
                            _loadedAt = dto.At;
                        }
                    }
                }
                catch { }
            }
            if (!force && _entries != null && DateTime.UtcNow - _loadedAt < MaxAge) return _entries;
            if (!Net.IsOnline) return _entries ?? new();

            // 2) server – 1 čtení
            try
            {
                var doc = await FirestoreRest.GetAsync(DocPath);
                var result = new Dictionary<string, DirectoryEntry>();
                if (doc != null)
                {
                    foreach (var (uid, value) in doc.Fields)
                    {
                        var f = value?["mapValue"]?["fields"];
                        var nick = f?["N"]?["stringValue"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(nick)) continue;
                        var friends = (f?["F"]?["arrayValue"]?["values"] as JsonArray)?
                            .Select(v => v?["stringValue"]?.GetValue<string>())
                            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList() ?? new();
                        result[uid] = new DirectoryEntry(uid, nick, friends, f?["V"]?["stringValue"]?.GetValue<string>());
                    }
                }
                _entries = result;
                _loadedAt = DateTime.UtcNow;
                try { await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(new CacheDto(_loadedAt, result.Values.ToList()))); }
                catch { }
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Adresář: {ex.Message}");
                return _entries ?? new();
            }
        }

        /// <summary>Hledání v telefonu: kus přezdívky, bez ohledu na velká písmena a háčky.</summary>
        public static List<DirectoryEntry> Search(Dictionary<string, DirectoryEntry> dir, string text, int max = 6)
        {
            text = text.Trim();
            if (text.Length < 2) return new();
            var q = Fold(text);
            return dir.Values
                .Where(e => e.Uid != AuthSession.Uid && Fold(e.Nick).Contains(q, StringComparison.Ordinal))
                .OrderBy(e => Fold(e.Nick).StartsWith(q, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(e => e.Nick, StringComparer.CurrentCultureIgnoreCase)
                .Take(max)
                .ToList();
        }

        /// <summary>Malá písmena bez háčků a čárek („Ondrášek“ → „ondrasek“).</summary>
        private static string Fold(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s.Normalize(System.Text.NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        /// <summary>
        /// „Možná znáš“: všichni uživatelé, které ještě nemám v kámoších (přátelství se stejně musí schválit).
        /// Nahoře ti s nejvíc společnými kámoši, pak podle abecedy.
        /// </summary>
        public static List<FriendSuggestion> Suggest(Dictionary<string, DirectoryEntry> dir, IEnumerable<string> myFriends, int max = 100)
        {
            var me = AuthSession.Uid;
            var mine = myFriends.ToHashSet();
            var counts = new Dictionary<string, int>();
            foreach (var f in mine)
            {
                if (!dir.TryGetValue(f, out var entry)) continue;
                foreach (var ff in entry.Friends)
                {
                    if (ff == me || mine.Contains(ff)) continue;
                    counts[ff] = counts.GetValueOrDefault(ff) + 1;
                }
            }
            return dir.Values
                .Where(e => e.Uid != me && !mine.Contains(e.Uid))
                .Select(e => new FriendSuggestion(e.Uid, e.Nick, counts.GetValueOrDefault(e.Uid)))
                .OrderByDescending(s => s.Mutual)
                .ThenBy(s => s.Nick, StringComparer.CurrentCultureIgnoreCase)
                .Take(max)
                .ToList();
        }

        // ---------------- vlastní řádek ----------------

        private static string SigKey(string uid) => $"dir_sig_{uid}";

        /// <summary>
        /// Přezdívka + verze appky (start appky / uložení přezdívky). Zapíše se jen při změně –
        /// verze tedy jednou po každé aktualizaci (správce pak ve Spotřebě vidí, kdo už ji má).
        /// </summary>
        public static async Task PublishNickAsync(string? nick)
        {
            if (AuthSession.Uid is not string uid || string.IsNullOrWhiteSpace(nick) || !Net.IsOnline) return;
            nick = nick.Trim();
            var version = AppInfo.Current.VersionString;
            var key = $"dir_nick_{uid}";
            if (Preferences.Get(key, "") == nick + "|" + version) return;
            try
            {
                var seg = DrinkQueue.FieldSegment(uid);
                await FirestoreRest.CommitAsync(new[]
                {
                    FirestoreRest.SetWrite(DocPath,
                        new() { [uid] = new Dictionary<string, object?> { ["N"] = nick, ["V"] = version } },
                        mask: new[] { $"{seg}.N", $"{seg}.V" })
                });
                Preferences.Set(key, nick + "|" + version);
                if (_entries != null && _entries.TryGetValue(uid, out var e)) _entries[uid] = e with { Nick = nick, Version = version };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Adresář (přezdívka): {ex.Message}");
            }
        }

        /// <summary>Přezdívka + seznam kámošů (stránka Kámoši). Zapíše se jen, když se něco změnilo.</summary>
        public static async Task PublishFriendsAsync(string? nick, IEnumerable<UserModel> friends)
        {
            if (AuthSession.Uid is not string uid || string.IsNullOrWhiteSpace(nick) || !Net.IsOnline) return;
            nick = nick.Trim();
            var ids = friends.Select(f => f.UserId).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            var sig = nick + "|" + string.Join(",", ids);
            if (Preferences.Get(SigKey(uid), "") == sig) return;
            try
            {
                var seg = DrinkQueue.FieldSegment(uid);
                await FirestoreRest.CommitAsync(new[]
                {
                    FirestoreRest.SetWrite(DocPath,
                        new() { [uid] = new Dictionary<string, object?> { ["N"] = nick, ["F"] = ids } },
                        mask: new[] { $"{seg}.N", $"{seg}.F" })   // V (verze) nechat
                });
                Preferences.Set(SigKey(uid), sig);
                if (_entries != null) _entries[uid] = new DirectoryEntry(uid, nick, ids, _entries.TryGetValue(uid, out var old) ? old.Version : null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Adresář (kámoši): {ex.Message}");
            }
        }
    }
}
