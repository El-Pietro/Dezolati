using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dezolati.Services
{
    // ---------------- Events/aktualni (functions/vanoce.js) ----------------

    public sealed record EventBadge(string Id, string Name, string Desc, string Icon, string Color, string Group,
        int? Need = null, string? Unit = null, bool Bonus = false, bool Meta = false, string? Hint = null);

    public sealed record EventTheme(string Theme, string From, string To);

    /// <summary>Zóna trhu: P = polygony, L = čáry (pás šířky W), C + R = kruh. Body jako text „lat,lon lat,lon …“.</summary>
    public sealed record TrhZone(string Id, string Name, string From, string To, string Status, string? Src = null,
        List<string>? P = null, List<string>? L = null, double W = 0, string? C = null, double R = 0)
    {
        private List<(double Lat, double Lon)[]>? _polys, _lines;
        private (double Lat, double Lon)? _center;

        public List<(double Lat, double Lon)[]> Polys => _polys ??= (P ?? new()).Select(Trhy.Decode).ToList();
        public List<(double Lat, double Lon)[]> Lines => _lines ??= (L ?? new()).Select(Trhy.Decode).ToList();
        public (double Lat, double Lon)? Center => _center ??= string.IsNullOrEmpty(C) ? null : Trhy.Decode(C)[0];

        public bool Confirmed => Status == "potvrzeno";
        public bool RunsOn(DateTime day) => string.CompareOrdinal(Events.Iso(day), From) >= 0 && string.CompareOrdinal(Events.Iso(day), To) <= 0;

        /// <summary>„27. 11. – 24. 12.“ nebo „29. 11.“</summary>
        public string When => From == To ? Events.Short(From) : $"{Events.Short(From)} – {Events.Short(To)}";
    }

    public sealed record EventDoc(string Id, string Name, string Theme, string From, string To,
        List<string>? Advent = null, List<EventTheme>? Themes = null, List<EventBadge>? Badges = null, List<TrhZone>? Trhy = null);

    /// <summary>
    /// Aktuální event (Vánoce 2026 …): jeden dokument Events/aktualni, čte se max. 1× za 12 h (uložený v telefonu / prohlížeči).
    /// Běží podle data (hospodský den) – správce si ho může zapnout dřív v Nastavení (náhled vzhledu a map;
    /// odznaky server před začátkem nepočítá, takže se náhledem nic nezíská).
    /// </summary>
    public static class Events
    {
        public const string AdminUid = "rB2bUxNHHzbfyHx3mYW4JW61l3N2";   // stejné jako UsageService.AdminUid
        public static bool IsAdmin => AuthSession.Uid == AdminUid;

        private const string KeyJson = "event_json";
        private const string KeyAt = "event_at";
        private const string KeyPreview = "event_preview";
        private const string KeyPreviewTheme = "event_preview_theme";
        private const string KeySnow = "event_snow";
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

        private static bool _loaded;
        private static EventDoc? _current;
        private static readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>Změnil se event, náhled nebo motiv → překreslit.</summary>
        public static event EventHandler? Changed;

        public static EventDoc? Current { get { LoadCached(); return _current; } }

        // ---------- nastavení ----------

        /// <summary>Náhled eventu před začátkem – jen pro správce.</summary>
        public static bool Preview
        {
            get => IsAdmin && Preferences.Get(KeyPreview, false);
            set { Preferences.Set(KeyPreview, value); Changed?.Invoke(null, EventArgs.Empty); }
        }

        /// <summary>Motiv v náhledu: vanoce / silvestr / zima.</summary>
        public static string PreviewTheme
        {
            get => Preferences.Get(KeyPreviewTheme, "vanoce");
            set { Preferences.Set(KeyPreviewTheme, value); Changed?.Invoke(null, EventArgs.Empty); }
        }

        /// <summary>Padající sníh / konfety (zapnuté rovnou, vypínač v Nastavení).</summary>
        public static bool Snow
        {
            get => Preferences.Get(KeySnow, true);
            set { Preferences.Set(KeySnow, value); Changed?.Invoke(null, EventArgs.Empty); }
        }

        // ---------- stav ----------

        /// <summary>Hospodský den teď (do 6:00 se počítá ke včerejšku).</summary>
        public static DateTime Today => DateTime.Now.Hour < 6 ? DateTime.Today.AddDays(-1) : DateTime.Today;

        /// <summary>Event opravdu běží (podle data).</summary>
        public static bool Running => Current is { } e && string.CompareOrdinal(Iso(Today), e.From) >= 0 && string.CompareOrdinal(Iso(Today), e.To) <= 0;

        /// <summary>Ukazovat event (běží, nebo náhled správce).</summary>
        public static bool Active => Current != null && (Running || Preview);

        /// <summary>Motiv teď: vanoce / silvestr / zima, null = žádný.</summary>
        public static string? Theme
        {
            get
            {
                if (!Active || Current is not { } e) return null;
                if (Preview) return PreviewTheme;
                var now = DateTime.Now;
                foreach (var t in e.Themes ?? new())
                    if (ParseLocal(t.From) is { } a && ParseLocal(t.To) is { } b && now >= a && now < b) return t.Theme;
                return e.Theme;
            }
        }

        /// <summary>Kolik adventních svíček už hoří (0–4).</summary>
        public static int AdventLit => Current?.Advent?.Count(d => string.CompareOrdinal(d, Iso(Today)) <= 0) ?? 0;

        public static IReadOnlyList<EventBadge> Badges => Current?.Badges ?? new();

        // ---------- načtení ----------

        private static void LoadCached()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var json = Preferences.Get(KeyJson, null as string);
                if (!string.IsNullOrEmpty(json)) _current = JsonSerializer.Deserialize<EventDoc>(json, JsonOpts);
            }
            catch { _current = null; }
        }

        /// <summary>Načíst Events/aktualni (1 čtení), když je uložená verze starší než 12 h (force = hned).</summary>
        public static async Task EnsureAsync(bool force = false)
        {
            LoadCached();
            if (AuthSession.Uid == null) return;
            var at = Preferences.Get(KeyAt, 0L);
            if (!force && DateTime.UtcNow.Ticks - at < MaxAge.Ticks) return;
            if (!await Gate.WaitAsync(0)) return;   // už se načítá
            try
            {
                var doc = await FirestoreRest.GetAsync("Events/aktualni");
                var before = _current;
                _current = doc == null ? null : Parse(doc);
                Preferences.Set(KeyJson, _current == null ? "" : JsonSerializer.Serialize(_current, JsonOpts));
                Preferences.Set(KeyAt, DateTime.UtcNow.Ticks);
                if (!Equals(before?.Id, _current?.Id) || before?.Trhy?.Count != _current?.Trhy?.Count || force)
                    Changed?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Event: {ex.Message}");
            }
            finally
            {
                Gate.Release();
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        private static EventDoc? Parse(FsDoc doc)
        {
            var plain = new JsonObject();
            foreach (var kv in doc.Fields) plain[kv.Key] = Plain(kv.Value);
            return plain.Deserialize<EventDoc>(JsonOpts);
        }

        /// <summary>Hodnota z Firestore REST ({"stringValue": …}) → obyčejný JSON.</summary>
        internal static JsonNode? Plain(JsonNode? v)
        {
            if (v is not JsonObject o) return null;
            if (o["stringValue"] is { } s) return JsonValue.Create(s.GetValue<string>());
            if (o["integerValue"] is { } i) return JsonValue.Create(long.Parse(i.ToString(), CultureInfo.InvariantCulture));
            if (o["doubleValue"] is { } d) return JsonValue.Create(d.GetValue<double>());
            if (o["booleanValue"] is { } b) return JsonValue.Create(b.GetValue<bool>());
            if (o["timestampValue"] is { } t) return JsonValue.Create(t.GetValue<string>());
            if (o["arrayValue"] is JsonObject arr)
                return new JsonArray(((arr["values"] as JsonArray) ?? new JsonArray()).Select(x => Plain(x)).ToArray());
            if (o["mapValue"] is JsonObject map)
            {
                var res = new JsonObject();
                if (map["fields"] is JsonObject fields)
                    foreach (var kv in fields) res[kv.Key] = Plain(kv.Value);
                return res;
            }
            return null;   // nullValue
        }

        // ---------- data ----------

        public static string Iso(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>„2026-12-24“ → „24. 12.“</summary>
        public static string Short(string iso) =>
            DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? $"{d.Day}. {d.Month}." : iso;

        private static DateTime? ParseLocal(string s) =>
            DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>
    /// Vánoční trhy: pozná, jestli je bod v zóně trhu (polygon, pás podél ulic, kruh) s tolerancí GPS.
    /// Počítá se v telefonu – přesná poloha nikam neodchází, na server jde jen TrhId a poloha zaokrouhlená na ~100 m.
    /// </summary>
    public static class Trhy
    {
        public const double ToleranceMeters = 15;
        public const double PubWinsMeters = 20;    // hospoda do 20 m má přednost před trhem
        public const double HomeWinsMeters = 20;   // stejně tak domov

        public static IReadOnlyList<TrhZone> All => Events.Current?.Trhy ?? new();

        /// <summary>Trh, ve kterém jsem (v náhledu správce i mimo termín trhu).</summary>
        public static TrhZone? Match(double lat, double lon, DateTime day)
        {
            if (!Events.Active) return null;
            foreach (var z in All)
                if ((Events.Preview || z.RunsOn(day)) && Contains(z, lat, lon, ToleranceMeters)) return z;
            return null;
        }

        public static bool Contains(TrhZone z, double lat, double lon, double tol)
        {
            foreach (var r in z.Polys)
                if (InPolygon(lat, lon, r) || EdgeDistance(lat, lon, r, true) <= tol) return true;
            foreach (var l in z.Lines)
                if (EdgeDistance(lat, lon, l, false) <= z.W + tol) return true;
            if (z.Center is { } c && PubCatalog.DistanceMeters(lat, lon, c.Lat, c.Lon) <= z.R + tol) return true;
            return false;
        }

        internal static (double Lat, double Lon)[] Decode(string s) =>
            s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p =>
            {
                var i = p.IndexOf(',');
                return (double.Parse(p[..i], CultureInfo.InvariantCulture), double.Parse(p[(i + 1)..], CultureInfo.InvariantCulture));
            }).ToArray();

        private static bool InPolygon(double lat, double lon, (double Lat, double Lon)[] ring)
        {
            var inside = false;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                var (yi, xi) = ring[i];
                var (yj, xj) = ring[j];
                if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        private static double EdgeDistance(double lat, double lon, (double Lat, double Lon)[] pts, bool closed)
        {
            if (pts.Length == 1) return PubCatalog.DistanceMeters(lat, lon, pts[0].Lat, pts[0].Lon);
            var best = double.MaxValue;
            for (var i = 0; i + 1 < pts.Length; i++) best = Math.Min(best, SegDistance(lat, lon, pts[i], pts[i + 1]));
            if (closed && pts.Length > 2) best = Math.Min(best, SegDistance(lat, lon, pts[^1], pts[0]));
            return best;
        }

        /// <summary>Vzdálenost bodu od úsečky v metrech (rovinně kolem bodu – na stovky metrů stačí).</summary>
        private static double SegDistance(double lat, double lon, (double Lat, double Lon) a, (double Lat, double Lon) b)
        {
            const double mLat = 111320;
            var mLon = 111320 * Math.Cos(lat * Math.PI / 180);
            double ax = (a.Lon - lon) * mLon, ay = (a.Lat - lat) * mLat, bx = (b.Lon - lon) * mLon, by = (b.Lat - lat) * mLat;
            double dx = bx - ax, dy = by - ay, len = dx * dx + dy * dy;
            var t = len == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / len, 0, 1);
            return Math.Sqrt(Math.Pow(ax + t * dx, 2) + Math.Pow(ay + t * dy, 2));
        }

        /// <summary>
        /// Tvary zóny pro kreslení do mapy: polygony; pás podél ulice jako obdélníky úseků + kolečka v ohybech; kruh.
        /// </summary>
        public static List<(double Lat, double Lon)[]> Shapes(TrhZone z, double extra = 0)
        {
            var res = new List<(double Lat, double Lon)[]>(z.Polys);
            foreach (var line in z.Lines)
            {
                var w = z.W + extra;
                for (var i = 0; i + 1 < line.Length; i++)
                {
                    var (a, b) = (line[i], line[i + 1]);
                    var mLon = 111320 * Math.Cos(a.Lat * Math.PI / 180);
                    double dx = (b.Lon - a.Lon) * mLon, dy = (b.Lat - a.Lat) * 111320, len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 0.1) continue;
                    double ox = -dy / len * w, oy = dx / len * w;   // kolmice, metry
                    (double, double) P((double Lat, double Lon) p, double sx, double sy) => (p.Lat + sy / 111320, p.Lon + sx / mLon);
                    res.Add(new[] { P(a, ox, oy), P(b, ox, oy), P(b, -ox, -oy), P(a, -ox, -oy) });
                }
                foreach (var p in line) res.Add(Circle(p.Lat, p.Lon, w, 12));
            }
            if (z.Center is { } c) res.Add(Circle(c.Lat, c.Lon, z.R + extra, 28));
            return res;
        }

        public static (double Lat, double Lon)[] Circle(double lat, double lon, double r, int n)
        {
            var mLon = 111320 * Math.Cos(lat * Math.PI / 180);
            return Enumerable.Range(0, n).Select(i =>
            {
                var a = i * 2 * Math.PI / n;
                return (lat + Math.Sin(a) * r / 111320, lon + Math.Cos(a) * r / mLon);
            }).ToArray();
        }

        /// <summary>Střed zóny (na přiblížení mapy a vzdálenost).</summary>
        public static (double Lat, double Lon) CenterOf(TrhZone z)
        {
            if (z.Center is { } c) return c;
            var pts = z.Polys.SelectMany(p => p).Concat(z.Lines.SelectMany(l => l)).ToList();
            return pts.Count == 0 ? (0, 0) : (pts.Average(p => p.Lat), pts.Average(p => p.Lon));
        }

        /// <summary>Nejbližší trhy (běžící i budoucí, ne skončené) se vzdáleností v metrech.</summary>
        public static List<(TrhZone Zone, double Meters)> Nearest(double lat, double lon, int count)
        {
            var today = Events.Iso(Events.Today);
            return All.Where(z => string.CompareOrdinal(z.To, today) >= 0)
                .Select(z => { var c = CenterOf(z); return (z, PubCatalog.DistanceMeters(lat, lon, c.Lat, c.Lon)); })
                .OrderBy(x => x.Item2).Take(count).ToList();
        }

        /// <summary>„350 m“ / „12 km“</summary>
        public static string DistanceText(double m) => m < 1000 ? $"{Math.Round(m / 10) * 10:0} m" : $"{m / 1000:0.#} km";

        /// <summary>
        /// Při zápisu: trh podle polohy (hospoda nebo domov do 20 m mají přednost).
        /// Vrací upravené místo (na trzích = „jinde“ s názvem trhu), TrhId a polohu zaokrouhlenou na 3 desetinná místa.
        /// </summary>
        public static (PlaceInfo Place, string? TrhId, double? EvLat, double? EvLon) Apply(PlaceInfo place, DateTime day)
        {
            if (!Events.Active || LastLocation.Get(TimeSpan.FromMinutes(2)) is not { } me) return (place, null, null, null);
            var evLat = Math.Round(me.Lat, 3);
            var evLon = Math.Round(me.Lon, 3);

            var zone = Match(me.Lat, me.Lon, day);
            if (zone == null) return (place, null, evLat, evLon);

            if (place.Place == Services.Place.Pub && place.Lat is double pl && place.Lon is double po
                && PubCatalog.DistanceMeters(me.Lat, me.Lon, pl, po) <= PubWinsMeters)
                return (place, null, evLat, evLon);
            if (place.Place == Services.Place.Home && HomePlace.Location is { } home
                && PubCatalog.DistanceMeters(me.Lat, me.Lon, home.Lat, home.Lon) <= HomeWinsMeters)
                return (place, null, evLat, evLon);

            return (new PlaceInfo(Services.Place.Other, null, zone.Name, me.Lat, me.Lon), zone.Id, evLat, evLon);
        }
    }
}
