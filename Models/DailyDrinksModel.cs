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

        public int Total => Pivo + Vino + Panaky;
    }

    /// <summary>Statistiky jednoho uživatele.</summary>
    public class UserStats
    {
        /// <summary>Aktuální hospodský den daného uživatele.</summary>
        public DateTime Today { get; set; }
        /// <summary>Součty přes celou historii (agregace na serveru).</summary>
        public DailyDrinksModel Totals { get; set; } = new();
        /// <summary>Počet dní, kdy pil.</summary>
        public int Days { get; set; }
        /// <summary>Rekordní den (null = nikdy).</summary>
        public DailyDrinksModel? Record { get; set; }
        /// <summary>Posledních 30 dní, nejnovější první.</summary>
        public List<DailyDrinksModel> Last30 { get; set; } = new();
    }
}
