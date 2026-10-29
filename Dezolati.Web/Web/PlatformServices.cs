using System.Text.Json;
using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>
    /// Webová verze PushService (Services/PushService.cs v appce): nastavení hlášení kámošům a „kde sedím“.
    /// Oznámení (fáze 2) a poloha / hospoda podle GPS (fáze 3) zatím na webu nejsou → zápis jde bez místa.
    /// Klíče v úložišti jsou stejné jako v appce.
    /// </summary>
    public static class PushService
    {
        private const string KeySharePub = "share_pub";
        private const string KeyDetectPub = "detect_pub";
        private const string KeyShareLevel = "share_level";

        public static int ShareLevel
        {
            get => Preferences.ContainsKey(KeyShareLevel)
                ? Math.Clamp(Preferences.Get(KeyShareLevel, 1), 0, 2)
                : 2;
            set
            {
                Preferences.Set(KeyShareLevel, Math.Clamp(value, 0, 2));
                Preferences.Set(KeySharePub, value == 2);
            }
        }

        public static bool SharePub => ShareLevel == 2;
        public static bool ShareDrink => ShareLevel >= 1;

        public static bool DetectPub
        {
            get => Preferences.Get(KeyDetectPub, true);
            set => Preferences.Set(KeyDetectPub, value);
        }

        private record LastPubInfo(DateTime Day, string? PubId, string? PubName, string? Place = null, double? Lat = null, double? Lon = null);

        private static string LastPubKey(string uid) => $"last_pub_{uid}";

        public static void RememberPlace(string uid, DateTime day, PlaceInfo place)
        {
            try
            {
                Preferences.Set(LastPubKey(uid), JsonSerializer.Serialize(
                    new LastPubInfo(day.Date, place.PubId, place.PubName, place.Place, place.Lat, place.Lon)));
            }
            catch { }
        }

        private static LastPubInfo? LastInfo(string uid, DateTime day)
        {
            try
            {
                var json = Preferences.Get(LastPubKey(uid), null as string);
                if (string.IsNullOrEmpty(json)) return null;
                var info = JsonSerializer.Deserialize<LastPubInfo>(json);
                return info != null && info.Day.Date == day.Date ? info : null;
            }
            catch
            {
                return null;
            }
        }

        public static string? LastPubName(string uid, DateTime day)
        {
            if (!DetectPub) return null;
            var info = LastInfo(uid, day);
            return info?.Place == Place.Home ? "doma" : info?.PubName;
        }

        public static PlaceInfo LastPlace(string uid, DateTime day)
        {
            var info = LastInfo(uid, day);
            return info == null ? PlaceInfo.Unknown : new PlaceInfo(info.Place, info.PubId, info.PubName, info.Lat, info.Lon);
        }

        // ---------------- web push (token prohlížeče do PushTokens, posílá server) ----------------

        private const string KeyToken = "push_token";
        private const string KeyTokenUid = "push_token_uid";

        /// <summary>Na webu se token získává ve Web/WebPush.cs (potřebuje prohlížeč) – tady nic.</summary>
        public static Task InitAsync() => Task.CompletedTask;

        private static string DocId(string token) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        /// <summary>Token prohlížeče → PushTokens/{sha256} (stejně jako telefon). Uloží se jen při změně.</summary>
        public static async Task SaveTokenAsync(string uid, string? token)
        {
            if (string.IsNullOrEmpty(token)) return;
            if (Preferences.Get(KeyToken, "") == token && Preferences.Get(KeyTokenUid, "") == uid) return;

            await FirestoreRest.PatchAsync($"PushTokens/{DocId(token)}", new()
            {
                ["Uid"] = uid,
                ["Token"] = token,
                ["Platform"] = "Web",
                ["UpdatedAt"] = DateTime.UtcNow
            });
            Preferences.Set(KeyToken, token);
            Preferences.Set(KeyTokenUid, uid);
        }

        public static bool HasToken => Preferences.Get(KeyToken, "").Length > 0;

        /// <summary>Při odhlášení / vypnutí: tenhle prohlížeč už nemá dostávat oznámení.</summary>
        public static async Task UnregisterAsync()
        {
            var token = Preferences.Get(KeyToken, "");
            if (token.Length == 0) return;
            try { await FirestoreRest.DeleteAsync($"PushTokens/{DocId(token)}"); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Push unregister: {ex.Message}"); }
            Preferences.Remove(KeyToken);
            Preferences.Remove(KeyTokenUid);
        }

        /// <summary>Na webu zatím bez polohy (přijde s mapou ve fázi 3).</summary>
        public static Task<PlaceInfo> PlaceHereAsync() => Task.FromResult(PlaceInfo.Unknown);
    }

    /// <summary>Připomínky „už X minut nic?“ – jen Android (lokální notifikace). Na webu nic.</summary>
    public static class Reminders
    {
        public static int Minutes { get => 0; set { } }
        public static Task OnDrinkRecordedAsync(DateTime day, int pivo, int vino, int panaky, bool fromReminder) => Task.CompletedTask;
        public static void OnCorrection(DateTime day, int pivo, int vino, int panaky) { }
        public static void Adjust(DateTime day, int pivo, int vino, int panaky) { }
        public static void Cancel() { }
    }

    /// <summary>Kousek PubService (profil hospody kvůli cenám). Mapa a katalog přijdou ve fázi 3.</summary>
    public static class PubService
    {
        public const int MaxPrice = 5000;

        /// <summary>Jméno hospody (v appce s přejmenováním z profilu; na webu zatím jen z katalogu).</summary>
        public static string NameOf(PubInfo pub) => pub.Name;

        public sealed class PubPrices
        {
            public int? PricePivo { get; init; }
            public int? PriceVino { get; init; }
            public int? PricePanak { get; init; }
        }

        public static async Task<PubPrices?> GetProfileAsync(string pubId)
        {
            var d = await FirestoreRest.GetAsync($"Pubs/{pubId}");
            if (d == null) return null;
            int? P(string f) => d.Has(f) ? (int)d.Int(f) : null;
            return new PubPrices { PricePivo = P("PricePivo"), PriceVino = P("PriceVino"), PricePanak = P("PricePanak") };
        }
    }

    public static class PubCatalog
    {
        public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000;
            double Rad(double deg) => deg * Math.PI / 180;
            var dLat = Rad(lat2 - lat1);
            var dLon = Rad(lon2 - lon1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }
    }
}
