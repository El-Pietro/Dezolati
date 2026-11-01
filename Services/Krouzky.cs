namespace Dezolati.Services
{
    /// <summary>Jedno mokré kolečko na lístku (souřadnice 0–1 vůči lístku).</summary>
    public sealed record WetRing(double X, double Y, double R, bool Wine, double Gap, double Opacity, double Width);

    /// <summary>
    /// Mokrá kolečka od půllitrů a sklenek na lístku a „šejdrové“ čárky – náhoda podle data, takže lístek
    /// vypadá celý den stejně a druhý den jinak. Sdílené appkou i webem (stejný den = stejný lístek).
    /// </summary>
    public static class Krouzky
    {
        /// <summary>Kolik koleček: přibývají s pitím (1 za každé 2 piva / vína), nejvýš 7.</summary>
        public static List<WetRing> For(DateTime day, int pivo, int vino)
        {
            var rnd = new Random(Seed(day));
            var beer = Math.Min(5, (pivo + 1) / 2);
            var wine = Math.Min(3, (vino + 1) / 2);
            var list = new List<WetRing>();
            for (var i = 0; i < beer + wine; i++)
            {
                var isWine = i >= beer;
                // piva: velký půllitr (širší kruh), víno: menší patka sklenky
                var r = isWine ? 0.075 + rnd.NextDouble() * 0.02 : 0.11 + rnd.NextDouble() * 0.03;
                // kolečka hlavně u okrajů a v rozích, ať nepřekrývají čárky úplně
                var x = rnd.NextDouble() < 0.5 ? 0.05 + rnd.NextDouble() * 0.3 : 0.65 + rnd.NextDouble() * 0.3;
                var y = 0.12 + rnd.NextDouble() * 0.8;
                list.Add(new WetRing(x, y, r, isWine,
                    Gap: rnd.NextDouble() * 360,
                    Opacity: 0.16 + rnd.NextDouble() * 0.12,
                    Width: isWine ? 0.010 : 0.014 + rnd.NextDouble() * 0.006));
            }
            return list;
        }

        /// <summary>Čárka „šejdrem“: natočení (°), posun dolů (px) a délka (násobek). Pátá (přeškrtnutí) se točí míň.</summary>
        public static (double Rotation, double Dy, double Scale) Jitter(DateTime day, int kind, int index)
        {
            var rnd = new Random(Seed(day) * 31 + kind * 1009 + index * 7919);
            var cross = index % 5 == 4;
            return (cross ? rnd.NextDouble() * 10 - 5 : rnd.NextDouble() * 14 - 7,
                    rnd.NextDouble() * 8 - 4,
                    0.9 + rnd.NextDouble() * 0.2);
        }

        private static int Seed(DateTime day) => day.Year * 1000 + day.DayOfYear;
    }
}
