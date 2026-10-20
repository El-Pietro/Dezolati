namespace Dezolati.Models
{
    /// <summary>Jeden zápis (jedno kliknutí na "Zapsat").</summary>
    public class DrinkModel
    {
        public string Id { get; set; } = "";
        public string UserId { get; set; } = "";
        public string? Name { get; set; }
        public int Pivo { get; set; }
        public int Vino { get; set; }
        public int Panaky { get; set; }
        public DateTime DrinkAt { get; set; }
    }
}
