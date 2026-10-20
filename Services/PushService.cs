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

        /// <summary>Hlásit kámošům, ve které hospodě sedím (uloženo i v profilu, tady kvůli zápisu bez signálu).</summary>
        public static bool SharePub
        {
            get => Preferences.Get(KeySharePub, false);
            set => Preferences.Set(KeySharePub, value);
        }

        /// <summary>
        /// Poznávat hospodu podle polohy (jen pro mě: lístek, statistiky). Jen v telefonu.
        /// Kdo už měl zapnuté „Hlásit“, má poznávání zapnuté taky.
        /// </summary>
        public static bool DetectPub
        {
            get => Preferences.Get(KeyDetectPub, SharePub);
            set => Preferences.Set(KeyDetectPub, value);
        }

        // ---------------- hospoda posledního zápisu (jen v telefonu, pro lístek) ----------------

        private record LastPubInfo(DateTime Day, string? PubId, string? PubName);

        private static string LastPubKey(string uid) => $"last_pub_{uid}";

        /// <summary>Po zápisu: kde se pilo (null = mimo hospodu / nepoznáno).</summary>
        public static void RememberPub(string uid, DateTime day, string? pubId, string? pubName)
        {
            try
            {
                Preferences.Set(LastPubKey(uid),
                    System.Text.Json.JsonSerializer.Serialize(new LastPubInfo(day.Date, pubId, pubName)));
            }
            catch { }
        }

        /// <summary>Hospoda posledního zápisu v daném hospodském dni (pro lístek).</summary>
        public static string? LastPubName(string uid, DateTime day)
        {
            if (!DetectPub) return null;
            try
            {
                var json = Preferences.Get(LastPubKey(uid), null as string);
                if (string.IsNullOrEmpty(json)) return null;
                var info = System.Text.Json.JsonSerializer.Deserialize<LastPubInfo>(json);
                return info != null && info.Day.Date == day.Date ? info.PubName : null;
            }
            catch
            {
                return null;
            }
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
        /// Ve které hospodě teď sedím? Jen když je poznávání (nebo hlášení) zapnuté, poloha povolená
        /// a seznam hospod už je v telefonu. Nikdy se kvůli tomu neptá a nezdržuje zápis víc než pár sekund.
        /// </summary>
        public static async Task<(string? PubId, string? PubName)> PubHereAsync()
        {
            if (!DetectPub && !SharePub) return (null, null);
            try
            {
                if (await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
                    return (null, null);

                var loc = await Geolocation.GetLastKnownLocationAsync();
                if (loc == null || DateTimeOffset.Now - loc.Timestamp > TimeSpan.FromMinutes(10))
                    loc = await Geolocation.GetLocationAsync(
                        new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(4))) ?? loc;
                if (loc == null) return (null, null);

                if (await PubCatalog.GetCachedAsync() == null) return (null, null);
                await PubService.LoadOverlayAsync();
                var pub = PubService.Nearest(loc.Latitude, loc.Longitude, PubRadiusMeters);
                return pub == null ? (null, null) : (pub.Id, PubService.NameOf(pub));
            }
            catch
            {
                return (null, null);
            }
        }
    }
}
