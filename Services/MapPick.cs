namespace Dezolati.Services
{
    /// <summary>
    /// Výběr místa na mapě pro jinou stránku (místo akce): stránka zavolá <see cref="Begin"/>, otevře mapu v režimu „pick“
    /// (křížek uprostřed jako u domova), mapa uloží <see cref="Result"/> a vrátí se na <see cref="Back"/>.
    /// </summary>
    public static class MapPick
    {
        public static (double Lat, double Lon)? Start { get; private set; }
        public static (double Lat, double Lon)? Result { get; set; }
        public static string Hint { get; private set; } = "Posuň mapu, ať je křížek na správném místě";
        public static string Ok { get; private set; } = "Tady";
        public static string Back { get; private set; } = "";

        public static void Begin(double? lat, double? lon, string hint, string ok, string back)
        {
            Start = lat is double la && lon is double lo ? (la, lo) : null;
            Result = null;
            (Hint, Ok, Back) = (hint, ok, back);
        }

        public static (double Lat, double Lon)? Take()
        {
            var r = Result;
            Result = null;
            return r;
        }
    }
}
