using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>
    /// Profil přihlášeného uživatele v paměti. Načte se jednou po přihlášení (1 čtení)
    /// a dál se jen udržuje lokálně – lístek, zápis ani nastavení ho znovu nečtou.
    /// Při prvním načtení případně proběhne migrace dat na nové schéma.
    /// </summary>
    public static class UserSession
    {
        private static Task? _ready;
        private static string? _readyFor;
        private static readonly object Gate = new();

        public static UserModel? Profile { get; private set; }

        /// <summary>Čas posledního zápisu (UTC).</summary>
        public static DateTime? LastDrinkAt { get; private set; }

        /// <summary>Ke kterému hospodskému dni se poslední zápis počítal.</summary>
        public static DateTime? LastDrinkDay { get; private set; }

        /// <summary>Ke kterému dni by patřil zápis udělaný teď.</summary>
        public static DateTime CurrentPubDay =>
            PubDay.Resolve(DateTime.Now, LastDrinkAt?.ToLocalTime(), LastDrinkDay);

        public static Task EnsureReadyAsync()
        {
            var uid = AuthSession.Uid ?? throw new InvalidOperationException("Uživatel není přihlášen.");
            lock (Gate)
            {
                if (_ready == null || _readyFor != uid || _ready.IsFaulted || _ready.IsCanceled)
                {
                    _readyFor = uid;
                    _ready = LoadAsync(uid);
                }
                return _ready;
            }
        }

        private static async Task LoadAsync(string uid)
        {
            DrinkQueue.HookConnectivity();

            FsDoc? doc;
            try
            {
                doc = await FirestoreRest.GetAsync($"Users/{uid}");
            }
            catch (Exception ex) when (Net.IsNetworkError(ex) && LocalCache.LoadProfile(uid) is { } cached)
            {
                // bez signálu → poslední známý profil z telefonu
                Profile = cached.Profile;
                LastDrinkAt = cached.LastDrinkAt;
                LastDrinkDay = cached.LastDrinkDay;
                ApplyPendingLastDrink(uid);
                return;
            }

            if (doc == null)
            {
                // nový / starý účet bez profilu → založit
                var nick = AuthSession.DisplayName;
                await FirestoreRest.PatchAsync($"Users/{uid}", new()
                {
                    ["UserId"] = uid,
                    ["Email"] = AuthSession.Email,
                    ["Nickname"] = nick,
                    ["Name"] = nick,
                    ["SchemaVersion"] = Migration.Version
                });
                Profile = new UserModel { Id = uid, UserId = uid, Email = AuthSession.Email, Nickname = nick, Name = nick };
                LastDrinkAt = null;
                LastDrinkDay = null;
                ApplyPendingLastDrink(uid);
                SaveCache(uid);
                return;
            }

            Profile = FirestoreService.ToUser(doc);
            LastDrinkAt = doc.Time("LastDrinkAt");
            LastDrinkDay = PubDay.Parse(doc.Str("LastDrinkDay"));
            ApplyPendingLastDrink(uid);
            SaveCache(uid);

            if (doc.Int("SchemaVersion") < Migration.Version)
            {
                try
                {
                    await Migration.RunAsync(uid, Profile);
                }
                catch (Exception ex)
                {
                    // migrace nesmí zablokovat appku – zkusí se znovu při příštím spuštění
                    System.Diagnostics.Debug.WriteLine($"Migrace selhala: {ex.Message}");
                }
            }
        }

        /// <summary>Lístek se změnil mimo obrazovku lístku (zápis z notifikace, návrat do appky) → překreslit.</summary>
        public static event Action? DrinksChanged;

        public static void RaiseDrinksChanged() => DrinksChanged?.Invoke();

        public static void OnDrinkRecorded(DateTime utcNow, DateTime day)
        {
            LastDrinkAt = utcNow;
            LastDrinkDay = day;
            if (AuthSession.Uid is string uid) SaveCache(uid);
        }

        public static void SetProfile(UserModel profile)
        {
            Profile = profile;
            if (AuthSession.Uid is string uid) SaveCache(uid);
        }

        /// <summary>Neodeslané zápisy ve frontě jsou novější než to, co ví server.</summary>
        private static void ApplyPendingLastDrink(string uid)
        {
            var last = DrinkQueue.Load(uid).Where(p => !p.Correction).OrderBy(p => p.At).LastOrDefault();
            if (last != null && (LastDrinkAt == null || last.At > LastDrinkAt))
            {
                LastDrinkAt = last.At;
                LastDrinkDay = last.Day;
            }
        }

        private static void SaveCache(string uid)
        {
            if (Profile != null)
                LocalCache.SaveProfile(uid, Profile, LastDrinkAt, LastDrinkDay);
        }

        public static void Reset()
        {
            lock (Gate)
            {
                _ready = null;
                _readyFor = null;
                Profile = null;
                LastDrinkAt = null;
                LastDrinkDay = null;
            }
        }
    }
}
