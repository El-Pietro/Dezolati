using System.Globalization;
using System.Text;
using static Dezolati.Services.SceneKit;

namespace Dezolati.Services
{
    // Obrázek výčepu pro čepování jako jednoduchý „scénář“ (cesty, přechody, ořezy, otočení).
    // Skládá se jednou tady a kreslí ho dva tenké převodníky: appka (Microsoft.Maui.Graphics)
    // a web (SVG). Plátno má vždy 360 × 400, převodník si ho jen zvětší.

    public abstract record SceneNode;

    /// <summary>Skupina: posun, otočení (stupně, po směru hodin), zvětšení, pak ořez cestou (v těch souřadnicích).</summary>
    public sealed record SGroup(double Tx, double Ty, double Rot, string? Clip, List<SceneNode> Children, double Scale = 1) : SceneNode;

    public sealed record SGradient(double X1, double Y1, double X2, double Y2, (double Offset, string Color)[] Stops);

    public sealed record SPath(string D,
        string? Fill = null, double FillOpacity = 1, SGradient? Gradient = null,
        string? Stroke = null, double StrokeWidth = 0, double StrokeOpacity = 1, bool Round = false) : SceneNode;

    public sealed record SText(double X, double Y, string Text, double Size, string Color) : SceneNode;

    /// <summary>Obrázek (vlastní výzdoba akce): klíč do úložiště obrázků platformy, volitelně ořez do kolečka.</summary>
    public sealed record SImage(string Key, double X, double Y, double W, double H, bool Circle = false) : SceneNode;

    public static class PourScene
    {
        public const double W = 360, H = 400;

        // ---- rozložení výčepu ----
        private const double CounterY = 330;          // deska pultu
        private const double TrayY = 318;             // vršek odkapávací mřížky
        private const double TowerX = 38, TowerW = 24, TowerTop = 70;
        private const double ArmY = 108, ArmH = 22;   // tělo kohoutu (z boku)
        private const double SpoutX = 172;            // osa hubičky
        private const double SpoutBottom = ArmY + ArmH + 16;

        // ---- krýgl (místní souřadnice: 0,0 = střed hrdla, y dolů) ----
        private const double Gh = 160;                // výška
        private const double TopHalf = 50, BotHalf = 45, Belly = 4;
        private const double WallT = 5;               // stěna
        private const double BaseT = 17;              // tlusté dno
        private static readonly double InnerBottom = Gh - BaseT;
        private static readonly double Cx = SpoutX + 16;
        private static readonly double MouthY = TrayY - Gh;

        private const string Ink = "#5A3418";
        private const string BeerHi = "#F2B230", BeerLo = "#B5650A";
        private const string Foam = "#FFF8E6";

        // poloviční šířka (vnější / vnitřní) v hloubce y – mírně bachratý krýgl
        private static double OuterHalf(double y) =>
            TopHalf + (BotHalf - TopHalf) * (y / Gh) + Belly * Math.Sin(Math.PI * Math.Clamp(y / Gh, 0, 1));
        private static double InnerHalf(double y) => OuterHalf(y) - WallT;

        public static List<SceneNode> Build(PourGame g)
        {
            var n = new List<SceneNode>();
            Backdrop(n);
            Counter(n);
            Tower(n);

            var tilt = g.Tilt;
            var pouring = g.State == PourGame.Phase.Pouring;
            var rad = tilt * Math.PI / 180;

            // stín pod krýglem (jen když stojí)
            if (tilt < 8)
                n.Add(new SPath(Ellipse(Cx, TrayY + 1, BotHalf + 8, 4), Fill: "#000000", FillOpacity: 0.22 * (1 - tilt / 8)));

            // hladiny (vodorovně ve světě, posun od hrdla dolů): od nejnižšího rohu vnitřku k hrdlu podle objemu
            var low = InnerBottom * Math.Cos(rad) + InnerHalf(InnerBottom) * Math.Sin(rad);
            double LevelY(double f) => low - low * Math.Min(f, 1.05);
            var beerY = LevelY(g.BeerFraction);
            var topY = LevelY(g.TopFraction);

            // proud: kde vstoupí do hrdla, kde narazí na stěnu, kde končí (hladina)
            var stream = pouring ? TraceStream(rad, MouthY + topY) : default;

            // --- krýgl: uvnitř proud a stékání po stěně (pod pivem) ---
            var level = new List<SceneNode>();   // vodorovně (otočené zpátky)
            var local = new List<SceneNode>();   // v souřadnicích krýglu
            if (pouring && stream.EndY > stream.EntryY)
                level.Add(Line(SpoutX - Cx, stream.EntryY - MouthY, SpoutX - Cx, stream.EndY - MouthY, BeerHi, 4.5));
            if (pouring && stream.Hit is (double hx, double hy))
            {
                // stéká po stěně až dolů (pod hladinou ho překryje pivo)
                var side = Math.Sign(hx == 0 ? -1 : hx);
                var sb = new StringBuilder($"M {F(hx)} {F(hy)}");
                for (var y = hy; y <= InnerBottom; y += 6)
                    sb.Append($" L {F(side * (InnerHalf(y) - 2.5))} {F(y)}");
                local.Add(new SPath(sb.ToString(), Stroke: BeerHi, StrokeWidth: 4, Round: true));
            }
            n.Add(new SGroup(Cx, MouthY, -tilt, null, Krygl(g.BeerFraction, g.TopFraction, tilt, level, local)));

            // proud nad hrdlem (mezi hubičkou a sklenicí)
            if (pouring)
                n.Add(Line(SpoutX, SpoutBottom - 2, SpoutX, Math.Min(stream.EntryY, stream.EndY), BeerHi, 5.5));

            Tap(n, pouring);
            return n;
        }

        /// <summary>
        /// Krýgl v místních souřadnicích (0,0 = střed hrdla, y dolů, výška 160): ucho, pivo, pěna, sklo.
        /// tilt = o kolik je krýgl nakloněný (hladiny zůstanou vodorovně). Použije ho i klouzání po pultu (zmenšený).
        /// </summary>
        public static List<SceneNode> Krygl(double beerFraction, double topFraction, double tilt = 0,
            List<SceneNode>? underLevel = null, List<SceneNode>? underLocal = null)
        {
            var rad = tilt * Math.PI / 180;
            var low = InnerBottom * Math.Cos(rad) + InnerHalf(InnerBottom) * Math.Sin(rad);
            double LevelY(double f) => low - low * Math.Min(f, 1.05);
            var beerY = LevelY(beerFraction);
            var topY = LevelY(topFraction);

            var glass = new List<SceneNode>();
            Handle(glass);
            var inside = new List<SceneNode>();
            if (underLevel is { Count: > 0 }) inside.Add(new SGroup(0, 0, tilt, null, underLevel));
            if (underLocal is { Count: > 0 }) inside.AddRange(underLocal);

            var liquid = new List<SceneNode>();
            if (beerFraction > 0.002)
            {
                liquid.Add(new SPath(Rect(-Gh, beerY, Gh * 2, Gh * 2), Gradient:
                    new SGradient(0, beerY, 0, beerY + Gh, new[] { (0.0, BeerHi), (1.0, BeerLo) })));
                // bublinky v pivu
                var rnd = new Random(11);
                for (var i = 0; i < 14; i++)
                {
                    var bx = -40 + rnd.NextDouble() * 80;
                    var by = beerY + 8 + rnd.NextDouble() * Math.Max(0, low - beerY - 10);
                    liquid.Add(new SPath(Ellipse(bx, by, 1.2, 1.2), Fill: "#FFF2C0", FillOpacity: 0.7));
                }
            }
            if (topY < beerY - 0.5)
            {
                liquid.Add(new SPath(Rect(-Gh, topY, Gh * 2, beerY - topY + 1), Fill: Foam));
                liquid.Add(new SPath($"M {F(-Gh)} {F(beerY)} L {F(Gh)} {F(beerY)}", Stroke: "#E9C87A", StrokeWidth: 1.5, StrokeOpacity: 0.8));
                var rnd = new Random(7);
                for (var i = 0; i < 22; i++)
                {
                    var bx = -48 + rnd.NextDouble() * 96;
                    var by = topY + rnd.NextDouble() * (beerY - topY);
                    liquid.Add(new SPath(Ellipse(bx, by, 1.4 + rnd.NextDouble() * 2, 1.4 + rnd.NextDouble() * 2),
                        Stroke: "#E6D6B0", StrokeWidth: 0.9));
                }
            }
            inside.Add(new SGroup(0, 0, tilt, null, liquid));
            glass.Add(new SGroup(0, 0, 0, InteriorPath(), inside));
            GlassBody(glass);
            return glass;
        }

        /// <summary>Výška krýglu v místních souřadnicích (kvůli zmenšení).</summary>
        public const double KryglHeight = Gh;

        // ---------------- proud ----------------

        private record struct Stream(double EntryY, double EndY, (double X, double Y)? Hit);

        /// <summary>Svislý proud z hubičky: převod do souřadnic krýglu a hledání vstupu do hrdla a nárazu na stěnu.</summary>
        private static Stream TraceStream(double rad, double surfaceWorldY)
        {
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            (double X, double Y) Local(double wy)
            {
                double dx = SpoutX - Cx, dy = wy - MouthY;
                return (dx * cos - dy * sin, dx * sin + dy * cos);   // otočení o +náklon
            }
            double entry = double.NaN;
            for (var wy = SpoutBottom; wy < H; wy += 0.5)
            {
                var (x, y) = Local(wy);
                if (double.IsNaN(entry))
                {
                    if (y >= 0 && Math.Abs(x) < InnerHalf(0)) entry = wy;
                    continue;
                }
                if (wy >= surfaceWorldY) return new Stream(entry, surfaceWorldY, null);
                if (y >= InnerBottom || Math.Abs(x) >= InnerHalf(y) - 2)
                    return new Stream(entry, wy, (x, y));
            }
            return new Stream(double.IsNaN(entry) ? SpoutBottom : entry, double.IsNaN(entry) ? SpoutBottom : surfaceWorldY, null);
        }

        // ---------------- výčep ----------------

        private static void Backdrop(List<SceneNode> n)
        {
            // obkladačky za výčepem
            n.Add(new SPath(Rect(0, 0, W, CounterY), Gradient: new SGradient(0, 0, 0, CounterY,
                new[] { (0.0, "#EFE6D2"), (1.0, "#E2D3B4") })));
            var sb = new StringBuilder();
            for (var y = 30.0; y < CounterY; y += 30) sb.Append($"M 0 {F(y)} L {F(W)} {F(y)} ");
            var row = 0;
            for (var y = 0.0; y < CounterY; y += 30, row++)
                for (var x = row % 2 == 0 ? 0.0 : 22.0; x < W; x += 44)
                    sb.Append($"M {F(x)} {F(y)} L {F(x)} {F(Math.Min(CounterY, y + 30))} ");
            n.Add(new SPath(sb.ToString(), Stroke: "#C9B48C", StrokeWidth: 1, StrokeOpacity: 0.55));
        }

        private static void Counter(List<SceneNode> n)
        {
            // čelo pultu – prkna
            n.Add(new SPath(Rect(0, CounterY + 12, W, H - CounterY - 12), Gradient: new SGradient(0, CounterY, 0, H,
                new[] { (0.0, "#6B3E1E"), (1.0, "#3E2210") })));
            var planks = new StringBuilder();
            for (var x = 30.0; x < W; x += 60) planks.Append($"M {F(x)} {F(CounterY + 12)} L {F(x)} {F(H)} ");
            n.Add(new SPath(planks.ToString(), Stroke: "#2A160A", StrokeWidth: 1.5, StrokeOpacity: 0.6));
            // deska
            n.Add(new SPath(Rect(0, CounterY, W, 14), Gradient: new SGradient(0, CounterY, 0, CounterY + 14,
                new[] { (0.0, "#A8703C"), (1.0, "#6E4320") })));
            n.Add(new SPath($"M 0 {F(CounterY + 0.8)} L {F(W)} {F(CounterY + 0.8)}", Stroke: "#D9A56A", StrokeWidth: 1.5));
            // odkapávací mřížka
            n.Add(new SPath(Rect(104, TrayY, 196, CounterY - TrayY + 1), Gradient: new SGradient(0, TrayY, 0, CounterY,
                new[] { (0.0, "#E9ECEE"), (1.0, "#8D9499") }), Stroke: "#5D6468", StrokeWidth: 1));
            var grill = new StringBuilder();
            for (var x = 112.0; x < 296; x += 8) grill.Append($"M {F(x)} {F(TrayY + 2)} L {F(x)} {F(CounterY - 2)} ");
            n.Add(new SPath(grill.ToString(), Stroke: "#5D6468", StrokeWidth: 1.2, StrokeOpacity: 0.7));
        }

        private static readonly (double, string)[] ChromeStops =
            { (0.0, "#6E7478"), (0.35, "#F2F4F5"), (0.55, "#C9CED1"), (1.0, "#757B7F") };

        private static void Tower(List<SceneNode> n)
        {
            // sloup z pultu
            n.Add(new SPath(RoundRect(TowerX, TowerTop, TowerW, CounterY - TowerTop, 6),
                Gradient: new SGradient(TowerX, 0, TowerX + TowerW, 0, ChromeStops), Stroke: "#4E5458", StrokeWidth: 1.2));
            // patka
            n.Add(new SPath(RoundRect(TowerX - 8, CounterY - 8, TowerW + 16, 9, 3),
                Gradient: new SGradient(TowerX - 8, 0, TowerX + TowerW + 8, 0, ChromeStops), Stroke: "#4E5458", StrokeWidth: 1.2));
            // čepička
            n.Add(new SPath(Ellipse(TowerX + TowerW / 2, TowerTop, TowerW / 2 + 3, 5),
                Gradient: new SGradient(TowerX - 3, 0, TowerX + TowerW + 3, 0, ChromeStops), Stroke: "#4E5458", StrokeWidth: 1.2));
        }

        private static void Tap(List<SceneNode> n, bool open)
        {
            // rameno ze sloupu ke kohoutu
            var armL = TowerX + TowerW - 2;
            n.Add(new SPath(RoundRect(armL, ArmY + 4, SpoutX - 18 - armL, ArmH - 8, 4),
                Gradient: new SGradient(0, ArmY + 4, 0, ArmY + ArmH - 4, ChromeStops), Stroke: "#4E5458", StrokeWidth: 1.2));
            // tělo kohoutu (z boku: válec se zaobleným čelem)
            var bodyL = SpoutX - 24;
            n.Add(new SPath($"M {F(bodyL)} {F(ArmY)} L {F(SpoutX + 6)} {F(ArmY)} " +
                            $"C {F(SpoutX + 20)} {F(ArmY)} {F(SpoutX + 20)} {F(ArmY + ArmH)} {F(SpoutX + 6)} {F(ArmY + ArmH)} " +
                            $"L {F(bodyL)} {F(ArmY + ArmH)} Z",
                Gradient: new SGradient(0, ArmY, 0, ArmY + ArmH, ChromeStops), Stroke: "#4E5458", StrokeWidth: 1.2));
            // hubička dolů
            n.Add(new SPath($"M {F(SpoutX - 6)} {F(ArmY + ArmH - 1)} L {F(SpoutX + 6)} {F(ArmY + ArmH - 1)} " +
                            $"L {F(SpoutX + 4)} {F(SpoutBottom)} L {F(SpoutX - 4)} {F(SpoutBottom)} Z",
                Gradient: new SGradient(SpoutX - 6, 0, SpoutX + 6, 0, ChromeStops), Stroke: "#4E5458", StrokeWidth: 1.2));
            // páka: zavřená kolmo, otevřená přitažená k sobě
            var lever = new List<SceneNode>
            {
                new SPath(RoundRect(-5, -10, 10, 12, 2), Gradient: new SGradient(-5, 0, 5, 0, ChromeStops), Stroke: "#4E5458", StrokeWidth: 1),
                new SPath($"M -4 -10 L -6 -58 C -6 -66 6 -66 6 -58 L 4 -10 Z",
                    Gradient: new SGradient(-6, 0, 6, 0, new[] { (0.0, "#111111"), (0.4, "#4A4A4A"), (1.0, "#141414") }),
                    Stroke: "#000000", StrokeWidth: 1),
                new SPath("M -2 -16 L -3 -56", Stroke: "#FFFFFF", StrokeWidth: 1.5, StrokeOpacity: 0.35, Round: true)
            };
            n.Add(new SGroup(SpoutX - 8, ArmY + 2, open ? -32 : 0, null, lever));
        }

        // ---------------- krýgl ----------------

        private static string OuterPath()
        {
            // bachratý krýgl se zaoblenými rohy dna
            var r = 10.0;
            return $"M {F(-TopHalf)} 0 " +
                   $"C {F(-OuterHalf(Gh * 0.35) - 1.5)} {F(Gh * 0.33)} {F(-OuterHalf(Gh * 0.7) - 1)} {F(Gh * 0.66)} {F(-BotHalf)} {F(Gh - r)} " +
                   $"Q {F(-BotHalf)} {F(Gh)} {F(-BotHalf + r)} {F(Gh)} " +
                   $"L {F(BotHalf - r)} {F(Gh)} Q {F(BotHalf)} {F(Gh)} {F(BotHalf)} {F(Gh - r)} " +
                   $"C {F(OuterHalf(Gh * 0.7) + 1)} {F(Gh * 0.66)} {F(OuterHalf(Gh * 0.35) + 1.5)} {F(Gh * 0.33)} {F(TopHalf)} 0 Z";
        }

        private static string InteriorPath()
        {
            var sb = new StringBuilder($"M {F(-InnerHalf(0))} 0");
            for (var y = 6.0; y <= InnerBottom - 6; y += 6) sb.Append($" L {F(-InnerHalf(y))} {F(y)}");
            var b = InnerHalf(InnerBottom);
            sb.Append($" Q {F(-b)} {F(InnerBottom)} {F(-b + 7)} {F(InnerBottom)} L {F(b - 7)} {F(InnerBottom)} Q {F(b)} {F(InnerBottom)} {F(b)} {F(InnerBottom - 6)}");
            for (var y = InnerBottom - 6; y >= 6; y -= 6) sb.Append($" L {F(InnerHalf(y))} {F(y)}");
            // kopeček pěny nad okrajem
            sb.Append($" L {F(InnerHalf(0))} 0 C {F(InnerHalf(0) * 0.5)} {F(-Gh * 0.08)} {F(-InnerHalf(0) * 0.5)} {F(-Gh * 0.08)} {F(-InnerHalf(0))} 0 Z");
            return sb.ToString();
        }

        private static void Handle(List<SceneNode> glass)
        {
            var d = $"M {F(TopHalf - 2)} {F(Gh * 0.16)} C {F(TopHalf + 46)} {F(Gh * 0.1)} {F(TopHalf + 50)} {F(Gh * 0.78)} {F(BotHalf + 2)} {F(Gh * 0.8)}";
            glass.Add(new SPath(d, Stroke: Ink, StrokeWidth: 15, Round: true));
            glass.Add(new SPath(d, Stroke: "#DCEBEE", StrokeWidth: 11, Round: true));
            glass.Add(new SPath(d, Stroke: "#FFFFFF", StrokeWidth: 3, StrokeOpacity: 0.8, Round: true));
        }

        private static void GlassBody(List<SceneNode> glass)
        {
            var outer = OuterPath();
            // nádech skla přes všechno
            glass.Add(new SPath(outer, Fill: "#E8F3F5", FillOpacity: 0.1));
            // tlusté dno
            var b = InnerHalf(InnerBottom);
            glass.Add(new SPath($"M {F(-b)} {F(InnerBottom - 6)} Q {F(-b)} {F(InnerBottom)} {F(-b + 7)} {F(InnerBottom)} L {F(b - 7)} {F(InnerBottom)} " +
                                $"Q {F(b)} {F(InnerBottom)} {F(b)} {F(InnerBottom - 6)} L {F(BotHalf)} {F(Gh - 10)} Q {F(BotHalf)} {F(Gh)} {F(BotHalf - 10)} {F(Gh)} " +
                                $"L {F(-BotHalf + 10)} {F(Gh)} Q {F(-BotHalf)} {F(Gh)} {F(-BotHalf)} {F(Gh - 10)} Z",
                Gradient: new SGradient(0, InnerBottom, 0, Gh, new[] { (0.0, "#F4FAFB"), (1.0, "#B9D2D7") }), FillOpacity: 0.85));
            // důlky (krýgl) – ořezané tělem
            var dimples = new List<SceneNode>();
            for (var row = 0; row < 4; row++)
            {
                var y = 30 + row * 30.0;
                for (var col = -2; col <= 2; col++)
                {
                    var x = col * 22.0 + (row % 2 == 0 ? 0 : 11);
                    if (Math.Abs(x) > InnerHalf(y) - 6) continue;
                    var squeeze = Math.Cos(x / OuterHalf(y) * 1.1);   // k okrajům užší (je to válec)
                    var rx = 8.5 * Math.Max(0.35, squeeze);
                    dimples.Add(new SPath(Ellipse(x, y, rx, 12), Fill: "#FFFFFF", FillOpacity: 0.07,
                        Stroke: "#FFFFFF", StrokeWidth: 1.4, StrokeOpacity: 0.55));
                    dimples.Add(new SPath($"M {F(x - rx * 0.8)} {F(y + 6)} Q {F(x)} {F(y + 14)} {F(x + rx * 0.8)} {F(y + 6)}",
                        Stroke: Ink, StrokeWidth: 1, StrokeOpacity: 0.28, Round: true));
                }
            }
            glass.Add(new SGroup(0, 0, 0, outer, dimples));
            // lesky
            glass.Add(new SPath($"M {F(-TopHalf + 9)} 8 C {F(-TopHalf + 5)} 50 {F(-TopHalf + 6)} 100 {F(-BotHalf + 10)} {F(Gh - 22)}",
                Stroke: "#FFFFFF", StrokeWidth: 4, StrokeOpacity: 0.6, Round: true));
            glass.Add(new SPath($"M {F(TopHalf - 10)} 14 C {F(TopHalf - 7)} 40 {F(TopHalf - 7)} 60 {F(TopHalf - 9)} 80",
                Stroke: "#FFFFFF", StrokeWidth: 2, StrokeOpacity: 0.5, Round: true));
            // cejch
            var markY = InnerBottom - InnerBottom * (PourGame.Mark / PourGame.Rim);
            glass.Add(new SPath($"M {F(-InnerHalf(markY) + 3)} {F(markY)} L {F(-InnerHalf(markY) + 20)} {F(markY)}", Stroke: Ink, StrokeWidth: 1.5));
            glass.Add(new SText(-InnerHalf(markY) + 23, markY + 4, "0,5 l", 10, Ink));
            // obrys a okraj
            glass.Add(new SPath(outer, Stroke: Ink, StrokeWidth: 2.2));
            glass.Add(new SPath($"M {F(-TopHalf)} 0 L {F(TopHalf)} 0", Stroke: Ink, StrokeWidth: 2.2));
            glass.Add(new SPath($"M {F(-TopHalf + 1)} 2.5 L {F(TopHalf - 1)} 2.5", Stroke: "#FFFFFF", StrokeWidth: 1.5, StrokeOpacity: 0.7));
        }

    }

    /// <summary>Pomocníci pro scénáře (cesty jako SVG data) a převod do SVG pro web.</summary>
    public static class SceneKit
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static string F(double v) => v.ToString("0.##", Inv);

        /// <summary>Web: odkaz (data URL) obrázku pro <see cref="SImage"/>; null = obrázek není, nekreslí se.</summary>
        public static Func<string, string?>? ImageHref { get; set; }

        private static int _svgIds;

        // ---------------- pomocníci ----------------

        public static SPath Line(double x1, double y1, double x2, double y2, string color, double width) =>
            new($"M {F(x1)} {F(y1)} L {F(x2)} {F(y2)}", Stroke: color, StrokeWidth: width, Round: true);

        public static string Rect(double x, double y, double w, double h) =>
            $"M {F(x)} {F(y)} L {F(x + w)} {F(y)} L {F(x + w)} {F(y + h)} L {F(x)} {F(y + h)} Z";

        public static string RoundRect(double x, double y, double w, double h, double r)
        {
            r = Math.Min(r, Math.Min(w, h) / 2);
            return $"M {F(x + r)} {F(y)} L {F(x + w - r)} {F(y)} Q {F(x + w)} {F(y)} {F(x + w)} {F(y + r)} " +
                   $"L {F(x + w)} {F(y + h - r)} Q {F(x + w)} {F(y + h)} {F(x + w - r)} {F(y + h)} " +
                   $"L {F(x + r)} {F(y + h)} Q {F(x)} {F(y + h)} {F(x)} {F(y + h - r)} " +
                   $"L {F(x)} {F(y + r)} Q {F(x)} {F(y)} {F(x + r)} {F(y)} Z";
        }

        public static string Ellipse(double cx, double cy, double rx, double ry)
        {
            const double k = 0.5523;
            return $"M {F(cx - rx)} {F(cy)} " +
                   $"C {F(cx - rx)} {F(cy - ry * k)} {F(cx - rx * k)} {F(cy - ry)} {F(cx)} {F(cy - ry)} " +
                   $"C {F(cx + rx * k)} {F(cy - ry)} {F(cx + rx)} {F(cy - ry * k)} {F(cx + rx)} {F(cy)} " +
                   $"C {F(cx + rx)} {F(cy + ry * k)} {F(cx + rx * k)} {F(cy + ry)} {F(cx)} {F(cy + ry)} " +
                   $"C {F(cx - rx * k)} {F(cy + ry)} {F(cx - rx)} {F(cy + ry * k)} {F(cx - rx)} {F(cy)} Z";
        }

        // ---------------- web: SVG ----------------

        /// <summary>Obsah &lt;svg viewBox="0 0 360 400"&gt; (defs + kresba).</summary>
        public static string ToSvg(List<SceneNode> nodes)
        {
            var defs = new StringBuilder();
            var body = new StringBuilder();
            // id ořezů a přechodů jedinečná v celé stránce (víc SVG vedle sebe by si jinak sahalo na cizí dzc0)
            var id = System.Threading.Interlocked.Add(ref _svgIds, 10000);
            void Emit(SceneNode node)
            {
                switch (node)
                {
                    case SGroup gr:
                        var attrs = new StringBuilder();
                        if (gr.Tx != 0 || gr.Ty != 0 || gr.Rot != 0 || gr.Scale != 1)
                            attrs.Append($" transform=\"translate({F(gr.Tx)} {F(gr.Ty)}) rotate({F(gr.Rot)})" + (gr.Scale != 1 ? $" scale({F(gr.Scale)})" : "") + "\"");
                        if (gr.Clip != null)
                        {
                            var cid = $"dzc{id++}";
                            defs.Append($"<clipPath id=\"{cid}\" clipPathUnits=\"userSpaceOnUse\"><path d=\"{gr.Clip}\"/></clipPath>");
                            // ořez musí být v souřadnicích PO otočení → vnořená skupina
                            body.Append($"<g{attrs}><g clip-path=\"url(#{cid})\">");
                            foreach (var c in gr.Children) Emit(c);
                            body.Append("</g></g>");
                        }
                        else
                        {
                            body.Append($"<g{attrs}>");
                            foreach (var c in gr.Children) Emit(c);
                            body.Append("</g>");
                        }
                        break;
                    case SPath p:
                        string fill;
                        if (p.Gradient is { } gd)
                        {
                            var gid = $"dzg{id++}";
                            defs.Append($"<linearGradient id=\"{gid}\" gradientUnits=\"userSpaceOnUse\" x1=\"{F(gd.X1)}\" y1=\"{F(gd.Y1)}\" x2=\"{F(gd.X2)}\" y2=\"{F(gd.Y2 == gd.Y1 && gd.X2 == gd.X1 ? gd.Y1 + 1 : gd.Y2)}\">");
                            foreach (var (o, col) in gd.Stops) defs.Append($"<stop offset=\"{F(o)}\" stop-color=\"{col}\"/>");
                            defs.Append("</linearGradient>");
                            fill = $"url(#{gid})";
                        }
                        else fill = p.Fill ?? "none";
                        body.Append($"<path d=\"{p.D}\" fill=\"{fill}\"");
                        if (fill != "none" && p.FillOpacity < 1) body.Append($" fill-opacity=\"{F(p.FillOpacity)}\"");
                        if (p.Stroke != null)
                        {
                            body.Append($" stroke=\"{p.Stroke}\" stroke-width=\"{F(p.StrokeWidth)}\" stroke-linejoin=\"round\"");
                            if (p.StrokeOpacity < 1) body.Append($" stroke-opacity=\"{F(p.StrokeOpacity)}\"");
                            if (p.Round) body.Append(" stroke-linecap=\"round\"");
                        }
                        body.Append("/>");
                        break;
                    case SText t:
                        body.Append($"<text x=\"{F(t.X)}\" y=\"{F(t.Y)}\" font-size=\"{F(t.Size)}\" fill=\"{t.Color}\">{t.Text}</text>");
                        break;
                    case SImage im when ImageHref?.Invoke(im.Key) is string href:
                        var clip = "";
                        if (im.Circle)
                        {
                            var cid = $"dzc{id++}";
                            defs.Append($"<clipPath id=\"{cid}\" clipPathUnits=\"userSpaceOnUse\"><path d=\"{Ellipse(im.X + im.W / 2, im.Y + im.H / 2, im.W / 2, im.H / 2)}\"/></clipPath>");
                            clip = $" clip-path=\"url(#{cid})\"";
                        }
                        body.Append($"<image href=\"{href}\" x=\"{F(im.X)}\" y=\"{F(im.Y)}\" width=\"{F(im.W)}\" height=\"{F(im.H)}\" preserveAspectRatio=\"xMidYMid slice\"{clip}/>");
                        break;
                }
            }
            foreach (var node in nodes) Emit(node);
            return $"<defs>{defs}</defs>{body}";
        }
    }
}
