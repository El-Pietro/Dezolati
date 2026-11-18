namespace Dezolati.Models
{
    public class DailyDrinksModel
    {
        public string Id { get; set; } = "";
        public string UserId { get; set; } = "";
        /// <summary>Hospodský den ve formátu dd-MM-yyyy (starý formát, pro kompatibilitu).</summary>
        public string Date { get; set; } = "";
        /// <summary>Hospodský den (v DB jako "Day" = yyyy-MM-dd, dá se podle něj řadit).</summary>
        public DateTime Day { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public int Pivo { get; set; }
        public int Vino { get; set; }
        public int Panaky { get; set; }
        /// <summary>Míchané drinky (od 3.12).</summary>
        public int Michacky { get; set; }
        /// <summary>Panáky podle druhu (od 3.12). Ostatní = Panaky − tyhle (i všechno starší).</summary>
        public int PanakSlivovice { get; set; }
        public int PanakLiker { get; set; }
        public int PanakRum { get; set; }
        public int PanakVodka { get; set; }
        public int PanakWhisky { get; set; }
        public int PanakOstatni => Math.Max(0, Panaky - PanakSlivovice - PanakLiker - PanakRum - PanakVodka - PanakWhisky);
        /// <summary>Útrata v Kč (null = den z doby před sledováním útraty a bez odhadu).</summary>
        public int? Kc { get; set; }
        /// <summary>Útrata je jen zpětný odhad podle výchozích cen.</summary>
        public bool KcEstimate { get; set; }
        /// <summary>Kousky podle místa (od verze 3.7; zbytek = nevím).</summary>
        public int PlacePub { get; set; }
        public int PlaceHome { get; set; }
        public int PlaceOther { get; set; }

        public int Total => Pivo + Vino + Panaky + Michacky;
    }

    /// <summary>Jedna hospoda ve statistice „kde nejčastěji piju“ (UserPubStats/{uid}).</summary>
    public sealed record PubStat(string PubId, string? Name, int Count, int Visits, int Kc, DateTime? LastAt);

    /// <summary>Statistiky jednoho uživatele.</summary>
    public class UserStats
    {
        /// <summary>Aktuální hospodský den daného uživatele.</summary>
        public DateTime Today { get; set; }
        /// <summary>Součty přes celou historii (agregace na serveru).</summary>
        public DailyDrinksModel Totals { get; set; } = new();
        /// <summary>Den prvního zápisu (null = nic / index se ještě staví).</summary>
        public DateTime? FirstDay { get; set; }
        /// <summary>Počet dní, kdy pil.</summary>
        public int Days { get; set; }
        /// <summary>Rekordní den (null = nikdy).</summary>
        public DailyDrinksModel? Record { get; set; }
        /// <summary>Posledních 30 dní, nejnovější první.</summary>
        public List<DailyDrinksModel> Last30 { get; set; } = new();
        /// <summary>Hospoda posledního zápisu (jen když ji dotyčný hlásí kámošům).</summary>
        public string? CurrentPubName { get; set; }
        /// <summary>Kdy tam zapsal naposledy (UTC).</summary>
        public DateTime? CurrentPubAt { get; set; }
        /// <summary>Útrata za celou historii (jen dny, kde je známá).</summary>
        public long TotalKc { get; set; }
        /// <summary>Kousky podle místa za celou historii.</summary>
        public long TotalPlacePub { get; set; }
        public long TotalPlaceHome { get; set; }
        public long TotalPlaceOther { get; set; }
        /// <summary>Hospody, nejčastější první.</summary>
        public List<PubStat> Pubs { get; set; } = new();
        /// <summary>Oblíbené hospody (z profilu).</summary>
        public Dictionary<string, FavoritePub> FavoritePubs { get; set; } = new();
        /// <summary>Dotyčný má pro mě zapnutý tajný režim → čísla jsou přepočtená (třetina), nic „kdo teď pije“.</summary>
        public bool Masked { get; set; }
        /// <summary>Odhad promile uložený při jeho posledním zápisu (null = nesdílí / střízlivý).</summary>
        public double? PromileValue { get; set; }
        public DateTime? PromileAt { get; set; }
        /// <summary>Pohlaví (kvůli „střízlivý / střízlivá“).</summary>
        public string? Gender { get; set; }
        /// <summary>Poslední zápis (UTC), čeho v něm bylo nejvíc (pivo / vino / panak) a kde (jen když místo hlásí).</summary>
        public DateTime? LastDrinkAt { get; set; }
        public string? LastDrinkKind { get; set; }
        public string? LastDrinkPlace { get; set; }
        /// <summary>Hospoda posledního zápisu na mapě (jen když ji hlásí): id a poloha – ťuk na „Naposledy“ otevře mapu.</summary>
        public string? LastPubId { get; set; }
        public double? LastPubLat { get; set; }
        public double? LastPubLon { get; set; }

        /// <summary>„před 3 h – pivo, U Lojzy“ / „včera 23:40 – víno“ / „12. 10. 21:15 – panák“ (appka i web).</summary>
        public string LastDrinkText()
        {
            if (LastDrinkAt is not DateTime atUtc) return "—";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var at = atUtc.ToLocalTime();
            var now = DateTime.Now;
            var ago = now - at;
            string when = ago.TotalMinutes < 2 ? "právě teď"
                : ago.TotalMinutes < 60 ? $"před {(int)ago.TotalMinutes} min"
                : ago.TotalHours < 12 ? $"před {(int)ago.TotalHours} h"
                : at.Date == now.Date ? "dnes " + at.ToString("H:mm", inv)
                : at.Date == now.Date.AddDays(-1) ? "včera " + at.ToString("H:mm", inv)
                : at.Year == now.Year ? at.ToString("d. M. H:mm", inv)
                : at.ToString("d. M. yyyy", inv);
            var what = LastDrinkKind switch { "pivo" => "pivo", "vino" => "víno", "panak" => "panák", "michacka" => "míchačka", _ => null };
            var tail = string.Join(", ", new[] { what, LastDrinkPlace }.Where(p => !string.IsNullOrWhiteSpace(p)));
            return tail.Length == 0 ? when : $"{when} – {tail}";
        }
    }
}
