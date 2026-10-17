namespace Dezolati.Models
{
    public class DailyDrinksModel
    {
        public string Id { get; set; } = "";
        public string UserId { get; set; } = "";
        /// <summary>Hospodský den ve formátu dd-MM-yyyy.</summary>
        public string Date { get; set; } = "";
        public DateTime Day { get; set; }
        public DateTime? UpdateAt { get; set; }
        public int Pivo { get; set; }
        public int Vino { get; set; }
        public int Panaky { get; set; }

        public int Total => Pivo + Vino + Panaky;
    }
}
