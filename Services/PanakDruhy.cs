using System.Text.Json;

namespace Dezolati.Services
{
    /// <summary>
    /// Druhy panáků (od 3.12). Pořád je to panák (4 cl) – druh jen zpřesní promile a jde vidět
    /// ve schované části statistik. Starší panáky a „nevím“ = Ostatní.
    /// </summary>
    public static class PanakDruhy
    {
        /// <param name="Key">uloží se do zápisu (PanakDruh)</param>
        /// <param name="Field">pole v denním součtu</param>
        /// <param name="Akuzativ">„dal si slivovici“ (1 kus)</param>
        public sealed record Druh(string Key, string Name, double Abv, string Field, string Akuzativ);

        public const string Ostatni = "Ostatni";

        public static readonly Druh[] All =
        {
            new("Slivovice", "Slivovice", 0.55, "PanakSlivovice", "slivovici"),
            new("Liker", "Likér", 0.20, "PanakLiker", "likér"),
            new("Rum", "Rum", 0.40, "PanakRum", "rum"),
            new("Vodka", "Vodka", 0.40, "PanakVodka", "vodku"),
            new("Whisky", "Whisky", 0.40, "PanakWhisky", "whisky"),
            new(Ostatni, "Ostatní", 0.40, "PanakOstatni", "panáka"),
        };

        public static Druh Find(string? key) => All.FirstOrDefault(d => d.Key == key) ?? All[^1];

        // ---------------- co se dnes pilo (kvůli škrtání) ----------------
        // Škrtnutí panáku z lístku ubere druh posledního zapsaného panáku toho dne
        // (jinak by slivovice zůstala ve statistice, i když ji škrtneš).

        private sealed record DayLog(DateTime Day, List<string> Keys);

        private static string LogKey(string uid) => $"panak_druhy_{uid}";

        private static DayLog? Load(string uid)
        {
            try
            {
                var json = Preferences.Get(LogKey(uid), null as string);
                return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<DayLog>(json);
            }
            catch { return null; }
        }

        private static void Save(string uid, DayLog log)
        {
            try { Preferences.Set(LogKey(uid), JsonSerializer.Serialize(log)); } catch { }
        }

        /// <summary>Zapsané panáky (n kusů jednoho druhu).</summary>
        public static void Remember(string uid, DateTime day, string? key, int n)
        {
            if (n <= 0) return;
            var log = Load(uid);
            if (log == null || log.Day.Date != day.Date) log = new DayLog(day.Date, new());
            for (var i = 0; i < n && log.Keys.Count < 200; i++) log.Keys.Add(Find(key).Key);
            Save(uid, log);
        }

        /// <summary>Škrtnutí: druh posledního panáku toho dne (a vyndat ho), jinak Ostatní.</summary>
        public static string TakeForStrike(string uid, DateTime day)
        {
            var log = Load(uid);
            if (log == null || log.Day.Date != day.Date || log.Keys.Count == 0) return Ostatni;
            var key = log.Keys[^1];
            log.Keys.RemoveAt(log.Keys.Count - 1);
            Save(uid, log);
            return key;
        }
    }
}
