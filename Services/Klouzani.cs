using System.Text;
using static Dezolati.Services.SceneKit;

namespace Dezolati.Services
{
    /// <summary>
    /// Klouzání krýglu po pultu – hra pro jednoho (logika i obrázek sdílené appkou a webem).
    /// Krýgl se postrčí prstem (švih doprava) a jede po pultu ke štamgastovi; má zastavit na jeho tácku.
    /// Pult je každé kolo jinak kluzký a občas je na něm loužička (tam to klouže víc).
    /// Přejede-li konec pultu, je na zemi. Pět krýglů, body se sčítají.
    /// </summary>
    public sealed class SlideGame
    {
        public const int Rounds = 5;
        public int Round { get; private set; } = 1;
        public int Total { get; private set; }
        public List<SlideResult> Results { get; } = new();
        public SlideRound Current { get; private set; }
        public bool Finished => Results.Count >= Rounds && Current.State == SlideRound.Phase.Done;

        public SlideGame() => Current = new SlideRound();

        public static string Verdict(int total) => total switch
        {
            >= 450 => "Výčepní by tě zaměstnal na místě.",
            >= 350 => "Štamgasti spokojení, nikdo nemusel vstávat.",
            >= 200 => "Půlka hospody se natahovala přes pult.",
            _ => "Hospodský ti střepy strhne z výplaty."
        };

        /// <summary>Zavolat po dokončení kola (Current.State == Done) – připíše body.</summary>
        public void Commit()
        {
            if (Current.Result is { } r && Results.Count < Round)
            {
                Results.Add(r);
                Total += r.Score;
            }
        }

        public bool Next()
        {
            if (Round >= Rounds) return false;
            Round++;
            Current = new SlideRound();
            return true;
        }
    }

    public sealed record SlideResult(string Title, int Score, double Distance, bool Broken);

    public sealed class SlideRound
    {
        // pult (souřadnice scény 360 × 400)
        public const double SurfaceY = 296;        // kde stojí dno krýglu
        public const double EdgeX = 312;           // konec pultu
        public const double FloorY = 384;
        public const double StartX = 40, PushMax = 112;
        private const double Gravity = 1400;

        public enum Phase { Ready, Pushing, Sliding, Falling, Done }

        public Phase State { get; private set; } = Phase.Ready;
        public double X { get; private set; } = StartX;
        public double V { get; private set; }
        public double TargetX { get; }
        public double Friction { get; }            // zpomalení px/s²
        public List<(double From, double To)> Puddles { get; } = new();
        public double FallY { get; private set; }  // o kolik spadl
        public double FallRot { get; private set; }
        public SlideResult? Result { get; private set; }

        private double fallVy, doneT;
        private double pushStartFinger, pushStartX;
        private readonly List<(double T, double X)> samples = new();

        public SlideRound(int? seed = null)
        {
            var r = seed is int s ? new Random(s) : Random.Shared;
            TargetX = 180 + r.NextDouble() * 100;
            Friction = 620 + r.NextDouble() * 420;
            var count = r.Next(0, 3);
            for (var i = 0; i < count; i++)
            {
                var from = PushMax + 20 + r.NextDouble() * (EdgeX - PushMax - 70);
                Puddles.Add((from, from + 26 + r.NextDouble() * 34));
            }
        }

        /// <summary>Kluzkost pultu slovy (ať hráč ví, do čeho jde).</summary>
        public string Surface => Friction < 760 ? "Pult je mokrý jak po oslavě – klouže." : Friction < 900 ? "Pult je normálně upatlaný." : "Pult je suchý a lepí – brzdí.";

        // ---- ovládání: prst táhne krýgl v rozjezdové zóně, puštěním (nebo na konci zóny) jede ----

        public void Down(double fingerX, double t)
        {
            if (State != Phase.Ready) return;
            State = Phase.Pushing;
            pushStartFinger = fingerX;
            pushStartX = X;
            samples.Clear();
            samples.Add((t, X));
        }

        public void Move(double fingerX, double t)
        {
            if (State != Phase.Pushing) return;
            X = Math.Clamp(pushStartX + (fingerX - pushStartFinger), StartX - 15, PushMax);
            samples.Add((t, X));
            if (samples.Count > 30) samples.RemoveAt(0);
            if (X >= PushMax) Up(t);   // ruka došla na konec – krýgl jede sám
        }

        public void Up(double t)
        {
            if (State != Phase.Pushing) return;
            // rychlost z posledních ~90 ms tahu
            var recent = samples.Where(s => s.T >= t - 0.09).ToList();
            if (recent.Count < 2) recent = samples.TakeLast(2).ToList();
            var v = recent.Count >= 2 && recent[^1].T > recent[0].T
                ? (recent[^1].X - recent[0].X) / (recent[^1].T - recent[0].T)
                : 0;
            if (v < 40)
            {
                // jen jsi ho posunul, nepostrčil – zkus znovu
                State = Phase.Ready;
                return;
            }
            V = Math.Min(v, 1700);
            State = Phase.Sliding;
        }

        public void Step(double dt)
        {
            if (dt <= 0) return;
            dt = Math.Min(dt, 0.05);
            switch (State)
            {
                case Phase.Sliding:
                    var a = Puddles.Any(p => X >= p.From && X <= p.To) ? Friction * 0.35 : Friction;
                    V -= a * dt;
                    if (V <= 0)
                    {
                        V = 0;
                        Finish();
                        break;
                    }
                    X += V * dt;
                    if (X > EdgeX)
                    {
                        State = Phase.Falling;
                        fallVy = 0;
                    }
                    break;
                case Phase.Falling:
                    X = Math.Min(336, X + Math.Min(V, 260) * 0.5 * dt);
                    fallVy += Gravity * dt;
                    FallY += fallVy * dt;
                    FallRot = Math.Min(100, FallRot + 260 * dt);
                    if (SurfaceY + FallY >= FloorY)
                    {
                        FallY = FloorY - SurfaceY;
                        if (doneT == 0) Finish(broken: true);
                    }
                    break;
            }
        }

        private void Finish(bool broken = false)
        {
            doneT = 1;
            State = Phase.Done;
            if (broken)
            {
                Result = new SlideResult("Na zemi", 0, 0, true);
                return;
            }
            var d = X - TargetX;
            var ad = Math.Abs(d);
            var score = ad <= 4 ? 100 : (int)Math.Round(Math.Max(0, 100 - (ad - 4) * 2.2));
            var title = ad <= 4 ? "Přesně do ruky"
                : ad <= 14 ? "Ani nemusel natáhnout ruku"
                : d < 0 ? (ad > 60 ? "Zůstalo stát v půlce pultu" : "Nedojelo – musí se natáhnout")
                : ad > 60 ? "Přejelo, štamgast jen koukal" : "Přejelo kousek";
            Result = new SlideResult(title, score, ad, false);
        }
    }

    /// <summary>Obrázek pultu pro klouzání (360 × 400).</summary>
    public static class SlideScene
    {
        private const double TopY = SlideRound.SurfaceY - 8;     // přední hrana desky (vršek pásu)
        private const double FrontY = SlideRound.SurfaceY + 6;   // čelo pultu
        private const double KryglScale = 0.4;

        public static List<SceneNode> Build(SlideRound r)
        {
            var n = new List<SceneNode>();
            const double W = 360, H = 400;

            // zeď s obkladačkami
            n.Add(new SPath(Rect(0, 0, W, FrontY), Gradient: new SGradient(0, 0, 0, FrontY,
                new[] { (0.0, "#EFE6D2"), (1.0, "#E0CFAE") })));
            var tiles = new StringBuilder();
            var row = 0;
            for (var y = 0.0; y < TopY; y += 30, row++)
            {
                tiles.Append($"M 0 {F(y)} L {F(W)} {F(y)} ");
                for (var x = row % 2 == 0 ? 0.0 : 22.0; x < W; x += 44)
                    tiles.Append($"M {F(x)} {F(y)} L {F(x)} {F(Math.Min(TopY, y + 30))} ");
            }
            n.Add(new SPath(tiles.ToString(), Stroke: "#C9B48C", StrokeWidth: 1, StrokeOpacity: 0.55));
            // police s lahvemi (jen siluety)
            n.Add(new SPath(Rect(20, 92, 320, 6), Fill: "#7A4A24"));
            var rnd = new Random(5);
            for (var x = 30.0; x < 330; x += 22 + rnd.NextDouble() * 10)
            {
                var bh = 34 + rnd.NextDouble() * 18;
                var col = rnd.Next(3) switch { 0 => "#2F5D3A", 1 => "#6B3A1E", _ => "#3E4B5A" };
                n.Add(new SPath($"M {F(x)} 92 L {F(x)} {F(92 - bh * 0.6)} Q {F(x)} {F(92 - bh * 0.72)} {F(x + 3)} {F(92 - bh * 0.78)} " +
                                $"L {F(x + 3)} {F(92 - bh)} L {F(x + 7)} {F(92 - bh)} L {F(x + 7)} {F(92 - bh * 0.78)} " +
                                $"Q {F(x + 10)} {F(92 - bh * 0.72)} {F(x + 10)} {F(92 - bh * 0.6)} L {F(x + 10)} 92 Z",
                    Fill: col, FillOpacity: 0.85));
            }

            // podlaha (za koncem pultu)
            n.Add(new SPath(Rect(0, FrontY, W, H - FrontY), Fill: "#3A2A1E"));
            var floor = new StringBuilder();
            for (var x = SlideRound.EdgeX + 6; x < W; x += 18) floor.Append($"M {F(x)} {F(SlideRound.FloorY)} L {F(x + 10)} {F(H)} ");
            n.Add(new SPath(floor.ToString(), Stroke: "#2A1D14", StrokeWidth: 1.5));
            n.Add(new SPath($"M {F(SlideRound.EdgeX)} {F(SlideRound.FloorY)} L {F(W)} {F(SlideRound.FloorY)}", Stroke: "#5A4232", StrokeWidth: 2));

            // štamgast za pultem u tácku
            Patron(n, r.TargetX, r.State == SlideRound.Phase.Done && r.Result is { Broken: false, Distance: <= 14 });

            // pult: deska (pás, ať jsou vidět loužičky) a čelo
            n.Add(new SPath(Rect(0, TopY, SlideRound.EdgeX, 14), Gradient: new SGradient(0, TopY, 0, TopY + 14,
                new[] { (0.0, "#B07A44"), (1.0, "#8A5A2E") })));
            n.Add(new SPath($"M 0 {F(TopY)} L {F(SlideRound.EdgeX)} {F(TopY)}", Stroke: "#6E4320", StrokeWidth: 1));
            n.Add(new SPath(Rect(0, FrontY, SlideRound.EdgeX, SlideRound.FloorY - FrontY), Gradient: new SGradient(0, FrontY, 0, SlideRound.FloorY,
                new[] { (0.0, "#6B3E1E"), (1.0, "#3E2210") })));
            var planks = new StringBuilder();
            for (var x = 30.0; x < SlideRound.EdgeX; x += 60) planks.Append($"M {F(x)} {F(FrontY)} L {F(x)} {F(SlideRound.FloorY)} ");
            n.Add(new SPath(planks.ToString(), Stroke: "#2A160A", StrokeWidth: 1.5, StrokeOpacity: 0.6));
            n.Add(new SPath($"M 0 {F(FrontY)} L {F(SlideRound.EdgeX)} {F(FrontY)} L {F(SlideRound.EdgeX)} {F(SlideRound.FloorY)}",
                Stroke: "#D9A56A", StrokeWidth: 1.5));

            // loužičky (lesklé)
            foreach (var (a, b) in r.Puddles)
            {
                n.Add(new SPath(Ellipse((a + b) / 2, SlideRound.SurfaceY - 2, (b - a) / 2, 3.5), Fill: "#D6EEF7", FillOpacity: 0.85, Stroke: "#9CC7D6", StrokeWidth: 0.8));
                n.Add(new SPath($"M {F(a + 6)} {F(SlideRound.SurfaceY - 3)} L {F(a + (b - a) * 0.45)} {F(SlideRound.SurfaceY - 3.5)}",
                    Stroke: "#FFFFFF", StrokeWidth: 1.2, StrokeOpacity: 0.8, Round: true));
            }
            // rozjezdová zóna (křída)
            n.Add(new SPath($"M {F(SlideRound.PushMax + 16)} {F(TopY + 2)} L {F(SlideRound.PushMax + 16)} {F(TopY + 12)}",
                Stroke: "#FFFFFF", StrokeWidth: 2, StrokeOpacity: 0.6, Round: true));
            PatronHand(n, r.TargetX);
            // tácek
            n.Add(new SPath(Ellipse(r.TargetX, SlideRound.SurfaceY - 1, 15, 3.2), Fill: "#FFFFFF", Stroke: "#B3262E", StrokeWidth: 1.6));

            // krýgl
            var h = PourScene.KryglHeight * KryglScale;
            if (r.State == SlideRound.Phase.Done && r.Result is { Broken: true })
            {
                Shards(n, r.X);
            }
            else
            {
                var rot = r.FallRot;
                var mouthX = r.X;
                var mouthY = SlideRound.SurfaceY - h + r.FallY;
                if (rot < 1)   // stín
                    n.Add(new SPath(Ellipse(r.X, SlideRound.SurfaceY + 0.5, 15, 2), Fill: "#000000", FillOpacity: 0.25));
                n.Add(new SGroup(mouthX, mouthY, rot, null, PourScene.Krygl(PourGame.Mark / PourGame.Rim, 1.0, -rot), KryglScale));
                if (r.State == SlideRound.Phase.Ready)
                    n.Add(new SPath($"M {F(r.X + 26)} {F(mouthY + 16)} L {F(r.X + 56)} {F(mouthY + 16)} M {F(r.X + 50)} {F(mouthY + 11)} L {F(r.X + 57)} {F(mouthY + 16)} L {F(r.X + 50)} {F(mouthY + 21)}",
                        Stroke: "#5A3418", StrokeWidth: 2, StrokeOpacity: 0.55, Round: true));
            }
            return n;
        }

        /// <summary>Štamgast s knírem a kšiltovkou (spodek schová pult / stůl). baseY = kde končí ramena.</summary>
        public static void Patron(List<SceneNode> n, double x, bool happy, double baseY = double.NaN, double scale = 1.25, string coat = "#4B5E44")
        {
            const string skin = "#E9C3A0", ink = "#3B2314";
            var p = new List<SceneNode>
            {
                // ramena a tělo (spodek schová pult)
                new SPath("M -34 0 C -34 -34 -20 -44 0 -44 C 20 -44 34 -34 34 0 Z", Fill: coat, Stroke: ink, StrokeWidth: 1.5),
                new SPath("M -6 -44 L 0 -30 L 6 -44", Stroke: ink, StrokeWidth: 1.2, StrokeOpacity: 0.6),
                // hlava, knír, pusa, oči
                new SPath(Ellipse(0, -62, 15, 17), Fill: skin, Stroke: ink, StrokeWidth: 1.5),
                new SPath("M -8 -56 Q 0 -61 8 -56", Stroke: "#5A3A22", StrokeWidth: 3, Round: true),
                new SPath(happy ? "M -5 -52 Q 0 -48 5 -52" : "M -4 -51 L 4 -51", Stroke: ink, StrokeWidth: 1.4, Round: true),
                new SPath(Ellipse(-5, -66, 1.5, 1.5) + " " + Ellipse(5, -66, 1.5, 1.5), Fill: ink),
                // čepice s kšiltem
                new SPath("M -16 -70 Q -15 -86 0 -86 Q 15 -86 16 -70 Z", Fill: "#2E3B4E", Stroke: ink, StrokeWidth: 1.4),
                new SPath("M -18 -70 L -30 -68", Stroke: "#2E3B4E", StrokeWidth: 4, Round: true),
            };
            n.Add(new SGroup(x, double.IsNaN(baseY) ? TopY : baseY, 0, null, p, scale));
        }

        /// <summary>Ruka štamgasta na pultu (kreslí se až přes desku).</summary>
        private static void PatronHand(List<SceneNode> n, double x) =>
            n.Add(new SPath(Ellipse(x + 30, TopY + 3, 10, 5), Fill: "#E9C3A0", Stroke: "#3B2314", StrokeWidth: 1.2));

        private static void Shards(List<SceneNode> n, double x)
        {
            var y = SlideRound.FloorY;
            n.Add(new SPath(Ellipse(x + 4, y - 1, 26, 4), Fill: "#E2A532", FillOpacity: 0.8));
            n.Add(new SPath(Ellipse(x + 10, y - 3, 10, 2.5), Fill: "#FFF8E6", FillOpacity: 0.9));
            var rnd = new Random(3);
            for (var i = 0; i < 7; i++)
            {
                var sx = x - 20 + rnd.NextDouble() * 48;
                var sy = y - 2 - rnd.NextDouble() * 4;
                var s = 3 + rnd.NextDouble() * 5;
                n.Add(new SPath($"M {F(sx)} {F(sy)} L {F(sx + s)} {F(sy - s * 0.6)} L {F(sx + s * 0.4)} {F(sy + s * 0.3)} Z",
                    Fill: "#E8F3F5", FillOpacity: 0.9, Stroke: "#5A3418", StrokeWidth: 0.8));
            }
        }
    }
}
