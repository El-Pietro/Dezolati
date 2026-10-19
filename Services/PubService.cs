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

        public static async Task<PubProfile?> GetProfileAsync(string pubId)
        {
            var d = await FirestoreRest.GetAsync($"Pubs/{pubId}");
            if (d == null) return null;
            return new PubProfile
            {
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

            static string Clean(string? s)
            {
                var t = (s ?? "").Trim();
                return t.Length > MaxLength ? t[..MaxLength] : t;
            }

            var fields = new Dictionary<string, object?>
            {
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
                Taps = d.Str("Taps"),
                Prices = d.Str("Prices"),
                Hours = d.Str("Hours"),
                Note = d.Str("Note")
            }).ToList();
        }
    }
}
