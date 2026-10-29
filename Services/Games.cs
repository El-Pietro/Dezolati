using System.Text.Json;

namespace Dezolati.Services
{
    // =====================================================================
    // Hospodské hry – jen v telefonu (žádná databáze). Rozehraná hra se ukládá do Preferences,
    // takže přežije zhasnutí telefonu i zabití appky.
    // =====================================================================

    /// <summary>Hráči u stolu (pamatuje si poslední sestavu).</summary>
    public static class GamePlayers
    {
        private const string Key = "hry_hraci";

        public static List<string> Load()
        {
            try { return JsonSerializer.Deserialize<List<string>>(Preferences.Get(Key, "[]")) ?? new(); }
            catch { return new(); }
        }

        public static void Save(List<string> players) =>
            Preferences.Set(Key, JsonSerializer.Serialize(players));
    }

    /// <summary>Uložení rozehrané hry + historie pro „Zpět“.</summary>
    public abstract class GameBase<T> where T : GameBase<T>
    {
        public List<string> Players { get; set; } = new();
        public int Current { get; set; }
        public bool Finished { get; set; }
        /// <summary>Předchozí stavy (JSON) pro „Zpět“, max. 60.</summary>
        public List<string> History { get; set; } = new();

        protected abstract string StoreKey { get; }

        protected void Snapshot()
        {
            var copy = History;
            History = new();
            var json = JsonSerializer.Serialize((T)this);
            History = copy;
            History.Add(json);
            if (History.Count > 60) History.RemoveAt(0);
        }

        public bool CanUndo => History.Count > 0;

        /// <summary>Vrátí předchozí stav (nový objekt).</summary>
        public T? Undo()
        {
            if (History.Count == 0) return null;
            var json = History[^1];
            var prev = JsonSerializer.Deserialize<T>(json);
            if (prev == null) return null;
            prev.History = History.Take(History.Count - 1).ToList();
            prev.Save();
            return prev;
        }

        public void Save() => Preferences.Set(StoreKey, JsonSerializer.Serialize((T)this));

        public static T? LoadSaved(string key)
        {
            try
            {
                var json = Preferences.Get(key, null as string);
                return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json);
            }
            catch { return null; }
        }

        public void Clear() => Preferences.Remove(StoreKey);
    }

    // =====================================================================
    // Šipky 501 / 301
    // =====================================================================

    public sealed class DartsVisit
    {
        public int Player { get; set; }
        public int Points { get; set; }
        public bool Bust { get; set; }
    }

    public enum DartsResult { Ok, Bust, Win, Invalid, NeedsDouble }

    public sealed class DartsGame : GameBase<DartsGame>
    {
        public const string Key = "hra_sipky";
        protected override string StoreKey => Key;

        public int Start { get; set; } = 501;
        public bool DoubleOut { get; set; } = true;
        public List<DartsVisit> Visits { get; set; } = new();
        public int? Winner { get; set; }

        // body, které třemi šipkami hodit nejde
        private static readonly HashSet<int> Impossible = new() { 163, 166, 169, 172, 173, 175, 176, 178, 179 };

        public int Remaining(int player) =>
            Start - Visits.Where(v => v.Player == player && !v.Bust).Sum(v => v.Points);

        /// <summary>Průměr na 3 šipky (přehozené kolo = 0).</summary>
        public double Average(int player)
        {
            var mine = Visits.Where(v => v.Player == player).ToList();
            return mine.Count == 0 ? 0 : mine.Sum(v => v.Bust ? 0 : v.Points) / (double)mine.Count;
        }

        public int VisitsOf(int player) => Visits.Count(v => v.Player == player);

        /// <summary>
        /// Zapsat kolo (součet 3 šipek). Při double out a dohození na 0 se nejdřív vrátí NeedsDouble
        /// a appka se zeptá „zavřel na double?“ → zavolá znovu s closedOnDouble.
        /// </summary>
        public DartsResult Submit(int points, bool? closedOnDouble = null)
        {
            if (Finished) return DartsResult.Invalid;
            if (points < 0 || points > 180 || Impossible.Contains(points)) return DartsResult.Invalid;
            var left = Remaining(Current) - points;
            if (left == 0 && DoubleOut && closedOnDouble == null) return DartsResult.NeedsDouble;

            Snapshot();
            var bust = left < 0 || (DoubleOut && left == 1) || (left == 0 && DoubleOut && closedOnDouble == false);
            Visits.Add(new DartsVisit { Player = Current, Points = points, Bust = bust });
            if (!bust && left == 0)
            {
                Winner = Current;
                Finished = true;
                Save();
                return DartsResult.Win;
            }
            Current = (Current + 1) % Players.Count;
            Save();
            return bust ? DartsResult.Bust : DartsResult.Ok;
        }

        // ---------------- návrh zavření (double out) ----------------

        private static readonly List<(string Name, int Value, bool Double, int Cost)> Darts = BuildDarts();

        private static List<(string, int, bool, int)> BuildDarts()
        {
            var list = new List<(string, int, bool, int)>();
            for (var n = 20; n >= 1; n--) list.Add(($"{n}", n, false, 0));
            list.Add(("25", 25, false, 3));
            for (var n = 20; n >= 1; n--) list.Add(($"T{n}", 3 * n, false, n >= 19 ? 1 : 3));
            for (var n = 20; n >= 1; n--) list.Add(($"D{n}", 2 * n, true, 3));
            list.Add(("Bull", 50, true, 4));
            return list;
        }

        // oblíbená zavírací dvojitá pole (čím dřív v seznamu, tím líp)
        private static readonly string[] FavoriteDoubles =
            { "D20", "D16", "D18", "D12", "D10", "D8", "D19", "D17", "D14", "D15", "D13", "D11", "D9", "D6", "D4", "D7", "D5", "D3", "D2", "D1", "Bull" };

        private static int DoubleRank(string name)
        {
            var i = Array.IndexOf(FavoriteDoubles, name);
            return i < 0 ? 30 : i;
        }

        /// <summary>Návrh zavření na max. 3 šipky (double out), null = nejde.</summary>
        public static string? Checkout(int remaining, int dartsLeft = 3)
        {
            if (remaining < 2 || remaining > 170) return null;
            string? best = null;
            var bestCost = int.MaxValue;
            var doubles = Darts.Where(d => d.Double).ToList();
            foreach (var fin in doubles)
            {
                var rest = remaining - fin.Value;
                if (rest < 0) continue;
                var fr = DoubleRank(fin.Name);
                if (rest == 0)
                {
                    Consider(fin.Name, 1000 + fr);
                    continue;
                }
                if (dartsLeft >= 2)
                    foreach (var a in Darts.Where(d => d.Value == rest))
                        Consider($"{a.Name} {fin.Name}", 2000 + fr + a.Cost);
                if (dartsLeft >= 3)
                    foreach (var a in Darts)
                    {
                        var r2 = rest - a.Value;
                        if (r2 <= 0) continue;
                        foreach (var b in Darts.Where(d => d.Value == r2))
                            Consider($"{a.Name} {b.Name} {fin.Name}", 3000 + fr + a.Cost + b.Cost + (a.Value < b.Value ? 1 : 0));
                    }
            }
            return best;

            void Consider(string route, int cost)
            {
                if (cost < bestCost) { bestCost = cost; best = route; }
            }
        }
    }

    // =====================================================================
    // Kostky – „deset tisíc“ podle pravidel naší hospody
    // =====================================================================

    public enum DiceResult { Ok, Invalid, TooMany, NotYet, MustRoll, TurnLost, Banked, GameOver }

    public sealed class DiceGame : GameBase<DiceGame>
    {
        public const string Key = "hra_kostky";
        protected override string StoreKey => Key;

        public const int MinTurn = 300;      // do 3 hodů aspoň 300, jinak tah propadá
        public const int RollsForMin = 3;

        public int Target { get; set; } = 10000;
        public bool Pairs { get; set; } = true;    // tři různé dvojice = 1000
        public List<int> Totals { get; set; } = new();
        public int TurnPoints { get; set; }
        public int Rolls { get; set; }             // hody v tomhle tahu
        public int DiceLeft { get; set; } = 6;
        public bool HotDice { get; set; }          // odložil všech 6 → musí házet znovu
        public int? FinalBy { get; set; }          // kdo první přehodil cíl (dohrává se kolo)
        public string? LastEvent { get; set; }     // „Petr: 1 250 do sloupce“ apod.

        public static int[] Counts(IEnumerable<int> dice)
        {
            var c = new int[7];
            foreach (var d in dice) if (d is >= 1 and <= 6) c[d]++;
            return c;
        }

        /// <summary>Body za odložené kostky; null = jsou mezi nimi nebodované.</summary>
        public static int? Score(IReadOnlyList<int> dice, bool pairs)
        {
            if (dice.Count == 0) return null;
            var c = Counts(dice);
            var n = dice.Count;
            if (n == 6 && Enumerable.Range(1, 6).All(f => c[f] == 1)) return 1500;   // postupka

            int? standard = 0;
            for (var f = 1; f <= 6 && standard != null; f++)
            {
                var k = c[f];
                if (k >= 3) standard += (f == 1 ? 1000 : f * 100) << (k - 3);     // čtyři ×2, pět ×4, šest ×8
                else if (f == 1) standard += 100 * k;
                else if (f == 5) standard += 50 * k;
                else if (k > 0) standard = null;
            }
            if (pairs && n == 6 && Enumerable.Range(1, 6).Count(f => c[f] == 2) == 3)
                return Math.Max(1000, standard ?? 0);
            return standard;
        }

        /// <summary>Dá se z hodu vůbec něco odložit? (jen pro hod appkou)</summary>
        public static bool AnyScore(IReadOnlyList<int> roll, bool pairs)
        {
            var c = Counts(roll);
            if (c[1] > 0 || c[5] > 0) return true;
            if (Enumerable.Range(1, 6).Any(f => c[f] >= 3)) return true;
            return Score(roll, pairs) != null;
        }

        /// <summary>Ještě potřebuje 300 (a kolik hodů na to zbývá).</summary>
        public bool NeedsMin => TurnPoints < MinTurn;
        public int RollsLeftForMin => Math.Max(0, RollsForMin - Rolls);

        /// <summary>Odložit kostky z hodu a házet dál (bank = rovnou zapsat).</summary>
        public DiceResult Keep(IReadOnlyList<int> dice, bool bank)
        {
            if (Finished) return DiceResult.GameOver;
            var s = Score(dice, Pairs);
            if (s == null) return DiceResult.Invalid;
            if (dice.Count > DiceLeft) return DiceResult.TooMany;
            var allSix = dice.Count == DiceLeft;
            if (bank && allSix) return DiceResult.MustRoll;                 // všech 6 bodovalo → musí házet znovu
            if (bank && TurnPoints + s.Value < MinTurn) return DiceResult.NotYet;

            Snapshot();
            TurnPoints += s.Value;
            Rolls++;
            DiceLeft -= dice.Count;
            HotDice = DiceLeft == 0;
            if (HotDice) DiceLeft = 6;

            if (bank) return BankInternal();

            // 3. hod a pořád pod 300 → propadá (pokud teď nehází všemi 6 znovu)
            if (Rolls >= RollsForMin && TurnPoints < MinTurn && !HotDice)
                return Lose($"{Players[Current]} nenahrál(a) {MinTurn} do {RollsForMin} hodů");
            Save();
            return DiceResult.Ok;
        }

        /// <summary>Zapsat body z tahu bez dalšího odkládání.</summary>
        public DiceResult Bank()
        {
            if (Finished) return DiceResult.GameOver;
            if (HotDice) return DiceResult.MustRoll;
            if (TurnPoints < MinTurn) return DiceResult.NotYet;
            Snapshot();
            return BankInternal();
        }

        /// <summary>Hod bez jediného bodu → tah za 0.</summary>
        public DiceResult Nothing()
        {
            if (Finished) return DiceResult.GameOver;
            Snapshot();
            return Lose($"{Players[Current]}: nic nepadlo, {TurnPoints} propadá");
        }

        private DiceResult BankInternal()
        {
            Totals[Current] += TurnPoints;
            LastEvent = $"{Players[Current]}: +{TurnPoints:N0}".Replace(',', ' ');
            if (FinalBy == null && Totals[Current] >= Target) FinalBy = Current;
            return NextPlayer(DiceResult.Banked);
        }

        private DiceResult Lose(string why)
        {
            LastEvent = why;
            return NextPlayer(DiceResult.TurnLost);
        }

        private DiceResult NextPlayer(DiceResult result)
        {
            TurnPoints = 0;
            Rolls = 0;
            DiceLeft = 6;
            HotDice = false;
            Current = (Current + 1) % Players.Count;
            // kdo přehodil cíl, kolo se dohraje (všichni stejně tahů) – ostatní ho ještě můžou přehodit
            if (FinalBy != null && Current == 0)
            {
                Finished = true;
                result = DiceResult.GameOver;
            }
            Save();
            return result;
        }

        /// <summary>Vítěz(ové) – nejvíc bodů.</summary>
        public List<int> Winners()
        {
            if (Totals.Count == 0) return new();
            var max = Totals.Max();
            return Enumerable.Range(0, Totals.Count).Where(i => Totals[i] == max).ToList();
        }
    }

    // =====================================================================
    // Kostky zjednodušené – jen kalkulačka: hody a pravidla si hlídá stůl
    // =====================================================================

    public sealed class SimpleDiceGame : GameBase<SimpleDiceGame>
    {
        public const string Key = "hra_kostky2";
        protected override string StoreKey => Key;

        public int Target { get; set; } = 10000;
        public List<int> Totals { get; set; } = new();
        public int TurnPoints { get; set; }        // body z předchozích hodů tohohle tahu
        public int? FinalBy { get; set; }
        public string? LastEvent { get; set; }

        /// <summary>„Další hod“: body z hodu přičíst k tahu, hází dál stejný hráč.</summary>
        public void NextRoll(int points)
        {
            if (Finished || points <= 0) return;
            Snapshot();
            TurnPoints += points;
            Save();
        }

        /// <summary>„Zapsat“: tah + poslední hod do sloupce, hraje další.</summary>
        public void Bank(int points)
        {
            if (Finished) return;
            Snapshot();
            var sum = TurnPoints + Math.Max(0, points);
            Totals[Current] += sum;
            LastEvent = $"{Players[Current]}: +{sum.ToString("#,0", System.Globalization.CultureInfo.GetCultureInfo("cs-CZ"))}";
            if (FinalBy == null && Totals[Current] >= Target) FinalBy = Current;
            Next();
        }

        /// <summary>„Vynulovat“: tah propadá (nic nepadlo / málo), hraje další.</summary>
        public void Reset()
        {
            if (Finished) return;
            Snapshot();
            LastEvent = $"{Players[Current]}: vynulováno";
            Next();
        }

        private void Next()
        {
            TurnPoints = 0;
            Current = (Current + 1) % Players.Count;
            if (FinalBy != null && Current == 0) Finished = true;   // kdo přehodil cíl, kolo se dohraje
            Save();
        }

        public List<int> Winners()
        {
            if (Totals.Count == 0) return new();
            var max = Totals.Max();
            return Enumerable.Range(0, Totals.Count).Where(i => Totals[i] == max).ToList();
        }
    }
}
