using System.Text.Json;

namespace Dezolati.Services
{
    /// <summary>
    /// Vlastní přezdívky kámošů – vidím je jen já. Leží v dokumentu Private/{můj uid} (číst a psát smí jen majitel,
    /// na rozdíl od profilu v Users, který čte každý přihlášený). Načte se 1× za spuštění, mezitím z telefonu.
    /// Sdílené s webem.
    /// </summary>
    public static class FriendAliases
    {
        private const string KeyPrefix = "aliasy_";
        private const int MaxLength = 30;

        private static Dictionary<string, string> _map = new();
        private static string? _loadedFor;
        private static bool _fromServer;

        private static string Key(string uid) => KeyPrefix + uid;

        /// <summary>Přezdívka, kterou jsem kámošovi dal já (null = žádná).</summary>
        public static string? Of(string friendUid)
        {
            LoadLocal();
            return _map.TryGetValue(friendUid, out var a) && !string.IsNullOrWhiteSpace(a) ? a : null;
        }

        /// <summary>Jak kámoše ukazovat: moje přezdívka, jinak jeho vlastní.</summary>
        public static string? Display(string friendUid, string? hisNick) => Of(friendUid) ?? hisNick;

        /// <summary>Z telefonu hned, ze serveru jednou za spuštění (1 čtení). Bez signálu to, co je v telefonu.</summary>
        public static async Task EnsureLoadedAsync()
        {
            LoadLocal();
            if (_fromServer || AuthSession.Uid is not string me || !Net.IsOnline) return;
            try
            {
                var doc = await FirestoreRest.GetAsync($"Private/{me}");
                _map = doc?.StrMap("Aliases")
                           .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                           .ToDictionary(kv => kv.Key, kv => kv.Value!) ?? new();
                _fromServer = true;
                SaveLocal(me);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Přezdívky kámošů: {ex.Message}");
            }
        }

        /// <summary>Uloží (prázdné = smaže) moji přezdívku pro kámoše. 1 zápis.</summary>
        public static async Task SetAsync(string friendUid, string? alias)
        {
            if (AuthSession.Uid is not string me) return;
            await EnsureLoadedAsync();
            alias = alias?.Trim();
            if (alias?.Length > MaxLength) alias = alias[..MaxLength];

            var next = new Dictionary<string, string>(_map);
            if (string.IsNullOrEmpty(alias)) next.Remove(friendUid);
            else next[friendUid] = alias;

            await FirestoreRest.PatchAsync($"Private/{me}", new()
            {
                ["Aliases"] = next.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)
            });
            _map = next;
            SaveLocal(me);
        }

        /// <summary>Odebraný kámoš: přezdívku zahodit (jen v telefonu, na serveru při příští změně).</summary>
        public static void Forget(string friendUid)
        {
            LoadLocal();
            if (_map.Remove(friendUid) && AuthSession.Uid is string me) SaveLocal(me);
        }

        /// <summary>Po odhlášení / smazání účtu.</summary>
        public static void Clear()
        {
            if (_loadedFor != null) Preferences.Remove(Key(_loadedFor));
            _map = new();
            _loadedFor = null;
            _fromServer = false;
        }

        private static void LoadLocal()
        {
            var me = AuthSession.Uid;
            if (me == _loadedFor) return;
            _loadedFor = me;
            _fromServer = false;
            _map = new();
            if (me == null) return;
            try
            {
                var json = Preferences.Get(Key(me), null as string);
                if (json != null) _map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
            catch { }
        }

        private static void SaveLocal(string me)
        {
            try { Preferences.Set(Key(me), JsonSerializer.Serialize(_map)); } catch { }
        }
    }
}
