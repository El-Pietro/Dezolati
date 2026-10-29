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
        private static double Grams(int pivo, int vino, int panaky)
        {
            var beerAbv = BeerDegrees switch { 10 => 0.040, 11 => 0.045, _ => 0.050 };
            return pivo * 500 * beerAbv * 0.789 + vino * 200 * 0.12 * 0.789 + panaky * 40 * 0.40 * 0.789;
        }

        // ---------------- log dnešních zápisů (jen v telefonu) ----------------

        private record LogItem(DateTime At, DateTime Day, int Pivo, int Vino, int Panaky);

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
        public static void Log(string uid, DateTime day, DateTime utcAt, int pivo, int vino, int panaky)
        {
            try
            {
                var items = LoadLog(uid).Where(i => i.Day.Date >= day.Date.AddDays(-1)).ToList();
                items.Add(new LogItem(utcAt, day.Date, pivo, vino, panaky));
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
                var g = Grams(i.Pivo, i.Vino, i.Panaky);
                bac = Math.Max(0, bac + g / (WeightKg * r));
                last = i.At;
            }

            var nowBac = Math.Max(0, bac - Burn * (now - last!.Value).TotalHours);
            if (nowBac <= 0.005) return null;
            var soberAt = now.AddHours(nowBac / Burn).ToLocalTime();
            return (nowBac, soberAt);
        }
    }
}
