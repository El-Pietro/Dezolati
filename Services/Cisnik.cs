using System.Text;
using static Dezolati.Services.SceneKit;

namespace Dezolati.Services
{
    /// <summary>
    /// Číšník s tácem – hra pro jednoho (logika i obrázek sdílené appkou a webem).
    /// Neseš tác s pěti krýgly přes hospodu ke stolu. Tác se sám naklání (jako tyč na prstu) a štamgasti
    /// do tebe vrážejí. Vyrovnává se jako tyč na prstu: prst na straně, která klesá, ji zvedá
    /// (nebo naklonit telefon proti pádu). Drží-li se moc dlouho, přepadne tác na druhou stranu.
    /// Při náklonu se pivo vylévá, při velkém náklonu krýgl sjede z tácu a rozbije se.
    /// </summary>
    public sealed class WaiterGame
    {
        public const int Beers = 5;
        public const double Duration = 24;        // s chůze ke stolu
        public const double SpillFrom = 8;        // ° – od toho náklonu teče přes okraj
        public const double DropAt = 30;          // ° – krýgl sjede
        private const double Instability = 1.2;   // jak rychle se tác sám převrací (1/s²)
        private const double Damping = 2.4;
        /// <summary>Síla vyrovnávání při plném zmáčknutí / náklonu (°/s²). Nasimulováno: pozorný hráč ~75 bodů, ležérní ~35.</summary>
        public const double Gain = 45;

        public enum Phase { Ready, Walking, Done }

        public Phase State { get; private set; } = Phase.Ready;
        public double T { get; private set; }
        public double Progress => Math.Min(1, T / Duration);
        /// <summary>Náklon tácu (°, + = pravá strana dolů) – to, co je vidět.</summary>
        public double Angle => Math.Clamp(wobble, -60, 60);
        /// <summary>Vyrovnávání −1 … 1: + zvedá pravou stranu (prst vpravo / telefon nakloněný doleva), − levou.</summary>
        public double Control { get; set; }
        /// <summary>Plnost krýglů zleva doprava (0–1); null = rozbitý.</summary>
        public double?[] Glasses { get; } = Enumerable.Repeat<double?>(1.0, Beers).ToArray();
        public int Broken => Glasses.Count(g => g == null);
        /// <summary>Hláška „pozor…“ před nárazem (null = klid).</summary>
        public string? Warning { get; private set; }
        /// <summary>Kde se zrovna vylévá (−1 vlevo, +1 vpravo, 0 nikde) – kvůli kapkám.</summary>
        public int SpillSide { get; private set; }
        /// <summary>Čas posledního rozbití (kvůli „Cink!“ a střepům).</summary>
        public double LastCrashT { get; private set; } = -10;
        public int LastCrashSide { get; private set; }
        public WaiterResult? Result { get; private set; }

        private double wobble, omega, nextEventT, eventDir;
        private string? pendingEvent;
        private readonly Random rnd;

        private static readonly string[] Bumps =
        {
            "Franta vstává od stolu!", "Někdo couvá se židlí!", "Štamgast gestikuluje!",
            "Pozor, kluk od fotbalu!", "Paní z kuchyně se řítí!", "Opilec se zvedá na záchod!"
        };

        public WaiterGame(int? seed = null)
        {
            rnd = seed is int s ? new Random(s) : new Random();
            nextEventT = 2.5 + rnd.NextDouble() * 2;
        }

        public void Start()
        {
            if (State != Phase.Ready) return;
            State = Phase.Walking;
            // ať to nezačne úplně rovně
            omega = (rnd.NextDouble() < 0.5 ? -1 : 1) * 6;
        }

        public void Step(double dt)
        {
            if (State != Phase.Walking || dt <= 0) return;
            dt = Math.Min(dt, 0.05);
            T += dt;

            // náhodné události: nejdřív varování, za 0,8 s náraz
            if (pendingEvent == null && T >= nextEventT - 0.8 && T < Duration - 1.5)
            {
                var puddle = rnd.NextDouble() < 0.25;
                pendingEvent = puddle ? "Mokrá podlaha!" : Bumps[rnd.Next(Bumps.Length)];
                eventDir = rnd.NextDouble() < 0.5 ? -1 : 1;
                Warning = pendingEvent + (eventDir < 0 ? " ←" : " →");
            }
            if (pendingEvent != null && T >= nextEventT)
            {
                var strength = pendingEvent.StartsWith("Mokrá") ? 45 : 25 + rnd.NextDouble() * 15;
                omega += eventDir * strength;
                pendingEvent = null;
                Warning = null;
                nextEventT = T + 2.6 + rnd.NextDouble() * 2.8 - Progress * 0.8;   // ke konci hustěji
            }

            // tác jako tyč na prstu: čím víc je nakloněný, tím rychleji padá; ovládání ho vrací
            var bob = Math.Sin(T * 7.5) * 6;   // houpání při chůzi
            var accel = Instability * Angle + bob - Damping * omega - Gain * Math.Clamp(Control, -1, 1);
            omega += accel * dt;
            wobble += omega * dt;

            // vylévání a sjíždění
            var a = Angle;
            SpillSide = 0;
            if (Math.Abs(a) > SpillFrom)
            {
                SpillSide = Math.Sign(a);
                var rate = (Math.Abs(a) - SpillFrom) * 0.022;
                for (var i = 0; i < Beers; i++)
                {
                    if (Glasses[i] is not double g) continue;
                    // na nižší straně teče víc
                    var pos = (i - (Beers - 1) / 2.0) / ((Beers - 1) / 2.0);   // −1 … 1
                    var side = 1 + 0.6 * pos * Math.Sign(a);
                    Glasses[i] = Math.Max(0, g - rate * side * dt);
                }
            }
            if (Math.Abs(a) > DropAt)
            {
                // sjede krajní krýgl na nižší straně
                var idx = a > 0 ? Array.FindLastIndex(Glasses, g => g != null) : Array.FindIndex(Glasses, g => g != null);
                if (idx >= 0)
                {
                    Glasses[idx] = null;
                    LastCrashT = T;
                    LastCrashSide = Math.Sign(a);
                    // tác bez váhy na té straně se cukne zpátky
                    omega = -Math.Sign(a) * 40;
                    wobble -= Math.Sign(a) * (Math.Abs(a) - DropAt * 0.4);
                }
            }

            if (Glasses.All(g => g == null) || T >= Duration)
                Finish();
        }

        private void Finish()
        {
            State = Phase.Done;
            Warning = null;
            var full = Glasses.Where(g => g != null).Sum(g => g!.Value);
            var score = (int)Math.Round(full / Beers * 100);
            var left = Beers - Broken;
            string title, comment;
            if (left == 0) { title = "Všechno na zemi"; comment = "Pět krýglů, pět střepin. Hospodský už volá pojišťovnu."; }
            else if (Broken == 0 && score >= 95) { title = "Vrchní roku"; comment = "Ani kapka na tácu. Štamgasti tleskají, hospodský počítá dýško."; }
            else if (Broken == 0 && score >= 80) { title = "Doneseno"; comment = "Trochu to šplouchlo, ale nikdo si nestěžoval."; }
            else if (Broken == 0) { title = "Doneseno, ale…"; comment = "Půl piva je na podlaze. Štamgasti dostali spíš ochutnávku."; }
            else if (Broken == 1) { title = "Jeden padl"; comment = "Cink. Zbytek dorazil. Kdo nedostal, ten bude remcat."; }
            else { title = $"{Broken} krýgly na zemi"; comment = "Hospodský si to strhne z výplaty. A má pravdu."; }
            Result = new WaiterResult(title, comment, score, left, Broken);
        }
    }

    public sealed record WaiterResult(string Title, string Comment, int Score, int Delivered, int Broken);

    /// <summary>Obrázek: hospoda ubíhá dozadu, uprostřed ruka s tácem a krýgly (360 × 400).</summary>
    public static class WaiterScene
    {
        private const double FloorY = 336;
        private const double TrayX = 180, TrayY = 214, TrayHalf = 118;
        private const double KScale = 0.28;

        public static List<SceneNode> Build(WaiterGame g)
        {
            var n = new List<SceneNode>();
            const double W = 360, H = 400;
            var scroll = g.T * 70;   // jak daleko ušel (px)

            // zeď s lambrí
            n.Add(new SPath(Rect(0, 0, W, FloorY), Gradient: new SGradient(0, 0, 0, FloorY,
                new[] { (0.0, "#E9DCC0"), (1.0, "#D8C49C") })));
            n.Add(new SPath(Rect(0, 200, W, FloorY - 200), Gradient: new SGradient(0, 200, 0, FloorY,
                new[] { (0.0, "#7A4A24"), (1.0, "#5A3418") })));
            var panels = new StringBuilder();
            for (var x = -(scroll * 0.6 % 60); x < W; x += 60) panels.Append($"M {F(x)} 206 L {F(x)} {F(FloorY)} ");
            n.Add(new SPath(panels.ToString(), Stroke: "#3E2210", StrokeWidth: 1.2, StrokeOpacity: 0.6));
            n.Add(new SPath($"M 0 200 L {F(W)} 200", Stroke: "#3E2210", StrokeWidth: 3));
            // obrázky na zdi
            for (var x = -(scroll * 0.6 % 180) + 40; x < W + 60; x += 180)
                n.Add(new SPath(Rect(x, 70, 46, 34), Fill: "#F4EEDC", Stroke: "#5A3418", StrokeWidth: 3));

            // stoly se štamgasty (ubíhají)
            var tableEvery = 170.0;
            var first = Math.Floor(scroll / tableEvery);
            for (var k = first - 1; k < first + 4; k++)
            {
                var x = k * tableEvery - scroll + 90;
                if (x < -80 || x > W + 80) continue;
                var seed = (int)(k * 7919 % 1000 + 1000);
                var r = new Random(seed);
                var coat = r.Next(3) switch { 0 => "#4B5E44", 1 => "#6B4A7A", _ => "#46607A" };
                SlideScene.Patron(n, x - 22, false, 246, 0.9, coat);
                if (r.NextDouble() < 0.6) SlideScene.Patron(n, x + 26, false, 246, 0.85, r.Next(2) == 0 ? "#7A5A3A" : "#5A6B44");
                // stůl s krýglem
                n.Add(new SPath(Rect(x - 52, 246, 104, 8), Fill: "#8A5A2E", Stroke: "#3E2210", StrokeWidth: 1.2));
                n.Add(new SPath(Rect(x - 4, 254, 8, FloorY - 254), Fill: "#6B3E1E"));
                n.Add(new SGroup(x + 8, 246 - PourScene.KryglHeight * 0.16, 0, null, PourScene.Krygl(0.6 + r.NextDouble() * 0.2, 0.75), 0.16));
            }

            // hospoda je v pozadí – trochu ji zamlžit, ať vynikne tác
            n.Add(new SPath(Rect(0, 0, W, FloorY), Fill: "#F3EAD6", FillOpacity: 0.35));
            // podlaha
            n.Add(new SPath(Rect(0, FloorY, W, H - FloorY), Fill: "#6E4A2C"));
            var planks = new StringBuilder();
            for (var y = FloorY + 14; y < H; y += 16) planks.Append($"M 0 {F(y)} L {F(W)} {F(y)} ");
            for (var x = -(scroll % 90); x < W; x += 90) planks.Append($"M {F(x)} {F(FloorY)} L {F(x - 30)} {F(H)} ");
            n.Add(new SPath(planks.ToString(), Stroke: "#4A2E18", StrokeWidth: 1.2, StrokeOpacity: 0.7));

            // střepy po rozbití (chvíli)
            if (g.T - g.LastCrashT < 1.2)
            {
                var sx = TrayX + g.LastCrashSide * (TrayHalf + 10) - (g.T - g.LastCrashT) * 70;
                n.Add(new SPath(Ellipse(sx, FloorY + 22, 26, 5), Fill: "#E2A532", FillOpacity: 0.8));
                var rr = new Random(9);
                for (var i = 0; i < 6; i++)
                {
                    var px = sx - 20 + rr.NextDouble() * 40; var py = FloorY + 18 + rr.NextDouble() * 8; var s = 3 + rr.NextDouble() * 5;
                    n.Add(new SPath($"M {F(px)} {F(py)} L {F(px + s)} {F(py - s * 0.6)} L {F(px + s * 0.4)} {F(py + s * 0.3)} Z",
                        Fill: "#E8F3F5", Stroke: "#5A3418", StrokeWidth: 0.8));
                }
                n.Add(new SText(sx - 18, FloorY - 6, "Cink!", 18, "#B3262E"));
            }

            // ruka (rukáv bílé košile) zespodu k tácu
            n.Add(new SPath($"M {F(TrayX + 70)} {F(H)} C {F(TrayX + 50)} {F(H - 60)} {F(TrayX + 20)} {F(TrayY + 70)} {F(TrayX + 4)} {F(TrayY + 22)} " +
                            $"L {F(TrayX - 18)} {F(TrayY + 26)} C {F(TrayX - 6)} {F(TrayY + 80)} {F(TrayX + 20)} {F(H - 50)} {F(TrayX + 24)} {F(H)} Z",
                Fill: "#FAFAF5", Stroke: "#5A3418", StrokeWidth: 1.6));
            n.Add(new SPath($"M {F(TrayX - 14)} {F(TrayY + 28)} L {F(TrayX + 6)} {F(TrayY + 24)}", Stroke: "#5A3418", StrokeWidth: 1.2, StrokeOpacity: 0.6));

            // tác s krýgly (otočený o náklon kolem dlaně)
            var tray = new List<SceneNode>();
            // dlaň pod tácem
            tray.Add(new SPath(Ellipse(-6, 14, 24, 9), Fill: "#E9C3A0", Stroke: "#5A3418", StrokeWidth: 1.4));
            var kh = PourScene.KryglHeight * KScale;
            for (var i = 0; i < WaiterGame.Beers; i++)
            {
                if (g.Glasses[i] is not double full) continue;
                var x = -TrayHalf + 20 + i * ((TrayHalf * 2 - 40) / (WaiterGame.Beers - 1));
                // hladina zůstane vodorovně: krýgl otočený s tácem, Krygl dostane náklon
                tray.Add(new SGroup(x, -kh - 2, 0, null, PourScene.Krygl(0.84 * full, Math.Min(1, 0.84 * full + 0.16 * Math.Min(1, full * 1.4)), -g.Angle), KScale));
            }
            tray.Add(new SPath(RoundRect(-TrayHalf, -3, TrayHalf * 2, 7, 3), Gradient: new SGradient(0, -3, 0, 4,
                new[] { (0.0, "#E3E6E8"), (0.5, "#9DA4A9"), (1.0, "#6E7478") }), Stroke: "#4E5458", StrokeWidth: 1));
            tray.Add(new SPath($"M {F(-TrayHalf)} -3 L {F(-TrayHalf - 4)} -9 M {F(TrayHalf)} -3 L {F(TrayHalf + 4)} -9", Stroke: "#6E7478", StrokeWidth: 2.5, Round: true));
            n.Add(new SGroup(TrayX, TrayY, g.Angle, null, tray));

            // šipka „zvedám“ na straně, kterou hráč právě zvedá (ať je vidět, co ovládání dělá)
            if (Math.Abs(g.Control) > 0.15 && g.State == WaiterGame.Phase.Walking)
            {
                var side = Math.Sign(g.Control);
                var rad0 = g.Angle * Math.PI / 180;
                var ax = TrayX + side * (TrayHalf - 6) * Math.Cos(rad0);
                var ay = TrayY + side * (TrayHalf - 6) * Math.Sin(rad0) + 16;
                var len = 14 + 14 * Math.Min(1, Math.Abs(g.Control));
                n.Add(new SPath($"M {F(ax)} {F(ay + len)} L {F(ax)} {F(ay)} M {F(ax - 6)} {F(ay + 7)} L {F(ax)} {F(ay)} L {F(ax + 6)} {F(ay + 7)}",
                    Stroke: "#3E8E41", StrokeWidth: 3.5, Round: true, StrokeOpacity: 0.9));
            }

            // kapky z nižší strany
            if (g.SpillSide != 0 && g.State == WaiterGame.Phase.Walking)
            {
                var rad = g.Angle * Math.PI / 180;
                var ex = TrayX + g.SpillSide * TrayHalf * Math.Cos(rad);
                var ey = TrayY + g.SpillSide * TrayHalf * Math.Sin(rad);
                var phase = g.T * 9 % 1;
                for (var i = 0; i < 4; i++)
                {
                    var t = (phase + i * 0.25) % 1;
                    n.Add(new SPath(Ellipse(ex + g.SpillSide * 3, ey + t * (FloorY - ey), 2.2, 3.4), Fill: "#E2A532", FillOpacity: 0.85));
                }
            }

            // ukazatel cesty ke stolu
            n.Add(new SPath(RoundRect(20, 14, 320, 8, 4), Fill: "#FFFFFF", FillOpacity: 0.7, Stroke: "#5A3418", StrokeWidth: 1));
            n.Add(new SPath(RoundRect(20, 14, Math.Max(8, 320 * g.Progress), 8, 4), Fill: "#C27A12"));
            n.Add(new SText(304, 38, "stůl", 12, "#5A3418"));
            // vodováha náklonu
            var lvl = Math.Clamp(g.Angle / WaiterGame.DropAt, -1, 1);
            var lvlColor = Math.Abs(g.Angle) < WaiterGame.SpillFrom ? "#3E8E41" : Math.Abs(g.Angle) < WaiterGame.DropAt * 0.8 ? "#C27A12" : "#B3262E";
            n.Add(new SPath(RoundRect(120, 32, 120, 12, 6), Fill: "#F2F7E6", Stroke: "#5A3418", StrokeWidth: 1));
            n.Add(new SPath($"M 168 32 L 168 44 M 192 32 L 192 44", Stroke: "#5A3418", StrokeWidth: 1, StrokeOpacity: 0.6));
            n.Add(new SPath(Ellipse(180 + lvl * 52, 38, 7, 4.5), Fill: lvlColor));

            if (g.Warning != null)
            {
                n.Add(new SPath(RoundRect(70, 56, 220, 26, 6), Fill: "#FFF4D6", Stroke: "#B3262E", StrokeWidth: 1.5));
                n.Add(new SText(82, 74, g.Warning, 14, "#B3262E"));
            }
            return n;
        }
    }
}
