using static Dezolati.Services.SceneKit;

namespace Dezolati.Services
{
    /// <summary>Cedulka eventu pod nadpisem lístku.</summary>
    public sealed record EventTag(string Theme, string Title, string Detail, int Candles = -1, bool Dark = false);

    /// <summary>
    /// Výzdoba lístku během eventu (Vánoce / Silvestr / Zima) jako sdílený scénář – appka ho kreslí přes
    /// SceneDrawable.Render, web přes SVG (statické části) a CSS (padající sníh, blikání, sekt, ohňostroj).
    /// Pohyb: funkce dostanou čas t v sekundách a spočítají polohu samy (žádný stav → jde to kreslit odkudkoli).
    /// </summary>
    public static class EventScene
    {
        // ---------------- barvy a texty ----------------

        /// <summary>Barva koleček od sklenic (svařák / sekt), null = obyčejná.</summary>
        public static string? RingColor(string? theme) => theme switch
        {
            "vanoce" => "#8E1B2A",
            "silvestr" => "#C9971F",
            _ => null
        };

        private static readonly string[] XmasBulbs = { "#E8433A", "#F2B632", "#3BA55C", "#3C8DDE" };
        private static readonly string[] NyeBulbs = { "#F2C94C", "#E8EDF2", "#D4A62A", "#E8EDF2" };
        private static readonly string[] WinterBulbs = { "#E8EDF2", "#BFD7EA", "#E8EDF2", "#9CC3E0" };

        public static string[] BulbColors(string? theme) => theme switch
        {
            "silvestr" => NyeBulbs,
            "zima" => WinterBulbs,
            _ => XmasBulbs
        };

        /// <summary>Jak rychle blikají světýlka (perioda v s).</summary>
        public static double BlinkPeriod(string? theme) => theme == "silvestr" ? 1.1 : 2.4;

        /// <summary>Cedulka pod nadpisem podle motivu a času.</summary>
        public static EventTag? Tag(string? theme, DateTime now)
        {
            switch (theme)
            {
                case "vanoce":
                    if (now.Month == 12 && now.Day >= 24) return new("vanoce", "Veselé Vánoce", "");
                    var lit = Events.AdventLit;
                    return new("vanoce", "Advent", lit == 0 ? "brzy" : $"{lit}/4", lit);
                case "silvestr":
                    if (now.Month == 1) return new("silvestr", $"Šťastný {now.Year}!", "", Dark: true);
                    var left = now.Date.AddDays(1) - now;
                    return new("silvestr", "Silvestr", $"do půlnoci {(int)left.TotalHours}:{left.Minutes:00}", Dark: true);
                case "zima":
                    if (now.Month == 1 && now.Day == 6) return new("zima", "Tři králové", "");
                    return new("zima", $"Šťastný {(now.Month == 1 ? now.Year : now.Year + 1)}", "");
                default:
                    return null;
            }
        }

        // ---------------- světýlka přes horní lištu (výška 46) ----------------

        public const double LightsHeight = 46;

        public static string Wire(double w)
        {
            // každý oblouk zvlášť (Q), ne „T“ – plátno v appce T neumí a kreslilo místo prověšení rovnou čáru;
            // T by navíc oblouky střídalo nahoru a dolů, kdežto žárovky visí vždycky dolů
            var sb = new System.Text.StringBuilder("M -5 8 L 0 10");
            for (var x = 0.0; x < w + 5; x += 98) sb.Append($" Q {F(x + 49)} 34 {F(x + 98)} 10");
            return sb.ToString();
        }

        /// <summary>Žárovky: střed, barva, zpoždění blikání (s).</summary>
        public static IEnumerable<(double X, double Y, string Color, double Delay)> Bulbs(double w, string? theme)
        {
            var colors = BulbColors(theme);
            var step = theme == "silvestr" ? 0.19 : 0.37;
            var i = 0;
            for (var x = 18.0; x < w - 6; x += 32, i++)
            {
                var y = 8 + 12 * Math.Abs(Math.Sin(x / 98 * Math.PI));
                yield return (x, y + 11, colors[i % colors.Length], i * step);
            }
        }

        public static List<SceneNode> Lights(double w, string? theme, double t)
        {
            var n = new List<SceneNode> { new SPath(Wire(w), Stroke: "#2F2A22", StrokeWidth: 1.6) };
            var period = BlinkPeriod(theme);
            foreach (var (x, y, color, delay) in Bulbs(w, theme))
            {
                var phase = ((t + delay) % period) / period;                  // 0..1
                var alpha = 0.45 + 0.55 * (0.5 + 0.5 * Math.Cos(phase * 2 * Math.PI));
                n.Add(new SPath(Rect(x - 2, y - 11, 4, 5), Fill: "#3A3A3A"));
                n.Add(new SPath(Ellipse(x, y, 4.5, 6.5), Fill: color, FillOpacity: alpha));
            }
            return n;
        }

        // ---------------- sněhová čepice přes horní okraj lístku ----------------

        /// <summary>Výška čepice; kreslí se od 12 px nad lístkem.</summary>
        public const double CapHeight = 34, CapOverhang = 12;

        public static List<SceneNode> Cap(double w)
        {
            var rnd = new Random(7);
            // horní okraj: hrbolky po ~44 px
            var segs = Math.Max(3, (int)Math.Round(w / 44));
            var sw = w / segs;
            var top = new System.Text.StringBuilder($"M 0 14");
            for (var i = 0; i < segs; i++)
            {
                var x0 = i * sw; var x1 = x0 + sw;
                var yEnd = i == segs - 1 ? 12 : 8 + rnd.NextDouble() * 4;
                top.Append($" C {F(x0 + sw * 0.25)} {F(2 + rnd.NextDouble() * 3)}, {F(x0 + sw * 0.7)} {F(1 + rnd.NextDouble() * 2)}, {F(x1)} {F(yEnd)}");
            }
            // spodní okraj zpátky: stékající cípy po ~34 px
            var drips = Math.Max(4, (int)Math.Round(w / 34));
            var dw = w / drips;
            var bottomPts = new List<(double X, double Y)>();
            for (var i = drips; i >= 0; i--)
                bottomPts.Add((i * dw, i % 3 == 1 ? 26 + rnd.NextDouble() * 4 : 18 + rnd.NextDouble() * 4));
            var bottom = new System.Text.StringBuilder($"M {F(bottomPts[0].X)} {F(bottomPts[0].Y)}");
            for (var i = 1; i < bottomPts.Count; i++)
            {
                var (px, py) = bottomPts[i - 1];
                var (x, y) = bottomPts[i];
                bottom.Append($" C {F(px - dw * 0.3)} {F(py + 4)}, {F(x + dw * 0.3)} {F(y + 4)}, {F(x)} {F(y)}");
            }
            // výplň: horní okraj, pravý roh dolů, spodní okraj zpátky (bez jeho úvodního „M“)
            var bottomBody = bottom.ToString();
            var fill = top + $" L {F(w)} {F(bottomPts[0].Y)} " + bottomBody[bottomBody.IndexOf('C')..] + " Z";

            var n = new List<SceneNode>
            {
                new SPath(fill, Fill: "#FFFFFF"),
                new SPath(bottom.ToString(), Stroke: "#C9D8E6", StrokeWidth: 2, Round: true)
            };
            // pár sněhových kuliček pod cípy
            foreach (var (x, y) in bottomPts.Where((p, i) => i % 3 == 1).Take(4))
                n.Add(new SPath(Ellipse(x, y + 3, 2.4, 2.4), Fill: "#FFFFFF"));
            return n;
        }

        // ---------------- roh lístku: jmelí (Vánoce) / sekt (Silvestr) ----------------

        public const double CornerW = 92, CornerH = 96;

        public static List<SceneNode> Mistletoe() => new()
        {
            new SPath("M 58 6 C 52 18 46 26 38 34", Stroke: "#5B6B2E", StrokeWidth: 2.4, Round: true),
            new SPath("M 50 18 C 34 8 18 12 12 20 C 24 26 40 26 50 18 Z", Fill: "#7C9A3C"),
            new SPath("M 50 18 C 36 14 22 15 14 20", Stroke: "#5B6B2E", StrokeWidth: 1),
            new SPath("M 44 28 C 46 44 58 54 70 54 C 70 42 60 30 44 28 Z", Fill: "#8FAE46"),
            new SPath("M 44 28 C 52 38 60 46 68 53", Stroke: "#5B6B2E", StrokeWidth: 1),
            new SPath("M 58 8 C 66 2 76 4 76 10", Stroke: "#B3262E", StrokeWidth: 5, Round: true),
            new SPath(Ellipse(38, 36, 4.6, 4.6), Fill: "#F4F1E6", Stroke: "#C9C2A8", StrokeWidth: 1),
            new SPath(Ellipse(33, 40, 4.2, 4.2), Fill: "#F4F1E6", Stroke: "#C9C2A8", StrokeWidth: 1),
            new SPath(Ellipse(41, 42, 3.8, 3.8), Fill: "#F4F1E6", Stroke: "#C9C2A8", StrokeWidth: 1),
        };

        private static double Lerp(double a, double b, double q) => a + (b - a) * Math.Clamp(q, 0, 1);
        private static double EaseOut(double q) => 1 - Math.Pow(1 - Math.Clamp(q, 0, 1), 3);
        private static double Seg(double p, double a, double b) => (p - a) / (b - a);

        /// <summary>Sekt nakloněný o 32°: každé ~3,6 s vyletí špunt po ose láhve, pak pěna a bublinky.</summary>
        public static List<SceneNode> Sekt(double t)
        {
            var p = (t % 3.6) / 3.6;

            // špunt: klid → zavrtění → výstřel → zmizí → vrátí se
            double dx = 0, dy = 0, rot = 0, corkOp = 1;
            if (p < 0.38) { }
            else if (p < 0.50) { rot = 6 * Math.Sin(Seg(p, 0.38, 0.50) * 2 * Math.PI); dy = -2 * Seg(p, 0.38, 0.50); }
            else if (p < 0.68) { var e = EaseOut(Seg(p, 0.50, 0.68)); dx = 4 * e; dy = -2 - 32 * e; rot = 260 * e; }
            else if (p < 0.78) { var q = Seg(p, 0.68, 0.78); dx = Lerp(4, 6, q); dy = Lerp(-34, -40, q); rot = Lerp(260, 330, q); corkOp = 1 - q; }
            else if (p < 0.92) corkOp = 0;
            else corkOp = Seg(p, 0.92, 1.0);

            // pěna z hrdla
            double foamS = 0, foamOp = 0;
            if (p >= 0.49 && p < 0.56) { var q = Seg(p, 0.49, 0.56); foamS = 1.15 * q; foamOp = q; }
            else if (p >= 0.56 && p < 0.64) { foamS = Lerp(1.15, 1, Seg(p, 0.56, 0.64)); foamOp = 1; }
            else if (p >= 0.64 && p < 0.84) { foamS = Lerp(1, 0.9, Seg(p, 0.64, 0.84)); foamOp = 0.95; }
            else if (p >= 0.84 && p < 0.92) { var q = Seg(p, 0.84, 0.92); foamS = Lerp(0.9, 0.4, q); foamOp = 0.9 * (1 - q); }
            var dripOp = p >= 0.56 && p < 0.94 ? Math.Min(1, Seg(p, 0.56, 0.66)) * (p > 0.88 ? 1 - Seg(p, 0.88, 0.94) : 1) : 0;

            var bottle = new List<SceneNode>();
            if (foamOp > 0)
                bottle.Add(new SGroup(46, 18, 0, null, new()
                {
                    new SPath("M -8 0 C -12 -6 -8 -12 -4 -10 C -5 -16 2 -18 4 -13 C 8 -16 13 -11 9 -7 C 13 -4 10 1 7 0 Z",
                        Fill: "#FFF8DC", FillOpacity: foamOp, Stroke: "#E8D9A0", StrokeWidth: 1, StrokeOpacity: foamOp)
                }, Math.Max(0.01, foamS)));
            bottle.Add(new SPath("M 38 92 L 38 52 C 38 44 42 40 42 32 L 42 18 L 50 18 L 50 32 C 50 40 54 44 54 52 L 54 92 Z",
                Fill: "#2F5A2A", Stroke: "#1F3D1C", StrokeWidth: 1.2));
            bottle.Add(new SPath("M 41 17 L 51 17 L 52 34 C 52 38 40 38 40 34 Z", Fill: "#D9B13B", Stroke: "#A9861F", StrokeWidth: 1));
            if (dripOp > 0)
                bottle.Add(new SPath("M 42 17 C 41 22 42 26 43.5 27 C 45 26 44 21 45 17 Z M 48 17 C 48 20 48 23 49.5 23.5 C 51 23 50.5 20 50.5 17 Z",
                    Fill: "#FFF8DC", FillOpacity: dripOp, Stroke: "#E8D9A0", StrokeWidth: 0.8, StrokeOpacity: dripOp));
            bottle.Add(new SPath(RoundRect(38.5, 58, 15, 18, 2), Fill: "#F4ECD2", Stroke: "#C9B98F", StrokeWidth: 1));
            bottle.Add(new SPath("M 41 63 L 51 63 M 41 67 L 49 67 M 41 71 L 50 71", Stroke: "#8E1B2A", StrokeWidth: 1.2));
            bottle.Add(new SPath("M 43 52 C 44 48 44 42 44 36", Stroke: "#6E9A5C", StrokeWidth: 1.6, StrokeOpacity: 0.7));
            if (corkOp > 0)
                bottle.Add(new SGroup(46 + dx, 12.5 + dy, rot, null, new()
                {
                    new SPath("M -4 5.5 L -3.5 -3.5 C -3.5 -6.5 3.5 -6.5 3.5 -3.5 L 4 5.5 Z", Fill: "#C89B5A", FillOpacity: corkOp,
                        Stroke: "#8A6630", StrokeWidth: 1, StrokeOpacity: corkOp),
                    new SPath("M -3 0.5 L 3 0.5", Stroke: "#8A6630", StrokeWidth: 0.8, StrokeOpacity: corkOp)
                }));
            // bublinky
            var bubbles = new[] { (44.0, 10.0, 1.8, -6.0, 0.05), (48.0, 9.0, 1.4, 5.0, 0.2), (46.0, 11.0, 1.2, -1.0, 0.35) };
            foreach (var (bx, by, r, drift, delay) in bubbles)
            {
                var bp = ((t - delay) % 3.6 + 3.6) % 3.6 / 3.6;
                if (bp < 0.52 || bp > 0.9) continue;
                var q = Seg(bp, 0.52, 0.9);
                var op = bp < 0.58 ? Seg(bp, 0.52, 0.58) : 1 - q;
                bottle.Add(new SPath(Ellipse(bx + drift * q, by - 26 * q, r, r), Fill: "#F7E7A6", FillOpacity: Math.Clamp(op, 0, 1)));
            }

            // celé otočené kolem (46, 60)
            return new() { new SGroup(46, 60, 32, null, new() { new SGroup(-46, -60, 0, null, bottle) }) };
        }

        // ---------------- tácek (88 × 88): sněhová čepice / serpentýna ----------------

        public static List<SceneNode> CoasterCap() => new()
        {
            new SPath("M 10 20 C 12 8 26 2 44 2 C 62 2 76 8 78 20 C 70 16 66 22 60 18 C 54 24 48 20 44 23 C 38 19 32 24 26 18 C 20 22 16 17 10 20 Z", Fill: "#FFFFFF"),
            new SPath("M 10 20 C 16 17 20 22 26 18 C 32 24 38 19 44 23 C 48 20 54 24 60 18 C 66 22 70 16 78 20", Stroke: "#C9D8E6", StrokeWidth: 1.5)
        };

        public static List<SceneNode> CoasterStreamer() => new()
        {
            new SPath("M 14 30 C 4 18 18 6 28 14 C 36 4 50 10 46 20", Stroke: "#D4A62A", StrokeWidth: 3, Round: true),
            new SPath("M 78 70 C 92 74 94 90 82 92 C 82 100 68 100 70 90", Stroke: "#B3262E", StrokeWidth: 2.6, Round: true)
        };

        // ---------------- serpentýna přes levý roh lístku (Silvestr) ----------------

        public static List<SceneNode> Streamer() => new()
        {
            new SPath("M 4 10 C 22 2 26 26 40 18 C 54 10 52 34 68 28 C 82 22 82 44 96 40 C 106 37 110 46 116 52", Stroke: "#D4A62A", StrokeWidth: 3, Round: true),
            new SPath("M 10 30 C 20 22 28 40 38 34 C 48 28 50 46 60 44", Stroke: "#B3262E", StrokeWidth: 2.4, StrokeOpacity: 0.8, Round: true)
        };

        // ---------------- cedulka: ikona a svíčky ----------------

        public static List<SceneNode> TagIcon(string theme) => theme switch
        {
            "silvestr" => new() { new SPath("M 10 1 L 12.2 7.3 L 18.5 9.5 L 12.2 11.7 L 10 18 L 7.8 11.7 L 1.5 9.5 L 7.8 7.3 Z", Fill: "#F2C94C") },
            "zima" => Snowflake(10, 10, 8, "#5B87B5"),
            _ => new()
            {
                new SPath("M 9 1 L 16 15 L 2 15 Z", Fill: "#2E6B3A"),
                new SPath("M 9 5 L 14 15 L 4 15 Z", Fill: "#3E8A4C"),
                new SPath(Rect(7.5, 15, 3, 4), Fill: "#6B3E1E"),
                new SPath(Ellipse(9, 2, 1.6, 1.6), Fill: "#E3B23C")
            }
        };

        private static List<SceneNode> Snowflake(double cx, double cy, double r, string color)
        {
            var n = new List<SceneNode>();
            for (var k = 0; k < 6; k++)
            {
                var a = k * Math.PI / 3;
                double X(double d, double off = 0) => cx + d * Math.Cos(a + off);
                double Y(double d, double off = 0) => cy + d * Math.Sin(a + off);
                n.Add(new SPath($"M {F(cx)} {F(cy)} L {F(X(r))} {F(Y(r))} M {F(X(r * 0.55))} {F(Y(r * 0.55))} L {F(X(r * 0.8, 0.35))} {F(Y(r * 0.8, 0.35))} M {F(X(r * 0.55))} {F(Y(r * 0.55))} L {F(X(r * 0.8, -0.35))} {F(Y(r * 0.8, -0.35))}",
                    Stroke: color, StrokeWidth: 1.4, Round: true));
            }
            return n;
        }

        /// <summary>Čtyři adventní svíčky (40 × 20), lit = kolik hoří.</summary>
        public static List<SceneNode> Candles(int lit)
        {
            var n = new List<SceneNode>();
            for (var i = 0; i < 4; i++)
            {
                var x = i * 10.5;
                var on = i < lit;
                if (on) n.Add(new SPath($"M {F(x + 4.5)} 1 C {F(x + 7)} 4 {F(x + 7)} 7 {F(x + 4.5)} 8 C {F(x + 2)} 7 {F(x + 2)} 4 {F(x + 4.5)} 1 Z", Fill: "#E8A52A"));
                n.Add(new SPath(RoundRect(x + 1.5, 8, 6, 12, 1), Fill: on ? "#8E1B2A" : "#C9B98F"));
            }
            return n;
        }

        // ---------------- odznaky (56 × 56): kroužek + piktogram ----------------

        private static readonly Dictionary<string, string> Glyphs = new()
        {
            ["candle"] = "M 10 9 L 14 9 L 14 21 L 10 21 Z M 12 2 C 14 5 14 7 12 8 C 10 7 10 5 12 2 Z",
            ["tree"] = "M 12 2 L 20 16 L 4 16 Z M 10 16 L 14 16 L 14 21 L 10 21 Z",
            ["pin"] = "M 12 2 C 15.9 2 19 5.1 19 9 C 19 14 12 22 12 22 C 12 22 5 14 5 9 C 5 5.1 8.1 2 12 2 Z",
            ["glass"] = "M 7 2 L 17 2 C 17 8 15 11 12 11 C 9 11 7 8 7 2 Z M 11 11 L 13 11 L 13 19 L 11 19 Z M 8 20 L 16 20 L 16 22 L 8 22 Z",
            ["people"] = "M 8 4 C 9.7 4 11 5.3 11 7 C 11 8.7 9.7 10 8 10 C 6.3 10 5 8.7 5 7 C 5 5.3 6.3 4 8 4 Z M 16 4 C 17.7 4 19 5.3 19 7 C 19 8.7 17.7 10 16 10 C 14.3 10 13 8.7 13 7 C 13 5.3 14.3 4 16 4 Z M 2 20 C 2 15 5 12 8 12 C 11 12 14 15 14 20 Z M 10 20 C 10 15 13 12 16 12 C 19 12 22 15 22 20 Z",
            ["bell"] = "M 12 3 C 15.3 3 18 5.7 18 9 L 18 14 L 20 17 L 4 17 L 6 14 L 6 9 C 6 5.7 8.7 3 12 3 Z M 10 19 L 14 19 C 14 20.1 13.1 21 12 21 C 10.9 21 10 20.1 10 19 Z",
            ["gift"] = "M 3 9 L 21 9 L 21 13 L 3 13 Z M 5 14 L 19 14 L 19 22 L 5 22 Z M 12 9 C 11 5 6 4 6 7 C 6 9 12 9 12 9 Z M 12 9 C 13 5 18 4 18 7 C 18 9 12 9 12 9 Z",
            ["heart"] = "M 12 21 C 12 21 3 15 3 9 C 3 6 5.2 4 7.8 4 C 9.6 4 11.1 5 12 6.4 C 12.9 5 14.4 4 16.2 4 C 18.8 4 21 6 21 9 C 21 15 12 21 12 21 Z",
            ["bottle"] = "M 10 2 L 14 2 L 14 6 L 16 9 L 16 22 L 8 22 L 8 9 L 10 6 Z",
            ["mug"] = "M 4 6 L 16 6 L 16 20 C 16 21 15 22 14 22 L 6 22 C 5 22 4 21 4 20 Z M 16 9 L 19 9 C 20.7 9 21 10.5 21 12 L 21 14 C 21 15.5 20.7 17 19 17 L 16 17 L 16 15 L 18.5 15 L 18.5 11 L 16 11 Z M 4 3 C 6 1.5 8 4 10 2.5 C 12 1 14 4 16 3 L 16 6 L 4 6 Z",
            ["letter"] = "M 3 6 L 21 6 L 21 19 L 3 19 Z M 3 6 L 12 13 L 21 6",
            ["calendar"] = "M 3 5 L 21 5 L 21 21 L 3 21 Z M 7 2 L 9 2 L 9 7 L 7 7 Z M 15 2 L 17 2 L 17 7 L 15 7 Z",
            ["star"] = "M 12 2 L 15 9 L 22 9 L 16.5 13.5 L 18.5 21 L 12 16.5 L 5.5 21 L 7.5 13.5 L 2 9 L 9 9 Z",
            ["clock"] = "M 12 2 C 17.5 2 22 6.5 22 12 C 22 17.5 17.5 22 12 22 C 6.5 22 2 17.5 2 12 C 2 6.5 6.5 2 12 2 Z",
            ["moon"] = "M 15 3 C 10 3 6 7 6 12 C 6 17 10 21 15 21 C 17.5 21 19.7 20 21 18 C 16.5 18 13 14.5 13 10 C 13 7 14 4.6 15 3 Z",
            ["crown"] = "M 3 8 L 7 12 L 12 5 L 17 12 L 21 8 L 19 19 L 5 19 Z",
            ["medal"] = "M 7 2 L 11 2 L 13 8 L 9 8 Z M 13 2 L 17 2 L 15 8 L 11 8 Z M 12 8 C 15.3 8 18 10.7 18 14 C 18 17.3 15.3 20 12 20 C 8.7 20 6 17.3 6 14 C 6 10.7 8.7 8 12 8 Z",
            ["fish"] = "M 2 12 C 5 7 10 6 15 8 L 21 4 L 20 12 L 21 20 L 15 16 C 10 18 5 17 2 12 Z",
            ["lantern"] = "M 10 2 L 14 2 L 14 4 L 10 4 Z M 8 5 L 16 5 L 17 8 L 17 18 L 16 21 L 8 21 L 7 18 L 7 8 Z",
            ["halo"] = "M 12 2 C 17 2 21 3.8 21 6 C 21 8.2 17 10 12 10 C 7 10 3 8.2 3 6 C 3 3.8 7 2 12 2 Z M 12 13 C 14.2 13 16 14.8 16 17 L 16 22 L 8 22 L 8 17 C 8 14.8 9.8 13 12 13 Z"
        };

        /// <summary>Odznak 56 × 56: získaný = barevný kroužek, nezískaný = čárkovaný a šedý.</summary>
        public static List<SceneNode> Badge(string icon, string color, bool got)
        {
            var n = new List<SceneNode>();
            if (got)
            {
                n.Add(new SPath(Ellipse(28, 28, 27, 27), Fill: color, FillOpacity: 0.33));
                n.Add(new SPath(Ellipse(28, 28, 24.5, 24.5), Fill: "#F5F5DC"));
                n.Add(new SPath(Ellipse(28, 28, 22, 22), Fill: "#FFFDF4", Stroke: color, StrokeWidth: 2.5));
            }
            else
            {
                // čárkovaný kroužek (plátno v appce čárkování cest neumí → kousky oblouku)
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < 20; i++)
                {
                    double a0 = i * Math.PI / 10, a1 = a0 + Math.PI / 20;
                    sb.Append($"M {F(28 + 26 * Math.Cos(a0))} {F(28 + 26 * Math.Sin(a0))} L {F(28 + 26 * Math.Cos(a1))} {F(28 + 26 * Math.Sin(a1))} ");
                }
                n.Add(new SPath(sb.ToString().Trim(), Stroke: "#B9AE90", StrokeWidth: 2, Round: true));
            }
            var glyph = Glyphs.TryGetValue(icon, out var g) ? g : Glyphs["star"];
            var ink = got ? color : "#B9AE90";
            var line = icon == "letter";
            n.Add(new SGroup(13, 13, 0, null, new()
            {
                line ? new SPath(glyph, Stroke: ink, StrokeWidth: 1.8, Round: true) : new SPath(glyph, Fill: ink)
            }, 1.25));
            // ručičky hodin a řádky kalendáře přes vybarvený tvar
            if (icon == "clock") n.Add(new SGroup(13, 13, 0, null, new() { new SPath("M 12 6 L 12 12 L 16 14", Stroke: "#FFFDF4", StrokeWidth: 1.8, Round: true) }, 1.25));
            // oko kapra, plamínek v lucerně
            if (icon == "fish") n.Add(new SGroup(13, 13, 0, null, new() { new SPath(Ellipse(6.5, 11, 1.3, 1.3), Fill: "#FFFDF4") }, 1.25));
            if (icon == "lantern") n.Add(new SGroup(13, 13, 0, null, new() { new SPath("M 12 9 C 14 12 14 15 12 17 C 10 15 10 12 12 9 Z", Fill: "#FFFDF4") }, 1.25));
            if (icon == "calendar") n.Add(new SGroup(13, 13, 0, null, new() { new SPath("M 3 10 L 21 10", Stroke: "#FFFDF4", StrokeWidth: 1.5) }, 1.25));
            return n;
        }

        // ---------------- bečka na akčním lístku (64 × 80): soudek s hladinou ----------------

        /// <summary>Soudek, fill = 0..1 kolik zbývá (hladina piva s pěnou).</summary>
        public static List<SceneNode> Keg(double fill)
        {
            fill = Math.Clamp(fill, 0, 1);
            const string body = "M 10 8 C 4 30 4 50 10 72 L 54 72 C 60 50 60 30 54 8 Z";
            var top = 70 - 60 * fill;
            var n = new List<SceneNode>
            {
                new SPath(body, Fill: "#E9D9B4", Stroke: "#6B3E1E", StrokeWidth: 2.5),
                new SGroup(0, 0, 0, body, new()
                {
                    new SPath(Rect(0, top, 64, 80), Gradient: new SGradient(0, top, 0, 72, new[] { (0.0, "#F2B230"), (1.0, "#B5650A") })),
                    fill > 0.02 ? new SPath(Rect(0, top - 4, 64, 5), Fill: "#FFF8E6") : new SPath("M 0 0 Z")
                }),
                new SPath(body, Stroke: "#6B3E1E", StrokeWidth: 2.5),
                new SPath("M 7 22 C 22 25 42 25 57 22", Stroke: "#3A3A3A", StrokeWidth: 3, Round: true),
                new SPath("M 7 58 C 22 61 42 61 57 58", Stroke: "#3A3A3A", StrokeWidth: 3, Round: true),
                new SPath(Ellipse(32, 8, 22, 4), Fill: "#D6C49A", Stroke: "#6B3E1E", StrokeWidth: 2),
                new SPath(RoundRect(28, 2, 8, 6, 1.5), Fill: "#8A8A8A", Stroke: "#3A3A3A", StrokeWidth: 1)
            };
            return n;
        }

        // ---------------- padající sníh / konfety / ohňostroj (přes celou stránku) ----------------

        private sealed record Flake(double X, double Size, double Opacity, double Duration, double Delay, double Drift, int Color, bool Round, double H);

        private static readonly Flake[] Flakes = MakeFlakes(46, 7);

        private static Flake[] MakeFlakes(int count, int seed)
        {
            var rnd = new Random(seed);
            return Enumerable.Range(0, count).Select(i => new Flake(rnd.NextDouble(), 2 + rnd.NextDouble() * 4, 0.55 + rnd.NextDouble() * 0.4,
                7 + rnd.NextDouble() * 8, rnd.NextDouble() * 14, 20 + rnd.NextDouble() * 16, i, i % 3 == 0, 2 + rnd.NextDouble() * 5)).ToArray();
        }

        private static readonly string[] ConfettiColors = { "#F2C94C", "#D4A62A", "#E8433A", "#3C8DDE", "#FFFFFF", "#C0C7D0" };

        /// <summary>Částice v čase t pro plochu w × h (sníh = bílé kuličky, Silvestr = konfety).</summary>
        public static List<SceneNode> Particles(double w, double h, string? theme, double t)
        {
            var n = new List<SceneNode>();
            var confetti = theme == "silvestr";
            foreach (var f in Flakes)
            {
                var prog = ((t + f.Delay) % f.Duration) / f.Duration;
                var x = f.X * w + f.Drift * prog;
                var y = -20 + (h + 40) * prog;
                if (!confetti)
                    n.Add(new SPath(Ellipse(x, y, f.Size / 2, f.Size / 2), Fill: "#FFFFFF", FillOpacity: f.Opacity));
                else
                {
                    var color = ConfettiColors[f.Color % ConfettiColors.Length];
                    var fw = 2 + f.Size; var fh = f.H;
                    n.Add(new SGroup(x, y, 760 * prog, null, new()
                    {
                        new SPath(f.Round ? Ellipse(0, 0, fw / 2, fw / 2) : Rect(-fw / 2, -fh / 2, fw, fh), Fill: color, FillOpacity: Math.Min(1, f.Opacity + 0.15))
                    }));
                }
            }
            return n;
        }

        // ohňostroj: střed (x jako podíl šířky, y v px), barva, poloměr, zpoždění
        private static readonly (double X, double Y, string Color, double R, double Delay)[] Spots =
        {
            (0.18, 70, "#F2C94C", 30, 0), (0.77, 56, "#E8433A", 34, 1.3), (0.5, 40, "#C0C7D0", 26, 2.4),
            (0.88, 150, "#3C8DDE", 22, 0.7), (0.07, 180, "#F2C94C", 20, 3.1)
        };

        public static List<SceneNode> Fireworks(double w, double t)
        {
            var n = new List<SceneNode>();
            foreach (var (sx, sy, color, r, delay) in Spots)
            {
                var q = (((t - delay) % 3.6) + 3.6) % 3.6 / 3.6;
                if (q > 0.8) continue;
                double dist, len, op;
                if (q < 0.55) { var e = EaseOut(q / 0.55); dist = r * e; len = 14 * Lerp(0.2, 1, e); op = Math.Min(1, q / 0.08) * 0.95; }
                else { var e = Seg(q, 0.55, 0.8); dist = Lerp(r, r * 1.25, e); len = 14 * Lerp(1, 0.4, e); op = 0.95 * (1 - e); }
                var cx = sx * w;
                for (var k = 0; k < 12; k++)
                {
                    var a = k * Math.PI / 6;
                    double ex = cx + Math.Sin(a) * dist, ey = sy - Math.Cos(a) * dist;
                    double bx = cx + Math.Sin(a) * Math.Max(0, dist - len), by = sy - Math.Cos(a) * Math.Max(0, dist - len);
                    n.Add(new SPath($"M {F(bx)} {F(by)} L {F(ex)} {F(ey)}", Stroke: color, StrokeWidth: 3, StrokeOpacity: op, Round: true));
                }
            }
            return n;
        }
    }
}
