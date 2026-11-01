using System.Text;
using static Dezolati.Services.SceneKit;

namespace Dezolati.Services
{
    /// <summary>
    /// Foukání pivního skla – hra pro jednoho (logika i obrázek sdílené appkou a webem).
    /// 1) naber sklovinu z pece (na jeden zátah, správné množství = správná tloušťka stěny),
    /// 2) předfoukni baňku a pořád toč píšťalou (jinak se sklo prověsí), sklo chladne → zpátky do pece,
    /// 3) ve formě dofoukni, ať sklo vyplní celý tvar – ne moc, praskne,
    /// 4) u krýglu nakresli prstem ucho.
    /// Fouká se do mikrofonu (stačí hlasitost, nic se nenahrává) nebo podržením tlačítka.
    /// </summary>
    public sealed class GlassGame
    {
        public enum Phase { Ready, Gather, Shape, Mold, Handle, Done }

        public Phase State { get; private set; } = Phase.Ready;
        public GlassShape Target { get; private set; } = GlassShape.All[0];

        // ---- vstupy ----
        /// <summary>Síla foukání 0–1 (mikrofon nebo tlačítko).</summary>
        public double Blow { get; set; }
        /// <summary>Píšťala v peci (nabírání / ohřívání).</summary>
        public bool InFurnace { get; set; }

        // ---- stav ----
        public double Glass { get; private set; }        // nabraná sklovina
        public double Heat { get; private set; } = 1;    // 1 = žhavé, 0 = studené
        public double Inflate { get; private set; }      // jak moc je baňka nafouknutá
        public double Sag { get; private set; }          // 0 = rovné, 1 = úplně prověšené
        public double Fill { get; private set; }         // jak moc sklo vyplnilo formu
        public double Over { get; private set; }         // přefouknutí ve formě
        public double RotSpeed { get; private set; }     // jak rychle točíš (px/s scény)
        public double Spin { get; private set; }         // úhel natočení (kvůli kresbě)
        public bool Cracked { get; private set; }
        public string? CrackReason { get; private set; }
        public bool Squashed { get; private set; }       // baňka byla větší než forma
        public List<(double X, double Y)> HandlePath { get; } = new();
        public double HandleQuality { get; private set; } = -1;
        public GlassResult? Result { get; private set; }
        public double T { get; private set; }

        private double rotAccum;

        public void Start(GlassShape target)
        {
            Target = target;
            State = Phase.Gather;
        }

        /// <summary>Poloměr baňky (scéna).</summary>
        public double BlobR => (10 + 14 * Glass) * (1 + Inflate);

        /// <summary>Tah prstem po obrázku = točení píšťalou (scéna px).</summary>
        public void AddRotation(double dx) => rotAccum += Math.Abs(dx);

        /// <summary>Konec nabírání (puštěné tlačítko) – na jeden zátah.</summary>
        public void EndGather()
        {
            if (State != Phase.Gather || Glass < 0.05) return;
            InFurnace = false;
            State = Phase.Shape;
        }

        /// <summary>Do formy (jde jen při tvarování).</summary>
        public void IntoMold()
        {
            if (State != Phase.Shape) return;
            InFurnace = false;
            Squashed = BlobR > Target.ParisonR * 1.12;
            State = Phase.Mold;
        }

        /// <summary>Otevřít formu = konec foukání.</summary>
        public void OpenMold()
        {
            if (State != Phase.Mold) return;
            Blow = 0;
            if (Target.HasHandle && !Cracked) State = Phase.Handle;
            else Finish();
        }

        public void HandleStart(double x, double y)
        {
            if (State != Phase.Handle) return;
            HandlePath.Clear();
            HandlePath.Add((x, y));
        }

        public void HandleMove(double x, double y)
        {
            if (State != Phase.Handle || HandlePath.Count == 0) return;
            var (lx, ly) = HandlePath[^1];
            if ((x - lx) * (x - lx) + (y - ly) * (y - ly) >= 9) HandlePath.Add((x, y));
        }

        public void HandleEnd()
        {
            if (State != Phase.Handle) return;
            if (HandlePath.Count < 6) { HandlePath.Clear(); return; }   // jen ťuk – kresli znovu
            HandleQuality = GlassScene.RateHandle(this);
            Finish();
        }

        public void SkipHandle()
        {
            if (State != Phase.Handle) return;
            HandlePath.Clear();
            HandleQuality = 0;
            Finish();
        }

        private double HeatFactor => Math.Clamp((Heat - 0.25) / 0.45, 0, 1);

        public void Step(double dt)
        {
            if (dt <= 0) return;
            dt = Math.Min(dt, 0.05);
            T += dt;

            // točení: rychlost z tahu prstem
            var inst = rotAccum / dt;
            rotAccum = 0;
            RotSpeed += (inst - RotSpeed) * Math.Min(1, dt * 5);
            Spin += RotSpeed * dt * 0.02;

            switch (State)
            {
                case Phase.Gather:
                    if (InFurnace)
                    {
                        Glass = Math.Min(1.3, Glass + 0.3 * dt);
                        Heat = 1;
                    }
                    break;

                case Phase.Shape:
                {
                    if (InFurnace)
                    {
                        Heat = Math.Min(1, Heat + 0.4 * dt);
                        break;   // v peci se nefouká
                    }
                    Heat = Math.Max(0, Heat - 0.075 * dt);
                    var rotOk = Math.Clamp(RotSpeed / 220, 0, 1);
                    // horké sklo bez točení teče dolů; točením se to trochu srovná
                    Sag = Math.Clamp(Sag + (1 - rotOk) * 0.12 * Heat * dt - rotOk * 0.03 * dt, 0, 1);
                    var eff = Blow * HeatFactor;
                    if (Blow > 0.5 && Heat < 0.18) { Crack("Foukal jsi do studeného skla – prasklo."); break; }
                    Inflate += eff * 0.2 * dt;
                    if (Inflate > 1.7) Crack("Baňka se přefoukla a praskla jako bublina.");
                    break;
                }

                case Phase.Mold:
                {
                    Heat = Math.Max(0, Heat - 0.04 * dt);
                    var eff = Blow * HeatFactor;
                    if (Fill < 1)
                    {
                        // víc skloviny = pomalejší (těžší) foukání
                        Fill = Math.Min(1.0001, Fill + eff * 0.42 * dt / (0.7 + Glass * 0.5));
                    }
                    else
                    {
                        Over += eff * dt;
                        if (Over > 0.7) Crack("Přefouknuto – stěna ve formě praskla.");
                    }
                    break;
                }
            }
        }

        private void Crack(string why)
        {
            Cracked = true;
            CrackReason = why;
            Blow = 0;
            Finish();
        }

        private void Finish()
        {
            State = Phase.Done;
            InFurnace = false;
            Blow = 0;
            if (Cracked)
            {
                Result = new GlassResult("Střepy", CrackReason ?? "Prasklo.", 0);
                return;
            }
            // tloušťka podle nabrané skloviny
            var (gLo, gHi) = Target.GatherRange;
            var gDist = Glass < gLo ? gLo - Glass : Glass > gHi ? Glass - gHi : 0;
            var thick = Math.Max(0, 25 - gDist * 90);
            // velikost baňky před formou
            var rel = BlobR / Target.ParisonR;
            var fit = Squashed ? 0 : Math.Max(0, 15 - Math.Max(0, Math.Abs(rel - 0.95) - 0.1) * 70);
            var sym = 20 * (1 - Sag);
            var fill = Fill >= 0.97 ? 30 : 30 * Fill * Fill;
            if (Over > 0.35) fill -= 6;
            var handle = Target.HasHandle ? 10 * Math.Max(0, HandleQuality) : 10;
            var score = (int)Math.Round(Math.Clamp(thick + fit + sym + fill + handle, 0, 100));

            var notes = new List<string>();
            if (Glass < gLo - 0.05) notes.Add("stěna je tenká jak papír");
            else if (Glass > gHi + 0.05) notes.Add("dno těžké jak cihla");
            if (Squashed) notes.Add("baňka se do formy nevešla a forma ji rozmačkala");
            if (Sag > 0.35) notes.Add("je to křivé – málo jsi točil");
            if (Fill < 0.9) notes.Add("sklo nevyplnilo formu, chybí kus dna");
            if (Over > 0.35) notes.Add("málem to prasklo");
            if (Target.HasHandle && HandleQuality < 0.4) notes.Add(HandleQuality <= 0 ? "bez ucha – pije se dvěma rukama" : "ucho vypadá jako vnitřnosti");

            var title = score >= 90 ? "Mistr sklář" : score >= 75 ? "Jde do výčepu" : score >= 55 ? "Druhá jakost" : "Na vázu pro babičku";
            var comment = notes.Count == 0 ? "Hospodský by to pověsil nad výčep." : char.ToUpper(notes[0][0]) + string.Join(", ", notes)[1..] + ".";
            Result = new GlassResult(title, comment, score);
        }
    }

    public sealed record GlassResult(string Title, string Comment, int Score);

    /// <summary>Tvar sklenice (svisle: hrdlo nahoře y=0, dno dole y=H; poloviční šířky podél výšky).</summary>
    public sealed record GlassShape(string Name, double H, (double F, double R)[] Profile, double ParisonR,
        (double Lo, double Hi) GatherRange, bool HasHandle)
    {
        public static readonly GlassShape[] All =
        {
            new("Krýgl", 140, new[] { (0.0, 46.0), (0.3, 49.0), (0.7, 47.0), (1.0, 42.0) }, 44, (0.62, 0.82), true),
            new("Pokál", 150, new[] { (0.0, 33.0), (0.25, 40.0), (0.55, 37.0), (0.82, 25.0), (1.0, 20.0) }, 38, (0.5, 0.7), false),
            new("Štamprle", 72, new[] { (0.0, 22.0), (0.5, 20.0), (1.0, 16.0) }, 22, (0.28, 0.44), false),
        };

        /// <summary>Poloviční šířka v hloubce y (0…H), plynule mezi body profilu.</summary>
        public double HalfAt(double y)
        {
            var f = Math.Clamp(y / H, 0, 1);
            for (var i = 1; i < Profile.Length; i++)
            {
                if (f > Profile[i].F) continue;
                var (f0, r0) = Profile[i - 1];
                var (f1, r1) = Profile[i];
                var t = (f - f0) / Math.Max(1e-6, f1 - f0);
                t = (1 - Math.Cos(t * Math.PI)) / 2;
                return r0 + (r1 - r0) * t;
            }
            return Profile[^1].R;
        }

        /// <summary>Obrys (svisle, střed hrdla = 0,0), se zaobleným dnem.</summary>
        public string Outline()
        {
            var sb = new StringBuilder($"M {F(-HalfAt(0))} 0");
            const int n = 24;
            for (var i = 1; i <= n; i++) { var y = H * i / n; sb.Append($" L {F(-HalfAt(y))} {F(y - (i == n ? 6 : 0))}"); }
            var b = HalfAt(H);
            sb.Append($" Q {F(-b)} {F(H)} {F(-b + 6)} {F(H)} L {F(b - 6)} {F(H)} Q {F(b)} {F(H)} {F(b)} {F(H - 6)}");
            for (var i = n - 1; i >= 0; i--) { var y = H * i / n; sb.Append($" L {F(HalfAt(y))} {F(y)}"); }
            return sb.Append(" Z").ToString();
        }
    }

    /// <summary>Obrázek sklárny (360 × 400).</summary>
    public static class GlassScene
    {
        private const double PipeY = 210, PipeEnd = 132;
        // hotová sklenice stojí na stole
        private const double TableY = 330, ShowX = 160;

        public static (double X, double Y) ShowTop(GlassGame g) => (ShowX, TableY - g.Target.H);

        public static List<SceneNode> Build(GlassGame g)
        {
            var n = new List<SceneNode>();
            const double W = 360, H = 400;
            // cihlová zeď sklárny – teplé cihly, od pece prozářené
            n.Add(new SPath(Rect(0, 0, W, 300), Gradient: new SGradient(0, 0, 0, 300,
                new[] { (0.0, "#A3603F"), (1.0, "#6E3A26") })));
            var bricks = new StringBuilder();
            var dark = new StringBuilder();
            var br = new Random(21);
            var row = 0;
            for (var y = 0.0; y < 300; y += 22, row++)
            {
                bricks.Append($"M 0 {F(y)} L {F(W)} {F(y)} ");
                for (var x = row % 2 == 0 ? 0.0 : 24.0; x < W; x += 48)
                {
                    bricks.Append($"M {F(x)} {F(y)} L {F(x)} {F(y + 22)} ");
                    if (br.NextDouble() < 0.22) dark.Append(Rect(x + 1.5, y + 1.5, 45, 19) + " ");
                }
            }
            if (dark.Length > 0) n.Add(new SPath(dark.ToString(), Fill: "#4A2216", FillOpacity: 0.28));
            n.Add(new SPath(bricks.ToString(), Stroke: "#E2C8AA", StrokeWidth: 1.6, StrokeOpacity: 0.45));
            // záře z pece přes zeď (zleva)
            n.Add(new SPath(Rect(0, 0, W, 300), Gradient: new SGradient(0, 0, 280, 0,
                new[] { (0.0, "#FFB25A"), (1.0, "#8A4A30") }), FillOpacity: g.InFurnace ? 0.45 : 0.3));
            // pracovní stůl / podlaha
            n.Add(new SPath(Rect(0, 300, W, 100), Gradient: new SGradient(0, 300, 0, 400,
                new[] { (0.0, "#6B5646"), (1.0, "#3A2E26") })));
            n.Add(new SPath($"M 0 300 L {F(W)} 300", Stroke: "#2A1F18", StrokeWidth: 3));

            // pec vlevo nahoře: kamenná kupole s klenbou z cihel a žhnoucím otvorem
            var glow = g.InFurnace ? "#FFF4C2" : "#FFD27A";
            n.Add(new SPath("M 0 36 L 70 36 Q 122 36 122 92 L 122 172 L 0 172 Z", Gradient: new SGradient(0, 36, 0, 172,
                new[] { (0.0, "#B3A391"), (1.0, "#6A5B4E") }), Stroke: "#3A2F27", StrokeWidth: 2.5));
            n.Add(new SPath(Ellipse(78, 112, 34, 38), Fill: "#7A3A22", Stroke: "#3A2F27", StrokeWidth: 2));
            n.Add(new SPath(Ellipse(78, 112, 46, 50), Fill: "#FFB347", FillOpacity: g.InFurnace ? 0.4 : 0.22));
            n.Add(new SPath(Ellipse(78, 112, 24, 28), Gradient: new SGradient(54, 0, 102, 0,
                new[] { (0.0, "#FF5A1F"), (0.5, glow), (1.0, "#FF5A1F") }), Stroke: "#2A1A12", StrokeWidth: 2));
            n.Add(new SPath(Ellipse(78, 118, 12, 10), Fill: "#FFFFFF", FillOpacity: g.InFurnace ? 0.55 : 0.3));

            switch (g.State)
            {
                case GlassGame.Phase.Ready:
                case GlassGame.Phase.Gather:
                    // píšťala vede do pece, na konci kapka skloviny
                    Pipe(n, 300, 260, 92, 120);
                    if (g.Glass > 0) n.Add(new SPath(Ellipse(86, 118, 6 + 10 * g.Glass, 5 + 9 * g.Glass), Fill: "#FFE08A", Stroke: "#FF7A2A", StrokeWidth: 1.5));
                    break;

                case GlassGame.Phase.Shape:
                    if (g.InFurnace)
                    {
                        Pipe(n, 300, 260, 92, 120);
                        n.Add(new SPath(Ellipse(86, 118, 8 + 10 * g.Glass, 7 + 9 * g.Glass), Fill: "#FFE08A"));
                    }
                    else
                    {
                        Pipe(n, 0, PipeY, PipeEnd, PipeY);
                        Blob(n, g);
                        // kam má baňka dorůst (velikost formy)
                        var tr = g.Target.ParisonR;
                        n.Add(new SPath(Ellipse(PipeEnd + tr * 0.95, PipeY, tr, tr), Stroke: "#FFFFFF", StrokeWidth: 1.5, StrokeOpacity: 0.45));
                    }
                    break;

                case GlassGame.Phase.Mold:
                    Pipe(n, 0, PipeY, PipeEnd, PipeY);
                    Mold(n, g);
                    break;

                case GlassGame.Phase.Handle:
                case GlassGame.Phase.Done:
                    Finished(n, g);
                    break;
            }

            // pod ukazateli tmavší pruh, ať jdou přečíst
            if (g.State is GlassGame.Phase.Gather or GlassGame.Phase.Shape or GlassGame.Phase.Mold)
                n.Add(new SPath(RoundRect(6, 6, 348, 42, 8), Fill: "#1E120C", FillOpacity: 0.45));

            // teploměr skla (barva) a točení
            if (g.State is GlassGame.Phase.Shape or GlassGame.Phase.Mold)
            {
                n.Add(new SPath(RoundRect(196, 14, 150, 10, 5), Fill: "#1E120C", Stroke: "#7A5A48", StrokeWidth: 1));
                n.Add(new SPath(RoundRect(196, 14, Math.Max(6, 150 * g.Heat), 10, 5), Fill: HeatColor(g.Heat)));
                n.Add(new SText(196, 40, g.Heat < 0.3 ? "studené – do pece!" : "teplota skla", 12, "#F2DCC8"));
                if (g.State == GlassGame.Phase.Shape && !g.InFurnace)
                {
                    var rot = Math.Clamp(g.RotSpeed / 220, 0, 1);
                    n.Add(new SPath(RoundRect(14, 14, 150, 10, 5), Fill: "#1E120C", Stroke: "#7A5A48", StrokeWidth: 1));
                    n.Add(new SPath(RoundRect(14, 14, Math.Max(6, 150 * rot), 10, 5), Fill: rot > 0.8 ? "#5DAA4E" : "#C9A23C"));
                    n.Add(new SText(14, 40, rot > 0.8 ? "točíš" : "toč píšťalou!", 12, "#F2DCC8"));
                }
            }
            if (g.State == GlassGame.Phase.Gather)
            {
                // kolik je nabráno (zelená zóna = akorát)
                var (lo, hi) = g.Target.GatherRange;
                n.Add(new SPath(RoundRect(196, 14, 150, 10, 5), Fill: "#1E120C", Stroke: "#7A5A48", StrokeWidth: 1));
                n.Add(new SPath(Rect(196 + 150 * lo / 1.3, 14, 150 * (hi - lo) / 1.3, 10), Fill: "#5DAA4E", FillOpacity: 0.6));
                n.Add(new SPath(RoundRect(196, 14, Math.Max(6, 150 * g.Glass / 1.3), 10, 5), Fill: "#FFB347", FillOpacity: 0.85));
                n.Add(new SText(196, 40, "sklovina", 12, "#F2DCC8"));
            }
            return n;
        }

        private static string HeatColor(double h) => h > 0.7 ? "#FFD27A" : h > 0.45 ? "#FF8A3C" : h > 0.25 ? "#C8442A" : "#8FA7AD";

        private static void Pipe(List<SceneNode> n, double x1, double y1, double x2, double y2)
        {
            n.Add(new SPath($"M {F(x1)} {F(y1)} L {F(x2)} {F(y2)}", Stroke: "#1A1A1A", StrokeWidth: 7, Round: true));
            n.Add(new SPath($"M {F(x1)} {F(y1 - 1.5)} L {F(x2)} {F(y2 - 1.5)}", Stroke: "#8A8F93", StrokeWidth: 2, StrokeOpacity: 0.8, Round: true));
        }

        private static void Blob(List<SceneNode> n, GlassGame g)
        {
            var r = g.BlobR;
            var cx = PipeEnd + r * 0.95;
            var cy = PipeY + g.Sag * r * 0.55;
            var color = HeatColor(g.Heat);
            var blob = new List<SceneNode>
            {
                // krček k píšťale
                new SPath($"M {F(PipeEnd - cx - 2)} -5 Q {F(-r * 0.6)} -6 {F(-r * 0.75)} {F(-r * 0.55)} L {F(-r * 0.75)} {F(r * 0.55)} Q {F(-r * 0.6)} 6 {F(PipeEnd - cx - 2)} 5 Z",
                    Fill: color),
                new SPath(Ellipse(0, 0, r * (1 + 0.12 * g.Sag), r * (1 - 0.06 * g.Sag)), Gradient: new SGradient(0, -r, 0, r,
                    new[] { (0.0, "#FFF3C4"), (0.5, color), (1.0, g.Heat > 0.3 ? "#C8442A" : "#6E8A90") }), Stroke: "#1E120C", StrokeWidth: 1.2),
            };
            // vzduch uvnitř (tloušťka stěny)
            var inner = r * g.Inflate / (1 + g.Inflate) * (1.15 - g.Glass * 0.25);
            if (inner > 2) blob.Add(new SPath(Ellipse(r * 0.05, 0, inner, inner * 0.92), Fill: "#2A1B14", FillOpacity: 0.35));
            // odlesk, ať je vidět točení
            var sx = Math.Cos(g.Spin) * r * 0.5;
            blob.Add(new SPath($"M {F(sx - 4)} {F(-r * 0.62)} Q {F(sx)} {F(-r * 0.8)} {F(sx + 6)} {F(-r * 0.6)}", Stroke: "#FFFFFF", StrokeWidth: 3, StrokeOpacity: 0.6, Round: true));
            n.Add(new SGroup(cx, cy, g.Sag * 28, null, blob));
        }

        private static void Mold(List<SceneNode> n, GlassGame g)
        {
            var t = g.Target;
            // forma: dvě železné poloviny kolem tvaru (hrdlo u píšťaly → otočeno o −90°)
            var mx = PipeEnd + 4;
            n.Add(new SPath(RoundRect(mx - 6, PipeY - 70, t.H + 30, 140, 8), Gradient: new SGradient(0, PipeY - 70, 0, PipeY + 70,
                new[] { (0.0, "#5C6166"), (0.5, "#3A3E42"), (1.0, "#24272A") }), Stroke: "#111111", StrokeWidth: 2));
            n.Add(new SPath($"M {F(mx - 6)} {F(PipeY)} L {F(mx + t.H + 24)} {F(PipeY)}", Stroke: "#111111", StrokeWidth: 1.5, StrokeOpacity: 0.7));
            var inside = new List<SceneNode>
            {
                new SPath(t.Outline(), Fill: "#16181A"),
            };
            // sklo vyplňuje tvar od hrdla
            var fillY = t.H * Math.Min(1, g.Fill) + 4;
            var glass = new List<SceneNode>
            {
                new SPath(Rect(-80, -2, 160, fillY), Gradient: new SGradient(0, 0, 0, t.H, new[] { (0.0, HeatColor(g.Heat)), (1.0, "#C8442A") }))
            };
            inside.Add(new SGroup(0, 0, 0, t.Outline(), glass));
            inside.Add(new SPath(t.Outline(), Stroke: "#8A9096", StrokeWidth: 1.5, StrokeOpacity: 0.8));   // tvar formy
            if (g.Fill >= 1 && g.Over > 0.15)
                inside.Add(new SPath(t.Outline(), Stroke: "#FF3B2A", StrokeWidth: 2 + g.Over * 4, StrokeOpacity: 0.8));
            n.Add(new SGroup(mx, PipeY, -90, null, inside));
            if (g.Squashed)
                n.Add(new SText(mx + 10, PipeY - 80, "nevešla se – rozmačkaná", 12, "#FF8A6A"));
        }

        private static void Finished(List<SceneNode> n, GlassGame g)
        {
            var t = g.Target;
            // stůl
            n.Add(new SPath(Rect(0, TableY, 360, 70), Gradient: new SGradient(0, TableY, 0, 400, new[] { (0.0, "#8A5A2E"), (1.0, "#4A2E18") })));
            if (g.Cracked)
            {
                var rnd = new Random(4);
                for (var i = 0; i < 9; i++)
                {
                    var x = ShowX - 50 + rnd.NextDouble() * 100; var y = TableY + 6 + rnd.NextDouble() * 20; var s = 5 + rnd.NextDouble() * 9;
                    n.Add(new SPath($"M {F(x)} {F(y)} L {F(x + s)} {F(y - s * 0.6)} L {F(x + s * 0.5)} {F(y + s * 0.4)} Z",
                        Fill: "#DCEEF2", FillOpacity: 0.85, Stroke: "#9DB8BE", StrokeWidth: 1));
                }
                return;
            }
            var stroke = 1.5 + Math.Clamp(g.Glass, 0, 1.3) * 3;
            var body = new List<SceneNode>();
            // nedofouknuté = chybí kus dna (ořez)
            var fillH = t.H * Math.Clamp(g.Fill, 0.3, 1);
            // hotová sklenice rovnou s pivem (ať je na tmavé zdi vidět)
            var beerTop = t.H * 0.22;
            var shape = new List<SceneNode>
            {
                new SPath(t.Outline(), Fill: "#DCEEF2", FillOpacity: 0.55),
                new SGroup(0, 0, 0, t.Outline(), new List<SceneNode>
                {
                    new SPath(Rect(-100, beerTop, 200, t.H), Gradient: new SGradient(0, beerTop, 0, t.H, new[] { (0.0, "#F2B230"), (1.0, "#B5650A") })),
                    new SPath(Rect(-100, beerTop - t.H * 0.12, 200, t.H * 0.12), Fill: "#FFF8E6"),
                }),
                new SPath(t.Outline(), Stroke: "#EAF6F8", StrokeWidth: stroke, StrokeOpacity: 0.95),
                new SPath($"M {F(-t.HalfAt(10) + 7)} 10 L {F(-t.HalfAt(t.H * 0.8) + 7)} {F(t.H * 0.8)}", Stroke: "#FFFFFF", StrokeWidth: 3, StrokeOpacity: 0.6, Round: true),
            };
            body.Add(new SGroup(0, 0, 0, Rect(-100, -10, 200, fillH + 10) , shape));
            if (g.Fill < 0.97)
                body.Add(new SPath($"M {F(-t.HalfAt(fillH))} {F(fillH)} Q 0 {F(fillH + 14)} {F(t.HalfAt(fillH))} {F(fillH)}", Stroke: "#EAF6F8", StrokeWidth: stroke));
            if (g.Over > 0.35)
                body.Add(new SPath($"M {F(t.HalfAt(t.H * 0.4) - 4)} {F(t.H * 0.35)} L {F(t.HalfAt(t.H * 0.4) - 12)} {F(t.H * 0.45)} L {F(t.HalfAt(t.H * 0.4) - 6)} {F(t.H * 0.52)}",
                    Stroke: "#9DB8BE", StrokeWidth: 1.2));
            var bottomY = TableY - (t.H - fillH);
            n.Add(new SPath(Ellipse(ShowX, TableY, t.HalfAt(t.H) + 8, 4), Fill: "#000000", FillOpacity: 0.25));
            // prověšení = nakřivo
            n.Add(new SGroup(ShowX, TableY - t.H + (t.H - fillH), g.Sag * 9, null, body));

            // ucho: kreslené prstem
            if (t.HasHandle)
            {
                var (ax, ay, bx, by) = HandleAnchors(g);
                if (g.State == GlassGame.Phase.Handle && g.HandlePath.Count == 0)
                {
                    n.Add(new SPath(Ellipse(ax, ay, 6, 6) + " " + Ellipse(bx, by, 6, 6), Fill: "#FFD27A", FillOpacity: 0.8));
                    n.Add(new SPath($"M {F(ax + 4)} {F(ay)} C {F(ax + 50)} {F(ay)} {F(bx + 50)} {F(by)} {F(bx + 4)} {F(by)}",
                        Stroke: "#FFD27A", StrokeWidth: 2, StrokeOpacity: 0.35));
                }
                if (g.HandlePath.Count > 1)
                {
                    var sb = new StringBuilder($"M {F(g.HandlePath[0].X)} {F(g.HandlePath[0].Y)}");
                    foreach (var (x, y) in g.HandlePath.Skip(1)) sb.Append($" L {F(x)} {F(y)}");
                    var d = sb.ToString();
                    n.Add(new SPath(d, Stroke: "#9DB8BE", StrokeWidth: 13, Round: true));
                    n.Add(new SPath(d, Stroke: "#DCEEF2", StrokeWidth: 9, Round: true));
                    n.Add(new SPath(d, Stroke: "#FFFFFF", StrokeWidth: 2.5, StrokeOpacity: 0.8, Round: true));
                }
            }
            _ = bottomY;
        }

        /// <summary>Kde má ucho začínat a končit (pravý bok sklenice).</summary>
        public static (double Ax, double Ay, double Bx, double By) HandleAnchors(GlassGame g)
        {
            var t = g.Target;
            var top = TableY - t.H;
            return (ShowX + t.HalfAt(t.H * 0.18) - 2, top + t.H * 0.18, ShowX + t.HalfAt(t.H * 0.78) - 2, top + t.H * 0.78);
        }

        /// <summary>Ucho: začátek a konec u kotev (v libovolném pořadí), vyboulené doprava o 20–60 px.</summary>
        public static double RateHandle(GlassGame g)
        {
            var p = g.HandlePath;
            if (p.Count < 6) return 0;
            var (ax, ay, bx, by) = HandleAnchors(g);
            double D((double X, double Y) a, double x, double y) => Math.Sqrt((a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y));
            var d1 = Math.Min(D(p[0], ax, ay) + D(p[^1], bx, by), D(p[0], bx, by) + D(p[^1], ax, ay));
            var bulge = p.Max(q => q.X) - Math.Max(ax, bx);
            var q = 1 - Math.Max(0, d1 - 12) / 70;
            if (bulge < 18) q -= (18 - bulge) / 30;
            if (bulge > 65) q -= (bulge - 65) / 40;
            // klikatost: délka čáry proti ideálnímu oblouku
            var len = 0.0;
            for (var i = 1; i < p.Count; i++) len += D(p[i - 1], p[i].X, p[i].Y);
            var ideal = Math.Abs(by - ay) + 2 * Math.Max(18, bulge);
            if (len > ideal * 1.5) q -= (len / ideal - 1.5) * 0.6;
            return Math.Clamp(q, 0, 1);
        }
    }
}
