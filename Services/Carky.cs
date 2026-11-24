namespace Dezolati.Services
{
    /// <summary>
    /// Čárky na lístku jako tahy propiskou (vektor, žádné obrázky) – appka je kreslí přes CarkyDrawable,
    /// web do SVG, takže vypadají všude stejně. Stejný den a druh = pořád stejně křivé čárky; zítra trochu jinak.
    /// Pětice: čtyři svislé čárky a pátá šikmo přes ně.
    /// </summary>
    public static class TallyScene
    {
        public const string Ink = "#16237E";          // modrá propiska
        private const double MarkH = 32, Step = 12, GroupW = 72, RowH = 42, Top = 4, Left = 4;   // menší, ať se lístek vejde na displej

        /// <summary>Kolik pětek se vejde na řádek dané šířky.</summary>
        public static int GroupsPerRow(double width) => Math.Max(1, (int)((width - Left - 60) / GroupW) + 1);

        /// <summary>Výška kresby pro daný počet čárek a šířku.</summary>
        public static double HeightFor(int count, double width)
        {
            if (count <= 0) return 30;
            var groups = (count + 4) / 5;
            var perRow = GroupsPerRow(width);
            return (groups + perRow - 1) / perRow * RowH + 2;
        }

        public static List<SceneNode> Build(int count, double width, DateTime day, int kind)
        {
            var nodes = new List<SceneNode>();
            var perRow = GroupsPerRow(width);
            for (var i = 0; i < count; i++)
            {
                var rnd = new Random(day.Year * 1000 + day.DayOfYear + kind * 7919 + i * 104729);
                double J(double a) => (rnd.NextDouble() * 2 - 1) * a;
                var g = i / 5;
                var gx = Left + (g % perRow) * GroupW;
                var gy = Top + (g / perRow) * RowH;

                string d;
                if (i % 5 == 4)
                {
                    // pátá přes předchozí čtyři: zleva dole doprava nahoru, lehce prohnutá
                    var y1 = gy + MarkH * 0.68 + J(2.5);
                    var y2 = gy + MarkH * 0.30 + J(2.5);
                    d = $"M {F(gx - 4 + J(1.5))} {F(y1)} C {F(gx + 14)} {F(y1 - 3 + J(2.5))} {F(gx + 35)} {F(y2 + 5 + J(2.5))} {F(gx + 58 + J(2.5))} {F(y2)}";
                }
                else
                {
                    // svislá čárka: malý háček na začátku, pak lehce zvlněný tah dolů
                    var x = gx + (i % 5) * Step + J(1.2);
                    var y = gy + J(2);
                    var h = MarkH + J(3);
                    var lean = J(3);
                    d = $"M {F(x - 1.2 + J(0.6))} {F(y + 2.2)} L {F(x)} {F(y)} " +
                        $"C {F(x + J(2.5) + lean * 0.3)} {F(y + h * 0.35)} {F(x + J(2.5) + lean * 0.7)} {F(y + h * 0.7)} {F(x + lean)} {F(y + h)}";
                }
                nodes.Add(new SPath(d, Stroke: Ink, StrokeWidth: 2.6, StrokeOpacity: 0.92, Round: true));
                // druhý tenký tah posunutý o kousek = propiska nepíše úplně rovnoměrně
                nodes.Add(new SGroup(0.8, 0.5, 0, null, new List<SceneNode>
                {
                    new SPath(d, Stroke: Ink, StrokeWidth: 1.2, StrokeOpacity: 0.45, Round: true)
                }));
            }
            return nodes;
        }

        private static string F(double v) => SceneKit.F(v);
    }
}
