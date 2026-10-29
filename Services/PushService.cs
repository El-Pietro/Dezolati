using System.Security.Cryptography;
using System.Text;

namespace Dezolati.Services
{
    /// <summary>
    /// Oznámení od kámošů (Firebase Cloud Messaging) a „kde sedím“.
    ///  - token telefonu se uloží do PushTokens/{sha256(token)} (číst ho smí jen server = Cloud Functions),
    ///  - posílá výhradně server (functions/index.js): žádost o přátelství, přijetí, „Petr sedí v U Peťky“,
    ///  - „kde sedím“: při ručním zápisu se k pití přidá nejbližší hospoda (do 60 m), když je zapnuté
    ///    poznávání hospody; kámošům se hlásí jen se zapnutým „Hlásit“.
    /// Zatím jen Android.
    /// </summary>
    public static class PushService
    {
        private const string KeyToken = "push_token";
        private const string KeyTokenUid = "push_token_uid";
        private const string KeySharePub = "share_pub";
        private const string KeyDetectPub = "detect_pub";
        private const double PubRadiusMeters = 60;

        private static bool _initDone;

        private const string KeyShareLevel = "share_level";

        /// <summary>
        /// Hlášení kámošům (uloženo i v profilu, tady kvůli zápisu bez signálu):
        /// 0 nehlásit, 1 hlásit že piju (oznámení „X si dala pivo“), 2 i kde (hospoda, „Teď sedí“, mapa).
        /// Výchozí 2 (vše zapnuté, kdo nechce, vypne).
        /// </summary>
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

        /// <summary>Hlásit kámošům i hospodu (úroveň 2).</summary>
        public static bool SharePub => ShareLevel == 2;

        /// <summary>Hlásit kámošům, že piju (úroveň 1 a víc).</summary>
        public static bool ShareDrink => ShareLevel >= 1;

        /// <summary>
        /// Poznávat hospodu podle polohy (jen pro mě: lístek, statistiky). Jen v telefonu.
        /// Výchozí zapnuto.
        /// </summary>
        public static bool DetectPub
        {
            get => Preferences.Get(KeyDetectPub, true);
            set => Preferences.Set(KeyDetectPub, value);
        }

        // ---------------- hospoda posledního zápisu (jen v telefonu, pro lístek) ----------------

        private record LastPubInfo(DateTime Day, string? PubId, string? PubName, string? Place = null, double? Lat = null, double? Lon = null);

        private static string LastPubKey(string uid) => $"last_pub_{uid}";

        /// <summary>Po zápisu: kde se pilo (hospoda, doma, jinde; null = nepoznáno).</summary>
        public static void RememberPlace(string uid, DateTime day, PlaceInfo place)
        {
            try
            {
                Preferences.Set(LastPubKey(uid), System.Text.Json.JsonSerializer.Serialize(
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
                var info = System.Text.Json.JsonSerializer.Deserialize<LastPubInfo>(json);
                return info != null && info.Day.Date == day.Date ? info : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Kde byl poslední zápis dne (pro lístek: „📍 U Peťky“ / „📍 doma“).</summary>
        public static string? LastPubName(string uid, DateTime day)
        {
            if (!DetectPub) return null;
            var info = LastInfo(uid, day);
            return info?.Place == Place.Home ? "doma" : info?.PubName;
        }

        /// <summary>Místo posledního zápisu dne (zápis z připomínky ho převezme – bez polohy).</summary>
        public static PlaceInfo LastPlace(string uid, DateTime day)
        {
            var info = LastInfo(uid, day);
            return info == null ? PlaceInfo.Unknown : new PlaceInfo(info.Place, info.PubId, info.PubName, info.Lat, info.Lon);
        }

        /// <summary>Po přihlášení / otevření lístku: povolení, kanál, token. Jednou za spuštění.</summary>
        public static async Task InitAsync()
        {
#if ANDROID
            if (_initDone || AuthSession.Uid is not string uid) return;
            _initDone = true;
            try
            {
                Dezolati.Droid.PushAndroid.EnsureChannel();
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                {
                    var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
                    if (status != PermissionStatus.Granted)
                        await Permissions.RequestAsync<Permissions.PostNotifications>();
                }
                var token = await Dezolati.Droid.PushAndroid.GetTokenAsync();
                await SaveTokenAsync(uid, token);
            }
            catch (Exception ex)
            {
                _initDone = false; // zkusí se příště
                System.Diagnostics.Debug.WriteLine($"Push init: {ex.Message}");
            }
#else
            await Task.CompletedTask;
#endif
        }

        /// <summary>FCM vydal nový token (volá služba na Androidu).</summary>
        public static async Task OnNewTokenAsync(string token)
        {
            if (AuthSession.Uid is string uid)
            {
                try { await SaveTokenAsync(uid, token); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Push token: {ex.Message}"); }
            }
        }

        private static string DocId(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        private static async Task SaveTokenAsync(string uid, string? token)
        {
            if (string.IsNullOrEmpty(token)) return;
            // stejný token pro stejného uživatele už je uložený → žádný zápis
            if (Preferences.Get(KeyToken, "") == token && Preferences.Get(KeyTokenUid, "") == uid) return;

            await FirestoreRest.PatchAsync($"PushTokens/{DocId(token)}", new()
            {
                ["Uid"] = uid,
                ["Token"] = token,
                ["Platform"] = DeviceInfo.Platform.ToString(),
                ["UpdatedAt"] = DateTime.UtcNow
            });
            Preferences.Set(KeyToken, token);
            Preferences.Set(KeyTokenUid, uid);
        }

        /// <summary>Při odhlášení: tenhle telefon už nemá dostávat oznámení pro tenhle účet.</summary>
        public static async Task UnregisterAsync()
        {
            var token = Preferences.Get(KeyToken, "");
            _initDone = false;
            if (token.Length == 0) return;
            try { await FirestoreRest.DeleteAsync($"PushTokens/{DocId(token)}"); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Push unregister: {ex.Message}"); }
            Preferences.Remove(KeyToken);
            Preferences.Remove(KeyTokenUid);
        }

        /// <summary>
        /// Kde teď jsem: doma (do 80 m od domova, poloha domova jen v telefonu), v hospodě (do 60 m),
        /// jinde, nebo nevím (poznávání vypnuté / bez polohy). Nezdržuje zápis víc než pár sekund.
        /// </summary>
        public static async Task<PlaceInfo> PlaceHereAsync()
        {
            if (!DetectPub && !SharePub) return PlaceInfo.Unknown;
            try
            {
                if (await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
                    return PlaceInfo.Unknown;

                var loc = await Geolocation.GetLastKnownLocationAsync();
                if (loc == null || DateTimeOffset.Now - loc.Timestamp > TimeSpan.FromMinutes(10))
                    loc = await Geolocation.GetLocationAsync(
                        new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(4))) ?? loc;
                if (loc == null) return PlaceInfo.Unknown;

                LastLocation.Remember(loc.Latitude, loc.Longitude);
                // doma / venku: poloha se hodí kámošům na mapu (jen při hlášení „I kde“)
                if (HomePlace.IsNear(loc.Latitude, loc.Longitude))
                    return new PlaceInfo(Place.Home, null, null, loc.Latitude, loc.Longitude);

                // hospody v okolí: chybí-li čtverec mapy, dotáhnout (max. pár sekund, bez signálu nic)
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await PubCatalog.EnsureCellsAsync(PubCatalog.CellsNear(loc.Latitude, loc.Longitude), cts.Token);
                }
                catch { }
                await PubService.LoadOverlayAsync();
                var pub = PubService.Nearest(loc.Latitude, loc.Longitude, PubRadiusMeters);
                return pub == null
                    ? new PlaceInfo(Place.Other, null, null, loc.Latitude, loc.Longitude)
                    : new PlaceInfo(Place.Pub, pub.Id, PubService.NameOf(pub), pub.Lat, pub.Lon);
            }
            catch
            {
                return PlaceInfo.Unknown;
            }
        }
    }
}
