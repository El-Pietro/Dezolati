using System.Text;
using System.Text.Json;
using static Dezolati.Services.SceneKit;

namespace Dezolati.Services
{
    /// <summary>
    /// Vaření piva – hra pro jednoho, po česku (logika i obrázek sdílené appkou a webem).
    /// 1) Rmutování dekokcí (trojrmutový postup): ze zapářky 35 °C přes prodlevy 52, 65 a 77 °C.
    ///    Teplota se nezvedá hořákem: odebere se díl rmutu, povaří se a vrací se zpátky – drž, dokud
    ///    teploměr neukáže cíl (teploměr se kvůli míchání opožďuje). Přestřelit = zničené enzymy.
    /// 2) Chmelovar (90 minut za 30 s): tři dávky chmele – na hořkost, na chuť, na vůni. A nenechat překypět.
    /// 3) Hlavní kvašení (7 dní za 25 s): spodní kvašení 8–10 °C, sklep se ohřívá → chladit.
    /// 4) Výsledek: stupňovitost, alkohol, hořkost, čirost, posudek štamgastů; pivo se pojmenuje a uloží do sklepa.
    /// </summary>
    public sealed class BrewGame
    {
        public enum Phase { Ready, Mash, Boil, Ferment, Done }
        public enum MashStep { Take, Boiling, Return, Rest }

        public static readonly double[] Targets = { 52, 65, 77 };
        public static readonly string[] RestNames = { "bílkovinná prodleva", "cukrotvorná prodleva", "odrmutování" };
        public static readonly (double From, double To, string Name)[] HopWindows =
            { (0, 10, "na hořkost"), (45, 60, "na chuť"), (80, 90, "na vůni") };
        private const double DecoctionT = 97;           // povařený rmut po přenesení
        private const double PortionSize = 0.45;        // kolik rmutu se odebere k povaření
        public const double BoilSeconds = 30, FermentSeconds = 25;

        public Phase State { get; private set; } = Phase.Ready;
        public MashStep Sub { get; private set; }
        public int Decoction { get; private set; }
        public double TrueT { get; private set; } = 35;
        public double ShownT { get; private set; } = 35;
        public double Returned { get; private set; }
        public bool Pouring { get; set; }
        public double SubT { get; private set; }
        public double[] MashErr { get; } = new double[3];
        private double startT = 35;

        // chmelovar
        public double BoilT { get; private set; }
        public double BoilMinute => BoilT / BoilSeconds * 90;
        public List<double> Hops { get; } = new();
        public double HopFlyT { get; private set; } = -10;
        public bool FoamEvent { get; private set; }
        public double FoamLevel { get; private set; }
        public int Boilovers { get; private set; }
        public double LastBoiloverT { get; private set; } = -10;
        private readonly List<double> foamAt = new();

        // kvašení
        public double FermT { get; private set; }
        public double FermDay => FermT / FermentSeconds * 7;
        public double Temp { get; private set; } = 9;
        public bool Cool { get; set; }
        public string? FermEvent { get; private set; }
        private double fermEventUntil, fermEventDrift, inRange, noise, maxT = 9, minT = 9;
        private readonly List<double> fermEvents = new();

        public BrewResult? Result { get; private set; }
        private readonly Random rnd;

        public BrewGame(int? seed = null)
        {
            rnd = seed is int s ? new Random(s) : new Random();
            foamAt.Add(6 + rnd.NextDouble() * 5);
            foamAt.Add(17 + rnd.NextDouble() * 6);
            fermEvents.Add(5 + rnd.NextDouble() * 5);
            fermEvents.Add(14 + rnd.NextDouble() * 6);
        }

        public double Target => Targets[Math.Min(Decoction, 2)];

        public void Start()
        {
            if (State != Phase.Ready) return;
            State = Phase.Mash;
            Sub = MashStep.Take;
        }

        /// <summary>Odebrat díl rmutu k povaření.</summary>
        public void TakePortion()
        {
            if (State != Phase.Mash || Sub != MashStep.Take) return;
            Sub = MashStep.Boiling;
            SubT = 0;
            Returned = 0;
            startT = TrueT;
        }

        /// <summary>Dost vráceno – drží se prodleva.</summary>
        public void ConfirmRest()
        {
            if (State != Phase.Mash || Sub != MashStep.Return || Returned < 0.01) return;
            Pouring = false;
            Sub = MashStep.Rest;
            SubT = 0;
        }

        public bool CanConfirm => State == Phase.Mash && Sub == MashStep.Return && Returned >= 0.01;

        public void AddHop()
        {
            if (State != Phase.Boil || Hops.Count >= 3) return;
            Hops.Add(BoilMinute);
            HopFlyT = BoilT;
        }

        public void CalmFire()
        {
            if (State != Phase.Boil || !FoamEvent) return;
            FoamEvent = false;
            FoamLevel = 0;
        }

        public void Step(double dt)
        {
            if (dt <= 0) return;
            dt = Math.Min(dt, 0.05);
            switch (State)
            {
                case Phase.Mash: StepMash(dt); break;
                case Phase.Boil: StepBoil(dt); break;
                case Phase.Ferment: StepFerment(dt); break;
            }
        }

        private void StepMash(double dt)
        {
            SubT += dt;
            switch (Sub)
            {
                case MashStep.Boiling:
                    if (SubT >= 3) { Sub = MashStep.Return; SubT = 0; }
                    break;
                case MashStep.Return:
                    if (Pouring && Returned < PortionSize)
                    {
                        Returned = Math.Min(PortionSize, Returned + 0.04 * dt);
                        var rest = 1 - PortionSize;
                        TrueT = (rest * startT + Returned * DecoctionT) / (rest + Returned);
                    }
                    if (Returned >= PortionSize - 1e-9) ConfirmRest();
                    break;
                case MashStep.Rest:
                    if (SubT >= 2.2)
                    {
                        MashErr[Decoction] = TrueT - Targets[Decoction];
                        Decoction++;
                        if (Decoction >= 3)
                        {
                            State = Phase.Boil;
                            BoilT = 0;
                        }
                        else Sub = MashStep.Take;
                    }
                    break;
            }
            // teploměr se kvůli míchání opožďuje
            ShownT += (TrueT - ShownT) * Math.Min(1, dt / 1.1);
        }

        private void StepBoil(double dt)
        {
            BoilT += dt;
            if (!FoamEvent && foamAt.Count > 0 && BoilT >= foamAt[0])
            {
                foamAt.RemoveAt(0);
                FoamEvent = true;
                FoamLevel = 0;
            }
            if (FoamEvent)
            {
                FoamLevel += dt * 0.55;
                if (FoamLevel >= 1)
                {
                    Boilovers++;
                    LastBoiloverT = BoilT;
                    FoamEvent = false;
                    FoamLevel = 0;
                }
            }
            if (BoilT >= BoilSeconds)
            {
                FoamEvent = false;
                State = Phase.Ferment;
                FermT = 0;
            }
        }

        private void StepFerment(double dt)
        {
            FermT += dt;
            if (fermEvents.Count > 0 && FermT >= fermEvents[0])
            {
                fermEvents.RemoveAt(0);
                var warm = rnd.NextDouble() < 0.7;
                FermEvent = warm ? "Sklep se ohřívá!" : "Venku mrzne, sklep chladne!";
                fermEventDrift = warm ? 0.9 : -0.9;
                fermEventUntil = FermT + 4;
            }
            if (FermEvent != null && FermT >= fermEventUntil) { FermEvent = null; fermEventDrift = 0; }
            noise += ((rnd.NextDouble() - 0.5) * 2 - noise) * Math.Min(1, dt * 1.5);
            var drift = 0.32 + fermEventDrift + noise * 0.35 + (Cool ? -1.3 : 0);
            Temp += drift * dt;
            maxT = Math.Max(maxT, Temp);
            minT = Math.Min(minT, Temp);
            inRange += dt * (Temp >= 8 && Temp <= 10 ? 1 : Temp >= 7 && Temp <= 11 ? 0.5 : 0);
            if (FermT >= FermentSeconds)
            {
                Cool = false;
                FermEvent = null;
                Finish();
            }
        }

        private void Finish()
        {
            State = Phase.Done;
            // rmut
            var mashScores = MashErr.Select(e => Math.Clamp(100 - (e > 0 ? e * 12 : -e * 7), 0, 100)).ToArray();
            var mash = mashScores.Average();
            // chmel: i-tá dávka do i-tého okna
            var hops = Hops.OrderBy(h => h).ToList();
            var hopScore = 0.0;
            for (var i = 0; i < 3; i++)
            {
                if (i >= hops.Count) continue;
                var (a, b, _) = HopWindows[i];
                var dist = hops[i] < a ? a - hops[i] : hops[i] > b ? hops[i] - b : 0;
                hopScore += Math.Max(0, 100 - dist * 4) / 3;
            }
            var boil = Math.Max(0, hopScore - Boilovers * 15);
            var ferment = Math.Clamp(inRange / FermentSeconds * 100, 0, 100);
            var score = (int)Math.Round(0.4 * mash + 0.3 * boil + 0.3 * ferment);

            var e52 = MashErr[0]; var e65 = MashErr[1]; var e77 = MashErr[2];
            var degree = Math.Round(Math.Clamp(12 - Math.Abs(e65) * 0.18 - Math.Max(0, -e77) * 0.08, 9, 13));
            var attenuation = Math.Clamp(0.78 - Math.Max(0, e65) * 0.03 + Math.Max(0, -e65) * 0.012, 0.5, 0.86);
            var abv = Math.Round(degree * 0.42 * attenuation / 0.78, 1);
            var ibu = (int)Math.Round(hops.Sum(m => 14 * (90 - m) / 90 + 4));
            var hazy = Math.Abs(e52) > 4 || ferment < 50;

            var notes = new List<string>();
            if (e65 > 3) notes.Add("sladké a těžké jak sirup – enzymy jsi uvařil");
            else if (e65 < -3) notes.Add("vodové a suché, chybí mu tělo");
            if (ibu >= 45) notes.Add("hořké jak pelyněk");
            else if (ibu < 18) notes.Add(hops.Count == 0 ? "chmel nevidělo ani z rychlíku" : "skoro bez hořkosti, jak limonáda");
            if (!hops.Any(h => h >= 75) && hops.Count > 0) notes.Add("nevoní chmelem");
            if (Boilovers > 0) notes.Add(Boilovers == 1 ? "jednou to překypělo, varna smrdí spáleninou" : "dvakrát to překypělo, sládek pláče");
            if (maxT > 12) notes.Add("kvasilo moc teplé – je cítit máslem a ovocem");
            else if (minT < 6.5) notes.Add("kvasinky v zimě usnuly, nedokvašené");
            if (hazy) notes.Add("zakalené");
            if (Math.Abs(e77) > 4) notes.Add("špatně se scezovalo");

            var title = score >= 90 ? "Sládek roku" : score >= 75 ? "Ležák, za který se nestydíš"
                : score >= 55 ? "Pitelné, po třetím" : score >= 35 ? "Na vaření guláše" : "Do kanálu";
            var comment = notes.Count == 0 ? "Štamgasti mlčí a pijou. Lepší pochvalu nedostaneš."
                : char.ToUpper(notes[0][0]) + string.Join(", ", notes)[1..] + ".";
            Result = new BrewResult(title, comment, score, degree, abv, ibu, hazy ? "zakalené" : "jiskrné",
                (int)mash, (int)boil, (int)ferment);
        }
    }

    public sealed record BrewResult(string Title, string Comment, int Score, double Degree, double Abv, int Ibu, string Clarity,
        int MashScore, int BoilScore, int FermentScore)
    {
        public string Summary => $"{Degree:0}° · {Abv:0.0} % · {Ibu} IBU · {Clarity}";
    }

    /// <summary>Uvařená piva v telefonu (žádná Firebase).</summary>
    public static class BrewCellar
    {
        private const string Key = "hry_varna_sklep";
        public sealed record Beer(string Name, double Degree, double Abv, int Ibu, int Score, DateTime At);

        public static readonly string[] Names =
        {
            "Dezolátský ležák", "Štamgastova radost", "Kyselý Franta", "Sládkova chyba", "Hospodská dvanáctka",
            "Pivo z garáže", "Ranní kocovina", "Plzeň to není", "Zlatý mok z vany", "Čtvrteční výčep"
        };

        public static List<Beer> Load()
        {
            try { return JsonSerializer.Deserialize<List<Beer>>(Preferences.Get(Key, "[]")) ?? new(); }
            catch { return new(); }
        }

        public static void Add(Beer b)
        {
            var list = Load();
            list.Insert(0, b);
            if (list.Count > 12) list = list.OrderByDescending(x => x.Score).Take(12).ToList();
            Preferences.Set(Key, JsonSerializer.Serialize(list));
        }

        public static string Text(List<Beer> list, int max = 5) =>
            list.Count == 0 ? "Sklep je zatím prázdný."
                : string.Join("\n", list.OrderByDescending(b => b.Score).Take(max)
                    .Select(b => $"{b.Name} – {b.Degree:0}°, {b.Abv:0.0} %, {b.Ibu} IBU, {b.Score} bodů"));
    }

    /// <summary>Obrázek varny / spilky (360 × 400).</summary>
    public static class BrewScene
    {
        private const string Copper = "#B8733A", CopperDark = "#7A4220", Ink = "#3B2314";

        public static List<SceneNode> Build(BrewGame g)
        {
            var n = new List<SceneNode>();
            // zeď varny (dlaždičky) a podlaha
            n.Add(new SPath(Rect(0, 0, 360, 400), Fill: "#E8DFCB"));
            var tiles = new StringBuilder();
            for (var y = 0.0; y < 310; y += 24) tiles.Append($"M 0 {F(y)} L 360 {F(y)} ");
            for (var x = 0.0; x < 360; x += 24) tiles.Append($"M {F(x)} 0 L {F(x)} 310 ");
            n.Add(new SPath(tiles.ToString(), Stroke: "#CFC2A6", StrokeWidth: 1, StrokeOpacity: 0.7));
            n.Add(new SPath(Rect(0, 310, 360, 90), Fill: "#7A6A58"));
            n.Add(new SPath("M 0 310 L 360 310", Stroke: "#5A4C3C", StrokeWidth: 2));

            switch (g.State)
            {
                case BrewGame.Phase.Ready:
                case BrewGame.Phase.Mash:
                    Mash(n, g);
                    break;
                case BrewGame.Phase.Boil:
                    Boil(n, g);
                    break;
                case BrewGame.Phase.Ferment:
                    Ferment(n, g);
                    break;
                case BrewGame.Phase.Done:
                    Done(n, g);
                    break;
            }
            return n;
        }

        private static void Kettle(List<SceneNode> n, double cx, double top, double w, double h, double fill, string liquid, bool fire, bool bubbles, double t)
        {
            // měděný kotel s kopulí
            n.Add(new SPath($"M {F(cx - w / 2)} {F(top)} L {F(cx - w / 2)} {F(top + h - 20)} Q {F(cx - w / 2)} {F(top + h)} {F(cx - w / 2 + 20)} {F(top + h)} " +
                            $"L {F(cx + w / 2 - 20)} {F(top + h)} Q {F(cx + w / 2)} {F(top + h)} {F(cx + w / 2)} {F(top + h - 20)} L {F(cx + w / 2)} {F(top)} Z",
                Gradient: new SGradient(cx - w / 2, 0, cx + w / 2, 0, new[] { (0.0, CopperDark), (0.35, "#E0A060"), (0.6, Copper), (1.0, CopperDark) }),
                Stroke: Ink, StrokeWidth: 1.6));
            // hladina (průhled okénkem)
            var winTop = top + 18; var winH = h - 44;
            n.Add(new SPath(RoundRect(cx - w * 0.28, winTop, w * 0.56, winH, 6), Fill: "#2A1B10", Stroke: "#5A3418", StrokeWidth: 2));
            var lvl = winTop + winH * (1 - Math.Clamp(fill, 0, 1));
            n.Add(new SGroup(0, 0, 0, RoundRect(cx - w * 0.28, winTop, w * 0.56, winH, 6), new List<SceneNode>
            {
                new SPath(Rect(cx - w * 0.3, lvl, w * 0.6, winTop + winH - lvl + 2), Fill: liquid),
            }));
            if (bubbles)
            {
                var rr = new Random(3);
                for (var i = 0; i < 8; i++)
                {
                    var bx = cx - w * 0.24 + rr.NextDouble() * w * 0.48;
                    var by = lvl + ((rr.NextDouble() * 40 - t * 30) % 40 + 40) % 40;
                    if (by > winTop + winH - 4) continue;
                    n.Add(new SPath(Ellipse(bx, by, 2, 2), Stroke: "#F5E6C0", StrokeWidth: 1, StrokeOpacity: 0.8));
                }
            }
            // kopule
            n.Add(new SPath($"M {F(cx - w / 2 - 4)} {F(top)} Q {F(cx)} {F(top - h * 0.45)} {F(cx + w / 2 + 4)} {F(top)} Z",
                Gradient: new SGradient(cx - w / 2, 0, cx + w / 2, 0, new[] { (0.0, CopperDark), (0.4, "#E8B070"), (1.0, CopperDark) }), Stroke: Ink, StrokeWidth: 1.6));
            n.Add(new SPath(RoundRect(cx - 7, top - h * 0.45 - 30, 14, 34, 3), Gradient: new SGradient(cx - 7, 0, cx + 7, 0,
                new[] { (0.0, CopperDark), (0.5, "#E8B070"), (1.0, CopperDark) }), Stroke: Ink, StrokeWidth: 1.2));
            // nožičky a oheň
            n.Add(new SPath($"M {F(cx - w * 0.35)} {F(top + h)} L {F(cx - w * 0.38)} 310 M {F(cx + w * 0.35)} {F(top + h)} L {F(cx + w * 0.38)} 310",
                Stroke: "#3A3A3A", StrokeWidth: 4));
            if (fire)
            {
                var flick = Math.Sin(t * 20) * 3;
                n.Add(new SPath($"M {F(cx - 24)} {F(top + h + 22)} Q {F(cx - 14)} {F(top + h + 2 + flick)} {F(cx - 6)} {F(top + h + 22)} Q {F(cx)} {F(top + h - 4 - flick)} {F(cx + 8)} {F(top + h + 22)} Q {F(cx + 16)} {F(top + h + 4 + flick)} {F(cx + 24)} {F(top + h + 22)} Z",
                    Gradient: new SGradient(0, top + h, 0, top + h + 22, new[] { (0.0, "#FFE27A"), (1.0, "#FF5A1F") })));
            }
        }

        private static void Thermometer(List<SceneNode> n, double x, double top, double h, double tShown, double? target, double min, double max)
        {
            n.Add(new SPath(RoundRect(x - 9, top, 18, h, 9), Fill: "#FFFFFF", Stroke: Ink, StrokeWidth: 1.4));
            n.Add(new SPath(Ellipse(x, top + h + 6, 12, 12), Fill: "#C0392B", Stroke: Ink, StrokeWidth: 1.4));
            var frac = Math.Clamp((tShown - min) / (max - min), 0, 1);
            n.Add(new SPath(Rect(x - 4, top + h * (1 - frac), 8, h * frac + 4), Fill: "#C0392B"));
            if (target is double tg)
            {
                var ty = top + h * (1 - (tg - min) / (max - min));
                n.Add(new SPath($"M {F(x - 16)} {F(ty)} L {F(x + 16)} {F(ty)}", Stroke: "#2E7D32", StrokeWidth: 2.5));
                n.Add(new SText(x + 18, ty + 4, $"{tg:0} °C", 12, "#2E7D32"));
            }
            n.Add(new SText(x - 22, top - 6, $"{tShown:0.0} °C", 14, Ink));
        }

        private static void Mash(List<SceneNode> n, BrewGame g)
        {
            var t = g.State == BrewGame.Phase.Mash ? g.SubT : 0;
            // vlevo rmutovací kád, vpravo pánev (povařuje se díl rmutu)
            var mainFill = 0.75 - (g.Sub is BrewGame.MashStep.Boiling ? 0.3 : g.Sub == BrewGame.MashStep.Return ? 0.3 - g.Returned * 0.66 : 0);
            Kettle(n, 110, 150, 150, 130, mainFill, "#C9A35E", false, false, t);
            var panFill = g.Sub == BrewGame.MashStep.Boiling ? 0.7 : g.Sub == BrewGame.MashStep.Return ? Math.Max(0, 0.7 - g.Returned * 1.55) : 0.05;
            Kettle(n, 270, 200, 100, 85, panFill, "#B88A40", g.Sub is BrewGame.MashStep.Boiling or BrewGame.MashStep.Return, g.Sub == BrewGame.MashStep.Boiling, t);
            // vracení: proud z pánve do kádě
            if (g.Pouring && g.Sub == BrewGame.MashStep.Return)
                n.Add(new SPath("M 236 150 C 210 110 180 105 150 128", Stroke: "#B88A40", StrokeWidth: 7, Round: true));
            Thermometer(n, 26, 60, 200, g.ShownT, g.State == BrewGame.Phase.Mash ? g.Target : null, 30, 85);
            if (g.Sub == BrewGame.MashStep.Boiling)
                n.Add(new SText(228, 330, $"povařuje se… {Math.Max(0, 3 - g.SubT):0.0} s", 13, Ink));
            if (g.Sub == BrewGame.MashStep.Rest)
                n.Add(new SText(60, 340, g.State == BrewGame.Phase.Mash ? $"{BrewGame.RestNames[g.Decoction]} – {g.TrueT:0.0} °C" : "", 15, "#2E7D32"));
            // které rmutování
            for (var i = 0; i < 3; i++)
            {
                var done = i < g.Decoction;
                var cur = i == g.Decoction && g.State == BrewGame.Phase.Mash;
                n.Add(new SPath(Ellipse(250 + i * 30, 30, 10, 10), Fill: done ? "#2E7D32" : cur ? "#C27A12" : "#FFFFFF", Stroke: Ink, StrokeWidth: 1.4));
                n.Add(new SText(245 + i * 30, 54, $"{BrewGame.Targets[i]:0}", 11, Ink));
            }
        }

        private static void Boil(List<SceneNode> n, BrewGame g)
        {
            Kettle(n, 180, 140, 190, 150, 0.7 + (g.FoamEvent ? g.FoamLevel * 0.3 : 0), "#B98A38", true, true, g.BoilT);
            // pěna při varu
            if (g.FoamEvent)
            {
                var y = 140 + 18 + (150 - 44) * (1 - (0.7 + g.FoamLevel * 0.3));
                n.Add(new SPath(Rect(180 - 190 * 0.28, y - 10, 190 * 0.56, 12), Fill: "#FFF4D6"));
                // hláška v cedulce nad kotlem (ať nepřekrývá poklici)
                n.Add(new SPath(RoundRect(196, 50, 150, 30, 6), Fill: "#FFF4D6", Stroke: "#B3262E", StrokeWidth: 1.5));
                n.Add(new SText(206, 71, "Kypí to! Ztlum oheň!", 14, "#B3262E"));
            }
            if (g.BoilT - g.LastBoiloverT < 1.5)
                n.Add(new SText(206, 71, "Překypělo…", 14, "#B3262E"));
            // letící chmel
            var since = g.BoilT - g.HopFlyT;
            if (since < 0.8)
            {
                var hx = 330 - since * 180; var hy = 60 + since * since * 120;
                Hop(n, hx, hy);
            }
            // časová osa vaření s okny na chmel
            const double x0 = 20, w = 320, y0 = 344;
            n.Add(new SPath(RoundRect(x0, y0, w, 14, 7), Fill: "#FFFFFF", Stroke: Ink, StrokeWidth: 1.2));
            foreach (var (a, b, name) in BrewGame.HopWindows)
            {
                n.Add(new SPath(Rect(x0 + w * a / 90, y0, w * (b - a) / 90, 14), Fill: "#5DAA4E", FillOpacity: 0.45));
                n.Add(new SText(x0 + w * a / 90, y0 + 30, name, 11, Ink));
            }
            foreach (var h in g.Hops) Hop(n, x0 + w * h / 90, y0 + 7, 0.6);
            n.Add(new SPath($"M {F(x0 + w * g.BoilMinute / 90)} {F(y0 - 4)} L {F(x0 + w * g.BoilMinute / 90)} {F(y0 + 18)}", Stroke: "#B3262E", StrokeWidth: 3));
            n.Add(new SText(20, 28, $"Chmelovar: {g.BoilMinute:0} / 90 min · chmel {g.Hops.Count}/3", 14, Ink));
        }

        private static void Hop(List<SceneNode> n, double x, double y, double s = 1)
        {
            // chmelová hlávka: šupinky
            var p = new List<SceneNode>();
            for (var i = 0; i < 4; i++)
                p.Add(new SPath(Ellipse(0, -8 + i * 5, 7 - i * 1.2, 5), Fill: i % 2 == 0 ? "#7FB24A" : "#6A9A3A", Stroke: "#3E5E20", StrokeWidth: 0.8));
            p.Add(new SPath("M 0 -13 L 2 -18", Stroke: "#3E5E20", StrokeWidth: 1.5, Round: true));
            n.Add(new SGroup(x, y, 0, null, p, s));
        }

        private static void Ferment(List<SceneNode> n, BrewGame g)
        {
            // otevřená kvasná káď ve spilce
            n.Add(new SPath("M 70 150 L 80 300 L 260 300 L 270 150 Z", Gradient: new SGradient(70, 0, 270, 0,
                new[] { (0.0, "#6B4A2A"), (0.5, "#9A6E40"), (1.0, "#6B4A2A") }), Stroke: Ink, StrokeWidth: 1.6));
            for (var y = 175.0; y < 300; y += 40) n.Add(new SPath($"M {F(70 + (y - 150) / 15)} {F(y)} L {F(270 - (y - 150) / 15)} {F(y)}", Stroke: "#3A3A3A", StrokeWidth: 4));
            // kvasnicová deka (kroužky pěny)
            n.Add(new SPath(Ellipse(170, 152, 100, 10), Fill: "#F3E6C4", Stroke: Ink, StrokeWidth: 1.2));
            var rr = new Random(5);
            for (var i = 0; i < 12; i++)
                n.Add(new SPath(Ellipse(90 + rr.NextDouble() * 160, 150 + rr.NextDouble() * 5, 6 + rr.NextDouble() * 6, 3), Fill: "#FFF8E6", Stroke: "#D8C08A", StrokeWidth: 0.8));
            // chladicí had
            var cold = g.Cool;
            n.Add(new SPath("M 270 170 L 300 170 L 300 290 L 270 290", Stroke: cold ? "#5AA9E6" : "#8A8F93", StrokeWidth: 6, Round: true));
            if (cold) n.Add(new SText(282, 160, "❄", 18, "#5AA9E6"));
            Thermometer(n, 26, 80, 180, g.Temp, null, 4, 16);
            // zelené pásmo 8–10
            var top = 80.0; var h = 180.0;
            double Y(double t) => top + h * (1 - (t - 4) / 12);
            n.Add(new SPath(Rect(40, Y(10), 8, Y(8) - Y(10)), Fill: "#2E7D32", FillOpacity: 0.7));
            n.Add(new SText(20, 28, $"Hlavní kvašení: den {Math.Min(7, g.FermDay + 1):0} ze 7 · drž 8–10 °C", 14, Ink));
            if (g.FermEvent != null) n.Add(new SText(80, 120, g.FermEvent, 15, "#B3262E"));
        }

        private static void Done(List<SceneNode> n, BrewGame g)
        {
            // stůl s krýglem tvého piva
            n.Add(new SPath(Rect(0, 290, 360, 110), Gradient: new SGradient(0, 290, 0, 400, new[] { (0.0, "#8A5A2E"), (1.0, "#4A2E18") })));
            var hazy = g.Result?.Clarity == "zakalené";
            n.Add(new SGroup(180, 290 - PourScene.KryglHeight * 1.1, 0, null, PourScene.Krygl(PourGame.Mark / PourGame.Rim, 1.0), 1.1));
            if (hazy) n.Add(new SGroup(180, 290 - PourScene.KryglHeight * 1.1, 0, null, new List<SceneNode>
            {
                new SPath(Rect(-45, 30, 90, 110), Fill: "#E8D8B0", FillOpacity: 0.35)
            }, 1.1));
            Hop(n, 70, 270, 1.6);
            Hop(n, 92, 280, 1.3);
        }
    }
}
