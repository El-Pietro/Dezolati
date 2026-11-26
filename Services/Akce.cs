using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dezolati.Services
{
    /// <summary>Bečka na akci. Full = plný počet piv (30 l = 60), Beers = vypito od naražení.</summary>
    public sealed class AkceKeg
    {
        public string Name { get; set; } = "Bečka";
        public double Liters { get; set; } = 30;
        public int Beers { get; set; }
        public DateTime? TappedAt { get; set; }
        public DateTime? EmptyAt { get; set; }

        public int Full => (int)Math.Round(Liters / 0.5);
        public int Left => Math.Max(0, Full - Beers);
        public string Label => $"{Name} {Liters:0.#} l";
    }

    /// <summary>Řádek akčního lístku: kdo, kolik piv, kdy naposledy a kdo zapsal.</summary>
    public sealed class AkceRow
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "?";
        public int N { get; set; }
        public DateTime? Last { get; set; }
        public string By { get; set; } = "";
    }

    public sealed class AkceChange
    {
        public string By { get; set; } = "";
        public DateTime? At { get; set; }
        public string What { get; set; } = "";
        public string? Before { get; set; }
    }

    /// <summary>Akce u kámoše (Akce/{id}, functions/akce.js).</summary>
    public sealed class AkceInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public double? Lat { get; set; }
        public double? Lon { get; set; }
        public string Place { get; set; } = "";
        public string Host { get; set; } = "";
        public string Code { get; set; } = "";
        public Dictionary<string, string> Status { get; set; } = new();
        public Dictionary<string, string> Nicks { get; set; } = new();
        public HashSet<string> Guests { get; set; } = new();
        public HashSet<string> Names { get; set; } = new();
        public List<AkceKeg> Kegs { get; set; } = new();
        public int Keg { get; set; } = -1;
        public List<AkceRow> Rows { get; set; } = new();
        public List<AkceChange> History { get; set; } = new();
        public Dictionary<string, string> Theme { get; set; } = new();
        public bool HasResults { get; set; }
        public int Version { get; set; }

        public AkceKeg? CurrentKeg => Keg >= 0 && Keg < Kegs.Count ? Kegs[Keg] : null;
        public string? MyStatus => AkceService.Me is string me && Status.TryGetValue(me, out var s) ? s : null;
        public bool Going => MyStatus is "yes" or "maybe";
        public bool IsHost => AkceService.Me == Host;
        public bool Running => DateTime.Now >= From.AddHours(-1) && DateTime.Now <= To.AddHours(1);
        public bool Over => DateTime.Now > To;
        public string NameOf(string key) => Nicks.TryGetValue(key, out var n) ? n : "?";

        /// <summary>„so 5. 12. 18:00 – ne 6. 12. 6:00“</summary>
        public string When
        {
            get
            {
                var cs = new CultureInfo("cs-CZ");
                string D(DateTime d) => $"{d.ToString("ddd", cs)} {d.Day}. {d.Month}. {d:H:mm}";
                return $"{D(From)} – {(To.Date == From.Date || (To - From).TotalHours < 14 ? To.ToString("H:mm") : D(To))}";
            }
        }

        /// <summary>Odhad, kdy bečka padne (podle tempa od naražení); null = málo dat.</summary>
        public DateTime? KegEmptyEstimate()
        {
            if (CurrentKeg is not { TappedAt: DateTime tapped } k || k.Beers < 4) return null;
            var perBeer = (DateTime.Now - tapped).TotalMinutes / k.Beers;
            return perBeer <= 0 ? null : DateTime.Now.AddMinutes(perBeer * k.Left);
        }
    }

    /// <summary>
    /// Výzdoba akce: jeden motiv pro celou akci. Sloty (top, corner, fall, coaster) berou hotový motiv,
    /// vlastní obrázek (Akce/{id}/Media/{slot}, razítko v Theme.{slot}Img) nebo nic. Theme.{slot} = „preset“ | „img“ | „none“.
    /// </summary>
    public static class AkceTheme
    {
        public static readonly (string Key, string Label)[] Presets =
        {
            ("mikulas", "Mikulášská"), ("vanoce", "Vánoční"), ("party", "Party"), ("none", "Bez výzdoby")
        };
        public static readonly string[] Slots = { "top", "corner", "fall", "coaster" };

        public static string Preset(AkceInfo a) => a.Theme.TryGetValue("preset", out var p) ? p : "party";
        public static string Slot(AkceInfo a, string slot) => a.Theme.TryGetValue(slot, out var v) ? v : "preset";

        public static readonly (string Key, string Label)[] SlotLabels =
        {
            ("top", "Nahoře"), ("corner", "Roh"), ("fall", "Padá"), ("coaster", "Tácek")
        };

        /// <summary>Režim slotů z motivu (chybí = „preset“, u „none“ = „none“).</summary>
        public static Dictionary<string, string> SlotsOf(Dictionary<string, string>? theme)
        {
            var preset = theme != null && theme.TryGetValue("preset", out var p) ? p : "party";
            return Slots.ToDictionary(s => s, s => theme != null && theme.TryGetValue(s, out var v) ? v : preset == "none" ? "none" : "preset");
        }

        /// <summary>Motiv k uložení: preset + režimy slotů, razítka nahraných obrázků ze starého zůstanou.</summary>
        public static Dictionary<string, string> Build(string preset, IDictionary<string, string> slots, Dictionary<string, string>? old)
        {
            var t = new Dictionary<string, string> { ["preset"] = preset };
            foreach (var s in Slots)
            {
                if (old != null && old.TryGetValue(s + "Img", out var stamp)) t[s + "Img"] = stamp;
                t[s] = slots.TryGetValue(s, out var m) ? m : preset == "none" ? "none" : "preset";
            }
            return t;
        }

        /// <summary>Nový motiv, vlastní obrázky ze starého se nechají.</summary>
        public static Dictionary<string, string> FromPreset(string preset, Dictionary<string, string>? old = null)
        {
            var t = new Dictionary<string, string> { ["preset"] = preset };
            foreach (var s in Slots)
            {
                var img = old != null && old.TryGetValue(s + "Img", out var stamp) ? stamp : null;
                if (img != null) t[s + "Img"] = img;
                t[s] = img != null && old!.TryGetValue(s, out var v) && v == "img" ? "img" : preset == "none" ? "none" : "preset";
            }
            return t;
        }
    }

    /// <summary>
    /// Akce u kámoše: načtení (1 čtení), akce (Cloud Function akceAction), stav z tichého pushe (0 čtení),
    /// poznání „jsem na akci“ při zápisu (do 100 m od místa a v čase akce). Sdílené s webem.
    /// </summary>
    public static class AkceService
    {
        private const string Url = "https://europe-west10-dezolati.cloudfunctions.net/akceAction";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private const string KeyMine = "akce_mine";
        private const string KeyMineAt = "akce_mine_at";
        public const double HereMeters = 100;

        private static List<AkceInfo>? _mine;
        private static readonly Dictionary<string, AkceInfo> Cache = new();

        /// <summary>Změnila se akce (id) – push, vlastní zápis, úprava.</summary>
        public static event Action<string>? Changed;

        /// <summary>Host z odkazu (web, anonymní účet): jeho uid, když není přihlášený normálně. Nastaví stránka odkazu.</summary>
        public static Func<string?>? GuestUid { get; set; }

        /// <summary>Já na akci: přihlášený, jinak host z odkazu.</summary>
        public static string? Me => AuthSession.Uid ?? GuestUid?.Invoke();

        private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

        // ---------------- moje akce ----------------

        public static List<AkceInfo> Mine
        {
            get
            {
                if (_mine != null) return _mine;
                try
                {
                    var json = Preferences.Get(KeyMine, null as string);
                    _mine = string.IsNullOrEmpty(json) ? new() : JsonSerializer.Deserialize<List<AkceInfo>>(json, Opts) ?? new();
                }
                catch { _mine = new(); }
                return _mine;
            }
        }

        /// <summary>Moje akce (nadcházející a z posledních 14 dní). Jedno čtení na akci; uložené max. 6 h.</summary>
        public static async Task<List<AkceInfo>> LoadMineAsync(bool force = false)
        {
            if (AuthSession.Uid is not string me) return new();
            if (!force && DateTime.UtcNow.Ticks - Preferences.Get(KeyMineAt, 0L) < TimeSpan.FromHours(6).Ticks) return Mine;
            var docs = await FirestoreRest.QueryAsync("Akce", new[]
            {
                Filter.ArrayContains("Members", me),
                Filter.Gte("To", DateTime.UtcNow.AddDays(-14))
            });
            var list = docs.Select(Parse).Where(a => a != null).Cast<AkceInfo>().OrderBy(a => a.From).ToList();
            foreach (var a in list) Cache[a.Id] = a;
            SaveMine(list);
            return list;
        }

        private static void SaveMine(List<AkceInfo> list)
        {
            _mine = list;
            try
            {
                Preferences.Set(KeyMine, JsonSerializer.Serialize(list));
                Preferences.Set(KeyMineAt, DateTime.UtcNow.Ticks);
            }
            catch { }
        }

        private static void Remember(AkceInfo a)
        {
            Cache[a.Id] = a;
            var list = Mine.Where(x => x.Id != a.Id).ToList();
            list.Add(a);
            _mine = list.OrderBy(x => x.From).ToList();
            try { Preferences.Set(KeyMine, JsonSerializer.Serialize(_mine)); } catch { }
            Changed?.Invoke(a.Id);
        }

        public static AkceInfo? Cached(string id) => Cache.TryGetValue(id, out var a) ? a : Mine.FirstOrDefault(x => x.Id == id);

        /// <summary>Akce z Firestore (1 čtení) – při otevření akčního lístku a po ↻.</summary>
        public static async Task<AkceInfo?> GetAsync(string id)
        {
            var doc = await FirestoreRest.GetAsync($"Akce/{id}");
            if (doc == null || Parse(doc) is not { } a) return null;
            Remember(a);
            return a;
        }

        /// <summary>Akce, na které právě jsem (dal jsem Jdu/Možná, je v čase a jsem do 100 m) – pro zápis z běžného lístku.</summary>
        public static string? AttendingAt(double lat, double lon, DateTime nowLocal)
        {
            foreach (var a in Mine)
            {
                if (!a.Going || a.Lat is not double alat || a.Lon is not double alon) continue;
                if (nowLocal < a.From.AddHours(-1) || nowLocal > a.To.AddHours(1)) continue;
                if (PubCatalog.DistanceMeters(lat, lon, alat, alon) <= HereMeters) return a.Id;
            }
            return null;
        }

        /// <summary>Akce, která teď běží a jsem na ni přihlášený (cedulka na lístku).</summary>
        public static AkceInfo? RunningNow() => Mine.FirstOrDefault(a => a.Going && a.Running);

        // ---------------- stav z pushe / z odpovědi serveru ----------------

        /// <summary>Tichý push „akce“: celý stav akčního lístku, 0 čtení.</summary>
        public static void OnPushState(string? json)
        {
            if (string.IsNullOrEmpty(json)) return;
            try { ApplyState(JsonNode.Parse(json)); } catch { }
        }

        private static void ApplyState(JsonNode? s)
        {
            if (s?["id"]?.GetValue<string>() is not string id) return;
            var a = Cached(id);
            if (a == null) return;   // akci ještě nemám načtenou – načte se při otevření
            var v = s["v"]?.GetValue<int>() ?? 0;
            if (v < a.Version) return;   // starší stav (pushe můžou přijít přeházeně)
            a.Version = v;
            if (s["n"]?.GetValue<string>() is string n) a.Name = n;
            if (s["f"] is JsonNode f) a.From = FromMs(f.GetValue<long>());
            if (s["t"] is JsonNode t) a.To = FromMs(t.GetValue<long>());
            if (s["k"] is JsonObject k)
            {
                var i = k["i"]?.GetValue<int>() ?? -1;
                a.Keg = i;
                while (a.Kegs.Count <= i) a.Kegs.Add(new AkceKeg());
                if (i >= 0)
                {
                    a.Kegs[i].Name = k["n"]?.GetValue<string>() ?? a.Kegs[i].Name;
                    a.Kegs[i].Liters = k["l"]?.GetValue<double>() ?? a.Kegs[i].Liters;
                    a.Kegs[i].Beers = k["b"]?.GetValue<int>() ?? 0;
                    if (k["at"]?.GetValue<long>() is long at && at > 0) a.Kegs[i].TappedAt = FromMs(at);
                }
            }
            else a.Keg = -1;
            if (s["r"] is JsonArray rows)
            {
                a.Rows = rows.OfType<JsonArray>().Select(r => new AkceRow
                {
                    Key = r[0]?.GetValue<string>() ?? "",
                    Name = r[1]?.GetValue<string>() ?? "?",
                    N = r[2]?.GetValue<int>() ?? 0,
                    Last = r[3]?.GetValue<long>() is long l && l > 0 ? FromMs(l) : null,
                    By = r[4]?.GetValue<string>() ?? ""
                }).ToList();
                foreach (var r in a.Rows) a.Nicks[r.Key] = r.Name;
            }
            Remember(a);
        }

        private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        private static long ToMs(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();

        // ---------------- akce na serveru ----------------

        public static async Task<(string Id, string Code)> CreateAsync(string name, DateTime from, DateTime to, double? lat, double? lon,
            string place, IEnumerable<AkceKeg> kegs, Dictionary<string, string>? theme = null)
        {
            var res = await CallAsync(new JsonObject
            {
                ["action"] = "create", ["name"] = name, ["from"] = ToMs(from), ["to"] = ToMs(to),
                ["lat"] = lat, ["lon"] = lon, ["place"] = place,
                ["kegs"] = new JsonArray(kegs.Select(k => (JsonNode)new JsonObject { ["name"] = k.Name, ["liters"] = k.Liters }).ToArray()),
                ["theme"] = ThemeJson(theme)
            });
            var id = res?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Server akci nevrátil.");
            await GetAsync(id);
            return (id, res?["code"]?.GetValue<string>() ?? "");
        }

        public static async Task UpdateAsync(string id, string? name = null, DateTime? from = null, DateTime? to = null,
            string? place = null, double? lat = null, double? lon = null, IEnumerable<AkceKeg>? plannedKegs = null,
            Dictionary<string, string>? theme = null)
        {
            var patch = new JsonObject();
            if (name != null) patch["name"] = name;
            if (from is DateTime f) patch["from"] = ToMs(f);
            if (to is DateTime t) patch["to"] = ToMs(t);
            if (place != null) patch["place"] = place;
            if (lat is double la && lon is double lo) { patch["lat"] = la; patch["lon"] = lo; }
            if (plannedKegs != null)
                patch["kegs"] = new JsonArray(plannedKegs.Select(k => (JsonNode)new JsonObject { ["name"] = k.Name, ["liters"] = k.Liters }).ToArray());
            if (theme != null) patch["theme"] = ThemeJson(theme);
            await CallAsync(new JsonObject { ["action"] = "update", ["id"] = id, ["patch"] = patch });
            await GetAsync(id);
        }

        private static JsonObject ThemeJson(Dictionary<string, string>? theme)
        {
            var o = new JsonObject();
            foreach (var kv in theme ?? new()) o[kv.Key] = kv.Value;
            return o;
        }

        public static Task InviteAsync(string id, IEnumerable<string> uids) =>
            CallAsync(new JsonObject { ["action"] = "invite", ["id"] = id, ["members"] = new JsonArray(uids.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()) });

        public static async Task RespondAsync(string id, string answer)
        {
            await CallAsync(new JsonObject { ["action"] = "respond", ["id"] = id, ["answer"] = answer });
            await GetAsync(id);
        }

        public static async Task DeleteAsync(string id)
        {
            await CallAsync(new JsonObject { ["action"] = "delete", ["id"] = id });
            SaveMine(Mine.Where(a => a.Id != id).ToList());
            Cache.Remove(id);
            Changed?.Invoke(id);
        }

        public static async Task AddNameAsync(string id, string name)
        {
            await CallAsync(new JsonObject { ["action"] = "addName", ["id"] = id, ["name"] = name });
            await GetAsync(id);
        }

        public static Task RevertAsync(string id, int index) =>
            CallAsync(new JsonObject { ["action"] = "revert", ["id"] = id, ["index"] = index });

        /// <summary>Pivo na akční lístek (komukoli). Server vrátí nový stav – žádné čtení navíc.</summary>
        public static async Task BeerAsync(string id, string who, int delta)
        {
            var res = await CallAsync(new JsonObject { ["action"] = "beer", ["id"] = id, ["who"] = who, ["delta"] = delta });
            ApplyState(res?["state"]);
            // pivo zapsané sám sobě se na mém lístku objeví hned (server ho zapsal i do Drinks)
            if (who == AuthSession.Uid) LocalCache.MarkStale();
        }

        /// <summary>Přeraženo: narazit bečku ze seznamu (index), nebo jinou (název + litry).</summary>
        public static async Task TapAsync(string id, int? keg, string? customName = null, double customLiters = 30)
        {
            var data = new JsonObject { ["action"] = "tap", ["id"] = id };
            if (customName != null) data["custom"] = new JsonObject { ["name"] = customName, ["liters"] = customLiters };
            else data["keg"] = keg ?? -1;
            var res = await CallAsync(data);
            await GetAsync(id);   // historie a seznam beček (stav z odpovědi je jen ta naražená)
            _ = res;
        }

        public sealed record LogItem(DateTime At, int Delta, string By, string Src);

        public static async Task<List<LogItem>> LogAsync(string id, string who)
        {
            var res = await CallAsync(new JsonObject { ["action"] = "log", ["id"] = id, ["who"] = who });
            return (res?["items"] as JsonArray ?? new()).OfType<JsonObject>().Select(i => new LogItem(
                FromMs(i["at"]?.GetValue<long>() ?? 0), i["delta"]?.GetValue<int>() ?? 0,
                i["by"]?.GetValue<string>() ?? "?", i["src"]?.GetValue<string>() ?? "")).ToList();
        }

        public sealed record StatsPerson(string Key, string Name, int Pivo, int Vino, int Panaky, int Michacky, int Total,
            DateTime? First, DateTime? Last, int? Tempo);
        public sealed record StatsKeg(string Name, double Liters, int Beers, DateTime? Tapped, DateTime? Empty);
        public sealed record AkceStats(int Pivo, double Liters, List<StatsPerson> People, List<(string Day, int Beers)> Days,
            Dictionary<string, string?> Titles, List<StatsKeg> Kegs);

        public static async Task<AkceStats> StatsAsync(string id)
        {
            var r = await CallAsync(new JsonObject { ["action"] = "stats", ["id"] = id }) ?? new JsonObject();
            DateTime? T(JsonNode? n) => n?.GetValue<long>() is long l && l > 0 ? FromMs(l) : null;
            var people = (r["people"] as JsonArray ?? new()).OfType<JsonObject>().Select(p => new StatsPerson(
                p["key"]?.GetValue<string>() ?? "", p["name"]?.GetValue<string>() ?? "?",
                p["pivo"]?.GetValue<int>() ?? 0, p["vino"]?.GetValue<int>() ?? 0, p["panaky"]?.GetValue<int>() ?? 0,
                p["michacky"]?.GetValue<int>() ?? 0, p["total"]?.GetValue<int>() ?? 0, T(p["first"]), T(p["last"]),
                p["tempo"]?.GetValue<int?>())).ToList();
            var days = (r["days"] as JsonObject ?? new()).Select(kv => (kv.Key, kv.Value?.GetValue<int>() ?? 0)).OrderBy(d => d.Key).ToList();
            var titles = (r["titles"] as JsonObject ?? new()).ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>());
            var kegs = (r["kegs"] as JsonArray ?? new()).OfType<JsonObject>().Select(k => new StatsKeg(
                k["name"]?.GetValue<string>() ?? "", k["liters"]?.GetValue<double>() ?? 0, k["beers"]?.GetValue<int>() ?? 0,
                T(k["tapped"]), T(k["empty"]))).ToList();
            return new AkceStats(r["pivo"]?.GetValue<int>() ?? 0, r["liters"]?.GetValue<double>() ?? 0, people, days, titles, kegs);
        }

        // ---------------- výzdoba: vlastní obrázky (Akce/{id}/Media/{slot}) ----------------

        private static readonly Dictionary<string, (string Stamp, string Data, string Mime)> MediaCache = new();

        /// <summary>Obrázek se načetl / změnil (klíč „{akce}/{slot}“) – appka si ho převede na IImage.</summary>
        public static event Action<string>? MediaLoaded;

        public static (string Data, string Mime)? MediaOf(string key) =>
            MediaCache.TryGetValue(key, out var m) ? (m.Data, m.Mime) : null;

        /// <summary>Náhled před nahráním (klíč třeba „nova/corner“).</summary>
        public static void SetLocalMedia(string key, string? data, string mime = "image/jpeg")
        {
            if (data == null) MediaCache.Remove(key); else MediaCache[key] = ("local", data, mime);
            MediaLoaded?.Invoke(key);
        }

        /// <summary>Načte vlastní obrázky výzdoby, které ještě nemám (1 čtení za obrázek, pak v paměti).</summary>
        public static async Task LoadMediaAsync(AkceInfo a)
        {
            foreach (var slot in AkceTheme.Slots)
            {
                var key = $"{a.Id}/{slot}";
                if (!a.Theme.TryGetValue(slot + "Img", out var stamp) || AkceTheme.Slot(a, slot) != "img") continue;
                if (MediaCache.TryGetValue(key, out var have) && have.Stamp == stamp) continue;
                try
                {
                    var doc = await FirestoreRest.GetAsync($"Akce/{a.Id}/Media/{slot}");
                    if (doc?.Str("Data") is not string data) continue;
                    MediaCache[key] = (stamp, data, doc.Str("Mime") ?? "image/jpeg");
                    MediaLoaded?.Invoke(key);
                }
                catch { /* bez obrázku se nakreslí motiv */ }
            }
        }

        /// <summary>Nahraje (base64, max ~180 kB) nebo smaže (null) vlastní obrázek slotu.</summary>
        public static async Task UploadMediaAsync(string id, string slot, string? data, string mime = "image/jpeg")
        {
            await CallAsync(new JsonObject { ["action"] = "media", ["id"] = id, ["slot"] = slot, ["data"] = data ?? "", ["mime"] = mime });
            var a = await GetAsync(id);
            var key = $"{id}/{slot}";
            if (data == null) MediaCache.Remove(key);
            else if (a != null && a.Theme.TryGetValue(slot + "Img", out var stamp)) MediaCache[key] = (stamp, data, mime);
            MediaLoaded?.Invoke(key);
        }

        // ---------------- hosté z odkazu (web) ----------------

        public sealed record Peek(string Id, string Name, DateTime From, DateTime To, string Place, string Host, List<string> Going, bool Member);

        public static async Task<Peek> PeekAsync(string code)
        {
            var r = await CallAsync(new JsonObject { ["action"] = "peek", ["code"] = code }) ?? new JsonObject();
            return new Peek(r["id"]?.GetValue<string>() ?? "", r["name"]?.GetValue<string>() ?? "", FromMs(r["from"]?.GetValue<long>() ?? 0),
                FromMs(r["to"]?.GetValue<long>() ?? 0), r["place"]?.GetValue<string>() ?? "", r["host"]?.GetValue<string>() ?? "?",
                (r["going"] as JsonArray ?? new()).Select(x => x?.GetValue<string>() ?? "").ToList(), r["member"]?.GetValue<bool>() ?? false);
        }

        public static async Task<string> JoinAsync(string code, string? name, string answer = "yes")
        {
            var r = await CallAsync(new JsonObject { ["action"] = "join", ["code"] = code, ["name"] = name, ["answer"] = answer });
            var id = r?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Přidat se nepovedlo.");
            await GetAsync(id);
            return id;
        }

        /// <summary>
        /// Zkušební akce (správce): začíná teď, 3 hodiny, špendlík kde stojím, jedna bečka, mikulášská výzdoba.
        /// Nikoho nezve – výsledky přijdou jen mně. Po zkoušce smazat.
        /// </summary>
        public static Task<(string Id, string Code)> CreateTestAsync(double? lat, double? lon) =>
            CreateAsync("Zkouška", DateTime.Now.AddMinutes(-5), DateTime.Now.AddHours(3), lat, lon,
                "zkušební akce", new[] { new AkceKeg { Name = "Zkušební", Liters = 10 } }, AkceTheme.FromPreset("mikulas"));

        /// <summary>Odkaz pro lidi bez appky.</summary>
        public static string ShareLink(AkceInfo a) => $"https://el-pietro.github.io/Dezolati/a/{a.Code}";

        // ---------------- pomocné ----------------

        private static async Task<JsonNode?> CallAsync(JsonObject data)
        {
            var token = await AuthSession.GetTokenAsync();
            using var req = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Content = new StringContent(new JsonObject { ["data"] = data }.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            JsonNode? root;
            try { root = JsonNode.Parse(json); }
            catch (JsonException) { throw new InvalidOperationException($"Server neodpověděl ({(int)resp.StatusCode})."); }
            if (root?["error"] is JsonNode err)
                throw new InvalidOperationException(err["message"]?.GetValue<string>() ?? "Server vrátil chybu.");
            return root?["result"];
        }

        /// <summary>Firestore dokument → AkceInfo (přes obyčejný JSON, Events.Plain).</summary>
        internal static AkceInfo? Parse(FsDoc doc)
        {
            try
            {
                var p = new JsonObject();
                foreach (var kv in doc.Fields) p[kv.Key] = Events.Plain(kv.Value);
                if (p["Deleted"] is JsonValue del && del.TryGetValue<bool>(out var deleted) && deleted) return null;
                DateTime T(JsonNode? n) => n?.GetValue<string>() is string s
                    ? DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ToLocalTime()
                    : DateTime.MinValue;
                DateTime? TN(JsonNode? n) => n == null ? null : T(n);
                double? D(JsonNode? n) => n is JsonValue v
                    ? (v.TryGetValue<double>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : null)
                    : null;
                Dictionary<string, string> Map(JsonNode? n) => (n as JsonObject ?? new()).ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
                var a = new AkceInfo
                {
                    Id = doc.Id,
                    Name = p["Name"]?.GetValue<string>() ?? "",
                    From = T(p["From"]),
                    To = T(p["To"]),
                    Lat = D(p["Lat"]),
                    Lon = D(p["Lon"]),
                    Place = p["Place"]?.GetValue<string>() ?? "",
                    Host = p["Host"]?.GetValue<string>() ?? "",
                    Code = p["Code"]?.GetValue<string>() ?? "",
                    Status = Map(p["Status"]),
                    Nicks = Map(p["Nicks"]),
                    Guests = (p["Guests"] as JsonObject ?? new()).Select(kv => kv.Key).ToHashSet(),
                    Names = (p["Names"] as JsonObject ?? new()).Select(kv => kv.Key).ToHashSet(),
                    Keg = (int)(D(p["Keg"]) ?? -1),
                    Theme = Map(p["Theme"]),
                    HasResults = p["Results"] is JsonObject,
                    Version = (int)(D(p["Version"]) ?? 0)
                };
                a.Kegs = (p["Kegs"] as JsonArray ?? new()).OfType<JsonObject>().Select(k => new AkceKeg
                {
                    Name = k["Name"]?.GetValue<string>() ?? "Bečka",
                    Liters = D(k["Liters"]) ?? 30,
                    Beers = (int)(D(k["Beers"]) ?? 0),
                    TappedAt = TN(k["TappedAt"]),
                    EmptyAt = TN(k["EmptyAt"])
                }).ToList();
                a.Rows = (p["Rows"] as JsonObject ?? new()).Select(kv => new AkceRow
                {
                    Key = kv.Key,
                    Name = a.NameOf(kv.Key),
                    N = (int)(D(kv.Value?["N"]) ?? 0),
                    Last = kv.Value?["Last"] is JsonNode l && D(l) is double ms && ms > 0 ? FromMs((long)ms) : null,
                    By = kv.Value?["By"]?.GetValue<string>() ?? ""
                }).ToList();
                a.History = (p["History"] as JsonArray ?? new()).OfType<JsonObject>().Select(h => new AkceChange
                {
                    By = h["By"]?.GetValue<string>() ?? "",
                    At = TN(h["At"]),
                    What = h["What"]?.GetValue<string>() ?? "",
                    Before = h["Before"]?.ToString()
                }).ToList();
                return a;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Řádky akčního lístku: lidé, co jdou (i bez zápisu), a kdo má piva; nejvíc piv nahoře.</summary>
        public static List<AkceRow> RowsFor(AkceInfo a)
        {
            var rows = a.Rows.ToDictionary(r => r.Key);
            foreach (var (uid, st) in a.Status)
                if (st is "yes" or "maybe" && !rows.ContainsKey(uid)) rows[uid] = new AkceRow { Key = uid, Name = a.NameOf(uid) };
            foreach (var key in a.Names)
                if (!rows.ContainsKey(key)) rows[key] = new AkceRow { Key = key, Name = a.NameOf(key) };
            return rows.Values.OrderByDescending(r => r.N).ThenBy(r => r.Name, StringComparer.Create(new CultureInfo("cs-CZ"), true)).ToList();
        }

        /// <summary>„23:41 (před 8 min) · zapsal Ondra“</summary>
        public static string Echo(AkceRow r)
        {
            if (r.Last is not DateTime last) return "zatím nic";
            var ago = DateTime.Now - last;
            var when = ago.TotalMinutes < 1 ? "teď" : ago.TotalMinutes < 60 ? $"před {(int)ago.TotalMinutes} min" : $"před {(int)ago.TotalHours} h {ago.Minutes} min";
            var by = r.By.Length > 0 && r.By != r.Name ? $" · zapsal {r.By}" : "";
            return $"{last:H:mm} ({when}){by}";
        }
    }
}
