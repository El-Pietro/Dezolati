using System.Globalization;
using System.Text.Json;

namespace Dezolati.Services
{
    /// <summary>Kde se pilo: hospoda / doma / jinde (poloha známá, nic poblíž) / null = nevím.</summary>
    public static class Place
    {
        public const string Pub = "pub";
        public const string Home = "home";
        public const string Other = "other";
    }

    /// <summary>Výsledek „kde teď jsem“ při zápisu.</summary>
    public sealed record PlaceInfo(string? Place, string? PubId, string? PubName, double? Lat = null, double? Lon = null)
    {
        public static readonly PlaceInfo Unknown = new(null, null, null);
    }

    /// <summary>
    /// Poslední známá poloha z používání appky (jen v telefonu). Podle ní telefon sám rozhodne,
    /// jestli ukázat pozvánku „do 15 km“ – na pozadí polohu zjišťovat nejde a nikam se neposílá.
    /// </summary>
    public static class LastLocation
    {
        private const string Key = "last_location";
        private record Loc(double Lat, double Lon, DateTime At);

        public static void Remember(double lat, double lon)
        {
            try { Preferences.Set(Key, JsonSerializer.Serialize(new Loc(lat, lon, DateTime.UtcNow))); } catch { }
        }

        /// <summary>Poloha, pokud není starší než maxAge.</summary>
        public static (double Lat, double Lon)? Get(TimeSpan maxAge)
        {
            try
            {
                var json = Preferences.Get(Key, null as string);
                if (string.IsNullOrEmpty(json) || JsonSerializer.Deserialize<Loc>(json) is not { } l) return null;
                return DateTime.UtcNow - l.At <= maxAge ? (l.Lat, l.Lon) : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Domov – poloha je JEN v tomhle telefonu (Preferences), na server nikdy nejde.
    /// Na serveru se objeví jen to, že se pilo „doma“ (bez souřadnic).
    /// </summary>
    public static class HomePlace
    {
        private const string KeyLat = "home_lat";
        private const string KeyLon = "home_lon";
        public const double RadiusMeters = 80;

        public static bool IsSet => Preferences.ContainsKey(KeyLat) && Preferences.ContainsKey(KeyLon);

        public static (double Lat, double Lon)? Location
        {
            get
            {
                if (!IsSet) return null;
                var lat = double.TryParse(Preferences.Get(KeyLat, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : double.NaN;
                var lon = double.TryParse(Preferences.Get(KeyLon, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : double.NaN;
                return double.IsNaN(lat) || double.IsNaN(lon) ? null : (lat, lon);
            }
        }

        public static void Set(double lat, double lon)
        {
            Preferences.Set(KeyLat, lat.ToString("R", CultureInfo.InvariantCulture));
            Preferences.Set(KeyLon, lon.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void Clear()
        {
            Preferences.Remove(KeyLat);
            Preferences.Remove(KeyLon);
        }

        public static bool IsNear(double lat, double lon) =>
            Location is { } h && PubCatalog.DistanceMeters(lat, lon, h.Lat, h.Lon) <= RadiusMeters;
    }

    /// <summary>
    /// Ceny pro útratu: výchozí (Nastavení, jen v telefonu) a ceny hospody z jejího profilu
    /// (sdílené, kdokoli je upraví). Profil hospody se kvůli cenám čte max. jednou za 12 h.
    /// </summary>
    public static class Prices
    {
        private const string KeyPivo = "price_pivo";
        private const string KeyVino = "price_vino";
        private const string KeyPanak = "price_panak";
        private static readonly TimeSpan PubCacheFor = TimeSpan.FromHours(12);

        public static int Pivo { get => Preferences.Get(KeyPivo, 0); set => Preferences.Set(KeyPivo, Math.Max(0, value)); }
        public static int Vino { get => Preferences.Get(KeyVino, 0); set => Preferences.Set(KeyVino, Math.Max(0, value)); }
        public static int Panak { get => Preferences.Get(KeyPanak, 0); set => Preferences.Set(KeyPanak, Math.Max(0, value)); }

        /// <summary>Má uživatel vyplněné aspoň nějaké výchozí ceny?</summary>
        public static bool AnyDefault => Pivo > 0 || Vino > 0 || Panak > 0;

        public static int Default(int pivo, int vino, int panaky) => pivo * Pivo + vino * Vino + panaky * Panak;

        private record PubPrices(DateTime At, int? Pivo, int? Vino, int? Panak);

        private static string PubKey(string pubId) => $"pub_prices_{pubId}";

        /// <summary>Ceny hospody (null = nevyplněné). Z mezipaměti, jinak 1 čtení profilu.</summary>
        public static async Task<(int? Pivo, int? Vino, int? Panak)> OfPubAsync(string pubId)
        {
            try
            {
                var json = Preferences.Get(PubKey(pubId), null as string);
                if (!string.IsNullOrEmpty(json) && JsonSerializer.Deserialize<PubPrices>(json) is { } c
                    && DateTime.UtcNow - c.At < PubCacheFor)
                    return (c.Pivo, c.Vino, c.Panak);

                if (!Net.IsOnline) return (null, null, null);
                var profile = await PubService.GetProfileAsync(pubId);
                Remember(pubId, profile?.PricePivo, profile?.PriceVino, profile?.PricePanak);
                return (profile?.PricePivo, profile?.PriceVino, profile?.PricePanak);
            }
            catch
            {
                return (null, null, null);
            }
        }

        /// <summary>Po načtení / úpravě profilu hospody – ať se nečte znovu.</summary>
        public static void Remember(string pubId, int? pivo, int? vino, int? panak)
        {
            try { Preferences.Set(PubKey(pubId), JsonSerializer.Serialize(new PubPrices(DateTime.UtcNow, pivo, vino, panak))); }
            catch { }
        }

        /// <summary>Kolik stál zápis: cena hospody, kde ji má vyplněnou, jinak výchozí.</summary>
        public static async Task<int> ForDrinkAsync(int pivo, int vino, int panaky, string? pubId)
        {
            int? pp = null, pv = null, pa = null;
            if (pubId != null) (pp, pv, pa) = await OfPubAsync(pubId);
            return pivo * (pp ?? Pivo) + vino * (pv ?? Vino) + panaky * (pa ?? Panak);
        }
    }

    /// <summary>
    /// Orientační promile (Widmark). Váha, stupňovitost piva a časy dnešních zápisů jsou JEN v telefonu.
    /// Je to odhad ze vzorce, ne dechovka – appka to tak vždy i píše.
    /// </summary>
    public static class Promile
    {
        private const string KeyEnabled = "promile_on";
        private const string KeyWeight = "promile_weight";
        private const string KeyDegrees = "promile_beer_deg";
        public const double Burn = 0.15;   // ‰ za hodinu

        public static bool Enabled { get => Preferences.Get(KeyEnabled, true); set => Preferences.Set(KeyEnabled, value); }
        public static int WeightKg { get => Preferences.Get(KeyWeight, 0); set => Preferences.Set(KeyWeight, Math.Clamp(value, 0, 300)); }
        /// <summary>Stupňovitost piva: 10 / 11 / 12.</summary>
        public static int BeerDegrees { get => Preferences.Get(KeyDegrees, 12); set => Preferences.Set(KeyDegrees, value is 10 or 11 or 12 ? value : 12); }

        /// <summary>Gramy alkoholu: pivo 0,5 l (10° 4 %, 11° 4,5 %, 12° 5 %), víno 2 dcl 12 %, panák 4 cl 40 %.</summary>
        /// <remarks>Panák podle druhu (slivovice 55 %, likér 20 %, jinak 40 %), míchačka = jeden 40% panák.</remarks>
        private static double Grams(int pivo, int vino, int panaky, int michacky = 0, double panakAbv = 0.40)
        {
            var beerAbv = BeerDegrees switch { 10 => 0.040, 11 => 0.045, _ => 0.050 };
            return pivo * 500 * beerAbv * 0.789 + vino * 200 * 0.12 * 0.789
                 + panaky * 40 * panakAbv * 0.789 + michacky * 40 * 0.40 * 0.789;
        }

        // ---------------- log dnešních zápisů (jen v telefonu) ----------------

        private record LogItem(DateTime At, DateTime Day, int Pivo, int Vino, int Panaky, int Michacky = 0, double PanakAbv = 0.40);

        private static string LogKey(string uid) => $"promile_log_{uid}";

        private static List<LogItem> LoadLog(string uid)
        {
            try
            {
                var json = Preferences.Get(LogKey(uid), null as string);
                return string.IsNullOrEmpty(json) ? new() : JsonSerializer.Deserialize<List<LogItem>>(json) ?? new();
            }
            catch
            {
                return new();
            }
        }

        /// <summary>Každý zápis i škrtnutí dneška (záporné hodnoty). Starší dny se zahazují.</summary>
        public static void Log(string uid, DateTime day, DateTime utcAt, int pivo, int vino, int panaky,
            int michacky = 0, double panakAbv = 0.40)
        {
            try
            {
                var items = LoadLog(uid).Where(i => i.Day.Date >= day.Date.AddDays(-1)).ToList();
                items.Add(new LogItem(utcAt, day.Date, pivo, vino, panaky, michacky, panakAbv));
                Preferences.Set(LogKey(uid), JsonSerializer.Serialize(items));
            }
            catch { }
        }

        /// <summary>„střízlivý kolem 4:30“ / „střízlivá zítra kolem 9:00“ / „… pozítří …“ / „… ve čtvrtek …“.</summary>
        public static string SoberText(DateTime soberLocal, string? gender)
        {
            var adj = gender == "Píčka" ? "střízlivá" : gender == "Píč" ? "střízlivý" : "střízlivý/á";
            var days = (soberLocal.Date - DateTime.Now.Date).Days;
            var when = days <= 0 ? "" : days == 1 ? "zítra " : days == 2 ? "pozítří " : soberLocal.DayOfWeek switch
            {
                DayOfWeek.Monday => "v pondělí ",
                DayOfWeek.Tuesday => "v úterý ",
                DayOfWeek.Wednesday => "ve středu ",
                DayOfWeek.Thursday => "ve čtvrtek ",
                DayOfWeek.Friday => "v pátek ",
                DayOfWeek.Saturday => "v sobotu ",
                _ => "v neděli "
            };
            return $"{adj} {when}kolem {soberLocal:H:mm}";
        }

        /// <summary>Promile kámoše: odhad uložený při jeho zápisu → kolik je teď (odbourávání) a kdy na nule.</summary>
        public static (double Promile, DateTime SoberAtLocal)? FromShared(double? value, DateTime? atUtc)
        {
            if (value is not double v || atUtc is not DateTime at || v <= 0) return null;
            var now = DateTime.UtcNow;
            var current = v - Burn * (now - at.ToUniversalTime()).TotalHours;
            if (current <= 0.005) return null;
            return (current, now.AddHours(current / Burn).ToLocalTime());
        }

        /// <summary>Odhad teď: (‰, kdy na nule). null = vypnuto / nevyplněná váha / nic dnes.</summary>
        public static (double Promile, DateTime SoberAtLocal)? Estimate(string uid, string? gender)
        {
            if (!Enabled || WeightKg < 30) return null;
            var r = gender == "Píčka" ? 0.55 : gender == "Píč" ? 0.68 : 0.6;
            var now = DateTime.UtcNow;

            // posledních 24 h, v pořadí; škrtnutí ubírají z posledního
            var items = LoadLog(uid).Where(i => now - i.At < TimeSpan.FromHours(24)).OrderBy(i => i.At).ToList();
            if (items.Count == 0) return null;

            double bac = 0;
            DateTime? last = null;
            foreach (var i in items)
            {
                if (last is DateTime l) bac = Math.Max(0, bac - Burn * (i.At - l).TotalHours);
                var g = Grams(i.Pivo, i.Vino, i.Panaky, i.Michacky, i.PanakAbv);
                bac = Math.Max(0, bac + g / (WeightKg * r));
                last = i.At;
            }

            var nowBac = Math.Max(0, bac - Burn * (now - last!.Value).TotalHours);
            if (nowBac <= 0.005) return null;
            var soberAt = now.AddHours(nowBac / Burn).ToLocalTime();
            return (nowBac, soberAt);
        }
    }

    /// <summary>
    /// Ruční oprava místa (ťuknutí na „📍 …“ na lístku): appka se spletla, sedím jinde.
    /// Platí jen na tomhle místě: když odejdu dál než 50 m, nebo se ocitnu u jiné hospody, než kterou appka poznala
    /// při opravě, poznává se zase samo. Jen v telefonu / prohlížeči.
    /// </summary>
    public static class PlaceOverride
    {
        private const string Key = "place_override";
        public const double KeepMeters = 50;

        private sealed record Data(string? Place, string? PubId, string? PubName, double? PubLat, double? PubLon,
            string? TrhId, double Lat, double Lon, string? DetectedPubId, DateTime Day);

        private static Data? Load()
        {
            try
            {
                var json = Preferences.Get(Key, null as string);
                return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Data>(json);
            }
            catch { return null; }
        }

        public static bool IsSet => Load() != null;

        /// <summary>Zapamatovat opravu. me = kde teď jsem, detected = co appka poznala sama (to oprava přebíjí).</summary>
        public static void Set(PlaceInfo place, string? trhId, (double Lat, double Lon) me, string? detectedPubId, DateTime day)
        {
            try
            {
                Preferences.Set(Key, JsonSerializer.Serialize(new Data(place.Place, place.PubId, place.PubName, place.Lat, place.Lon,
                    trhId, me.Lat, me.Lon, detectedPubId, day.Date)));
            }
            catch { }
        }

        public static void Clear()
        {
            try { Preferences.Remove(Key); } catch { }
        }

        /// <summary>
        /// Opravené místo pro zápis, nebo null (oprava neplatí – jiný den, odešel jsem, jsem u jiné hospody).
        /// me = poloha teď (null = nevím → oprava platí dál), detected = co appka poznala teď.
        /// </summary>
        public static (PlaceInfo Place, string? TrhId)? Apply(PlaceInfo detected, (double Lat, double Lon)? me, DateTime day)
        {
            if (Load() is not { } d) return null;
            var moved = me is { } m && PubCatalog.DistanceMeters(m.Lat, m.Lon, d.Lat, d.Lon) > KeepMeters;
            var otherPub = detected.PubId != null && detected.PubId != d.PubId && detected.PubId != d.DetectedPubId;
            if (d.Day != day.Date || moved || otherPub)
            {
                Clear();
                return null;
            }
            return (new PlaceInfo(d.Place, d.PubId, d.PubName, d.PubLat, d.PubLon), d.TrhId);
        }
    }

    /// <summary>Jedna volba v ruční opravě místa (Place null = zase poznávat samo).</summary>
    public sealed record PlaceChoice(string Label, PlaceInfo? Place, string? TrhId);

    /// <summary>Ruční oprava místa: nabídka (hospody okolo, trhy, doma, jinde) a uložení volby. Sdílené s webem.</summary>
    public static class PlaceChoices
    {
        /// <summary>Kde jsem a co nabídnout; null = poloha nejde zjistit.</summary>
        public static async Task<((double Lat, double Lon) Me, List<PlaceChoice> Choices)?> LoadAsync(DateTime day)
        {
            if (await PushService.LocateAsync() is not { } loc) return null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await PubCatalog.EnsureCellsAsync(PubCatalog.CellsNear(loc.Lat, loc.Lon), cts.Token);
            }
            catch { }
            await PubService.LoadOverlayAsync();

            var list = new List<PlaceChoice>();
            foreach (var (pub, m) in PubService.Around(loc.Lat, loc.Lon, 150, 4))
            {
                var name = PubService.NameOf(pub);
                list.Add(new($"{name} · {Trhy.DistanceText(m)}", new PlaceInfo(Place.Pub, pub.Id, name, pub.Lat, pub.Lon), null));
            }
            if (Events.Active && Trhy.All.FirstOrDefault(z => (Events.Preview || z.RunsOn(day)) && Trhy.Contains(z, loc.Lat, loc.Lon, 60)) is { } trh)
                list.Add(new($"Trhy: {trh.Name}", new PlaceInfo(Place.Other, null, trh.Name, loc.Lat, loc.Lon), trh.Id));
            list.Add(new("Doma", new PlaceInfo(Place.Home, null, null, loc.Lat, loc.Lon), null));
            list.Add(new("Jinde (venku)", new PlaceInfo(Place.Other, null, null, loc.Lat, loc.Lon), null));
            if (PlaceOverride.IsSet) list.Add(new("Poznávat samo", null, null));
            return ((loc.Lat, loc.Lon), list);
        }

        /// <summary>Uložit volbu: platí pro další zápisy tady a lístek ji hned ukáže.</summary>
        public static void Choose(PlaceChoice choice, (double Lat, double Lon) me, DateTime day)
        {
            if (AuthSession.Uid is not string uid) return;
            if (choice.Place is not { } place)
            {
                PlaceOverride.Clear();
                return;
            }
            var detected = PubService.Nearest(me.Lat, me.Lon, 60)?.Id;
            PlaceOverride.Set(place, choice.TrhId, me, detected, day);
            PushService.RememberPlace(uid, day, place);
        }
    }
}
