using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>
    /// Profily hospod ve Firestore. Čte se jen při otevření konkrétní hospody (1 čtení),
    /// historie až na požádání (≤ 10 čtení).
    /// </summary>
    public static class PubService
    {
        public const int MaxLength = 300;
        public const int MaxNameLength = 80;

        // opravené názvy hospod (id → název); čtení = počet přejmenovaných hospod, jednou za spuštění
        private static Dictionary<string, string>? _renamed;

        /// <summary>Název, jak ho opravili uživatelé (jinak z OpenStreetMap).</summary>
        public static string NameOf(PubInfo pub) =>
            _renamed != null && _renamed.TryGetValue(pub.Id, out var n) ? n : pub.Name;

        /// <summary>Načte opravené názvy (jen dokumenty s Renamed = true). Bez signálu tiše nic.</summary>
        public static async Task LoadRenamedAsync(bool force = false)
        {
            if (_renamed != null && !force) return;
            try
            {
                var docs = await FirestoreRest.QueryAsync("Pubs", new[] { Filter.Eq("Renamed", true) });
                _renamed = docs
                    .Where(d => !string.IsNullOrWhiteSpace(d.Str("DisplayName")))
                    .ToDictionary(d => d.Id, d => d.Str("DisplayName")!);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Opravené názvy hospod: {ex.Message}");
                _renamed ??= new Dictionary<string, string>();
            }
        }

        public static async Task<PubProfile?> GetProfileAsync(string pubId)
        {
            var d = await FirestoreRest.GetAsync($"Pubs/{pubId}");
            if (d == null) return null;
            return new PubProfile
            {
                DisplayName = d.Str("DisplayName"),
                Taps = d.Str("Taps"),
                Prices = d.Str("Prices"),
                Hours = d.Str("Hours"),
                Note = d.Str("Note"),
                UpdatedAt = d.Time("UpdatedAt")?.ToLocalTime(),
                UpdatedByNick = d.Str("UpdatedByNick")
            };
        }

        /// <summary>
        /// Uloží profil a zároveň záznam do historie – jednou atomickou dávkou
        /// (pravidla ve Firestore bez záznamu v historii úpravu nepustí).
        /// </summary>
        public static async Task SaveAsync(PubInfo pub, PubProfile profile)
        {
            var uid = AuthSession.Uid ?? throw new InvalidOperationException("Nejsi přihlášený.");
            await UserSession.EnsureReadyAsync();
            var nick = UserSession.Profile?.Nickname ?? "?";
            var now = DateTime.UtcNow;
            var editId = Guid.NewGuid().ToString("N");

            static string Clean(string? s, int max = MaxLength)
            {
                var t = (s ?? "").Trim();
                return t.Length > max ? t[..max] : t;
            }

            // název: prázdný nebo stejný jako v OSM = žádná oprava
            var name = Clean(profile.DisplayName, MaxNameLength);
            var renamed = name.Length > 0 && name != pub.Name;

            var fields = new Dictionary<string, object?>
            {
                ["DisplayName"] = renamed ? name : "",
                ["Taps"] = Clean(profile.Taps),
                ["Prices"] = Clean(profile.Prices),
                ["Hours"] = Clean(profile.Hours),
                ["Note"] = Clean(profile.Note)
            };

            var edit = new Dictionary<string, object?>(fields)
            {
                ["PubId"] = pub.Id,
                ["By"] = uid,
                ["ByNick"] = nick,
                ["At"] = now
            };

            var doc = new Dictionary<string, object?>(fields)
            {
                ["PubId"] = pub.Id,
                ["Name"] = pub.Name,
                ["Renamed"] = renamed,
                ["UpdatedAt"] = now,
                ["UpdatedBy"] = uid,
                ["UpdatedByNick"] = nick,
                ["EditId"] = editId
            };

            await FirestoreRest.CommitAsync(new[]
            {
                FirestoreRest.SetWrite($"PubEdits/{editId}", edit, mustNotExist: true),
                FirestoreRest.SetWrite($"Pubs/{pub.Id}", doc)
            });

            // opravený název hned i v mapě
            _renamed ??= new Dictionary<string, string>();
            if (renamed) _renamed[pub.Id] = name;
            else _renamed.Remove(pub.Id);
        }

        /// <summary>Posledních pár úprav (potřebuje index PubEdits: PubId↑ At↓).</summary>
        public static async Task<List<PubEdit>> GetHistoryAsync(string pubId, int limit = 10)
        {
            var docs = await FirestoreRest.QueryAsync("PubEdits",
                new[] { Filter.Eq("PubId", pubId) }, orderBy: "At", descending: true, limit: limit);
            return docs.Select(d => new PubEdit
            {
                At = d.Time("At")?.ToLocalTime(),
                ByNick = d.Str("ByNick"),
                DisplayName = d.Str("DisplayName"),
                Taps = d.Str("Taps"),
                Prices = d.Str("Prices"),
                Hours = d.Str("Hours"),
                Note = d.Str("Note")
            }).ToList();
        }
    }
}
