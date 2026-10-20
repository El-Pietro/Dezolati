using System.Text.Json.Nodes;
using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>
    /// Jednorázová migrace dat přihlášeného uživatele na schéma verze 2.
    /// Spouští ji UserSession, když má profil SchemaVersion menší než Version.
    /// Každý uživatel migruje jen svoje data (to dovolí security rules).
    ///
    ///  DailyDrinks: staré dokumenty s náhodným ID se sloučí do {uid}_{yyyyMMdd},
    ///               doplní se Day (yyyy-MM-dd) a Total, duplicity se smažou.
    ///  Friends:     doplní se Members + Nicks, zamítnuté žádosti se smažou.
    /// </summary>
    public static class Migration
    {
        public const int Version = 2;

        public static async Task RunAsync(string uid, UserModel me)
        {
            var writes = new List<JsonObject>();

            // ---------- DailyDrinks ----------
            var daily = await FirestoreRest.QueryAsync("DailyDrinks", 0, ("UserId", uid));

            foreach (var group in daily
                         .Select(d => (Doc: d, Model: FirestoreService.ToDaily(d)))
                         .Where(x => x.Model.Day != DateTime.MinValue)
                         .GroupBy(x => x.Model.Day))
            {
                var day = group.Key;
                var targetId = FirestoreService.DailyId(uid, day);
                var items = group.ToList();

                bool alreadyClean = items.Count == 1
                                    && items[0].Doc.Id == targetId
                                    && items[0].Doc.Has("Day")
                                    && items[0].Doc.Has("Total");
                if (alreadyClean) continue;

                int pivo = items.Sum(x => x.Model.Pivo);
                int vino = items.Sum(x => x.Model.Vino);
                int panaky = items.Sum(x => x.Model.Panaky);

                // cílový dokument se zapíše celý (absolutní hodnoty, ne přičtení)
                writes.Add(FirestoreRest.SetWrite($"DailyDrinks/{targetId}", new()
                {
                    ["UserId"] = uid,
                    ["Date"] = PubDay.Key(day),
                    ["Day"] = FirestoreService.IsoDay(day),
                    ["Pivo"] = pivo,
                    ["Vino"] = vino,
                    ["Panaky"] = panaky,
                    ["Total"] = pivo + vino + panaky,
                    ["UpdatedAt"] = items.Select(x => x.Model.UpdatedAt).Max() ?? DateTime.UtcNow
                }));

                foreach (var x in items.Where(x => x.Doc.Id != targetId))
                    writes.Add(FirestoreRest.DeleteWrite($"DailyDrinks/{x.Doc.Id}"));
            }

            // ---------- Friends ----------
            var sent = await FirestoreRest.QueryAsync("Friends", 0, ("UserId", uid));
            var received = await FirestoreRest.QueryAsync("Friends", 0, ("FriendId", uid));

            foreach (var d in sent.Concat(received).GroupBy(d => d.Id).Select(g => g.First()))
            {
                var status = d.Str("Status");
                if (status is not ("Pending" or "Accepted"))
                {
                    writes.Add(FirestoreRest.DeleteWrite($"Friends/{d.Id}"));
                    continue;
                }

                var from = d.Str("UserId") ?? "";
                var to = d.Str("FriendId") ?? "";
                if (from.Length == 0 || to.Length == 0) continue;

                var nicks = d.StrMap("Nicks");
                if (d.StrArray("Members").Count == 2 && nicks.ContainsKey(from) && nicks.ContainsKey(to))
                    continue;

                var other = from == uid ? to : from;
                var otherNick = (await FirestoreRest.GetAsync($"Users/{other}"))?.Str("Nickname");

                writes.Add(FirestoreRest.SetWrite($"Friends/{d.Id}", new()
                {
                    ["Members"] = new List<string> { from, to },
                    ["Nicks"] = new Dictionary<string, object?> { [uid] = me.Nickname, [other] = otherNick }
                }, mask: new[] { "Members", "Nicks" }));
            }

            // ---------- hotovo ----------
            writes.Add(FirestoreRest.SetWrite($"Users/{uid}",
                new() { ["SchemaVersion"] = Version },
                mask: new[] { "SchemaVersion" }));

            await FirestoreRest.CommitAsync(writes);
        }
    }
}
