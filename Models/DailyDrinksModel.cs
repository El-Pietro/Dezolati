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
        /// <summary>Útrata v Kč (null = den z doby před sledováním útraty a bez odhadu).</summary>
        public int? Kc { get; set; }
        /// <summary>Útrata je jen zpětný odhad podle výchozích cen.</summary>
        public bool KcEstimate { get; set; }
        /// <summary>Kousky podle místa (od verze 3.7; zbytek = nevím).</summary>
        public int PlacePub { get; set; }
        public int PlaceHome { get; set; }
        public int PlaceOther { get; set; }

        public int Total => Pivo + Vino + Panaky;
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
    }
}
