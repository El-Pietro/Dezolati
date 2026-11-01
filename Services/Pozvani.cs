using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>Člen pozvánky: kdo, jak odpověděl (yes / no / wait) a kdo ho pozval.</summary>
    public sealed record InviteMember(string Uid, string Nick, string Status, string? InvitedBy)
    {
        public string Mark => Status switch { "yes" => "✔", "no" => "✖", _ => "?" };
    }

    /// <summary>Pozvánka do hospody (Invites/{id}, Version 2).</summary>
    public sealed class InviteInfo
    {
        public string Id { get; init; } = "";
        public string From { get; init; } = "";
        public string FromNick { get; init; } = "";
        public string PubId { get; init; } = "";
        public string PubName { get; init; } = "";
        public double Lat { get; init; }
        public double Lon { get; init; }
        public DateTime EndsAt { get; init; }
        public List<InviteMember> Members { get; init; } = new();

        public bool Active => EndsAt > DateTime.UtcNow;
        public string MyStatus => Members.FirstOrDefault(m => m.Uid == AuthSession.Uid)?.Status ?? "wait";
        public bool Waiting => Active && MyStatus == "wait";
        public int Going => Members.Count(m => m.Status == "yes");
        /// <summary>Hospodský den pozvánky (končí v 6:00 → den předtím).</summary>
        public DateTime PubDay => EndsAt.ToLocalTime().Date.AddDays(-1);
    }

    /// <summary>Cizí pozvánka do stejné hospody, ve které je někdo z mých kámošů.</summary>
    public sealed record JoinableInvite(string Id, string FromNick, List<string> Friends, int Going);

    /// <summary>
    /// Pozvánky na vybrané kámoše (od 3.11): pozvaní odpovídají Jdu / Nejdu, kdo jde, může zvát další,
    /// k pozvánce kámoše do stejné hospody se dá přidat. Končí ráno v 6:00. Zakládá a mění server
    /// (Cloud Function inviteAction – hlídá, že zveš jen kámoše), čte appka přímo (jen pozvánky, kde jsem).
    /// Sdílené s webem.
    /// </summary>
    public static class Pozvani
    {
        private const string Url = "https://europe-west10-dezolati.cloudfunctions.net/inviteAction";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
        public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(10);
        private const string KeyLast = "pozvanka_last";

        /// <summary>Hospoda, do které se právě zve (z mapy / profilu hospody → stránka Pozvánky).</summary>
        public static PubInfo? DraftPub { get; set; }

        /// <summary>Kolik pozvánek čeká na moji odpověď (naposledy načteno).</summary>
        public static int WaitingCount { get; private set; }

        /// <summary>Moje pozvánky: běžící + skončené za posledních 18 h (kvůli souhrnu). 1 čtení za pozvánku.</summary>
        public static async Task<List<InviteInfo>> MineAsync()
        {
            var me = AuthSession.Uid ?? throw new InvalidOperationException("Nejsi přihlášený.");
            var docs = await FirestoreRest.QueryAsync("Invites", new[]
            {
                Filter.ArrayContains("Members", me),
                Filter.Gte("EndsAt", DateTime.UtcNow.AddHours(-18))
            });
            var list = docs.Where(d => d.Int("Version") == 2).Select(Parse)
                .OrderByDescending(i => i.Active).ThenByDescending(i => i.EndsAt).ToList();
            WaitingCount = list.Count(i => i.Waiting);
            return list;
        }

        public static TimeSpan? WaitLeft()
        {
            var last = Preferences.Get(KeyLast, 0L);
            if (last == 0) return null;
            var left = MinGap - (DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc));
            return left > TimeSpan.Zero ? left : null;
        }

        public static async Task<string> CreateAsync(PubInfo pub, IEnumerable<string> friendUids)
        {
            if (WaitLeft() is TimeSpan left)
                throw new InvalidOperationException($"Pozvánku jsi poslal před chvílí, další až za {Math.Ceiling(left.TotalMinutes)} min.");
            var res = await CallAsync(new JsonObject
            {
                ["action"] = "create",
                ["pubId"] = pub.Id,
                ["pubName"] = PubService.NameOf(pub),
                ["lat"] = pub.Lat,
                ["lon"] = pub.Lon,
                ["members"] = new JsonArray(friendUids.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray())
            });
            Preferences.Set(KeyLast, DateTime.UtcNow.Ticks);
            return res?["id"]?.GetValue<string>() ?? "";
        }

        public static Task RespondAsync(string id, bool going) =>
            CallAsync(new JsonObject { ["action"] = "respond", ["id"] = id, ["answer"] = going ? "yes" : "no" });

        public static Task AddAsync(string id, IEnumerable<string> friendUids) =>
            CallAsync(new JsonObject
            {
                ["action"] = "add", ["id"] = id,
                ["members"] = new JsonArray(friendUids.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray())
            });

        public static Task JoinAsync(string id) => CallAsync(new JsonObject { ["action"] = "join", ["id"] = id });

        /// <summary>Pozvánky kámošů do téhle hospody, ve kterých ještě nejsem.</summary>
        public static async Task<List<JoinableInvite>> AtPubAsync(string pubId)
        {
            var res = await CallAsync(new JsonObject { ["action"] = "atPub", ["pubId"] = pubId });
            return (res?["invites"] as JsonArray)?.Select(n => new JoinableInvite(
                    n?["id"]?.GetValue<string>() ?? "",
                    n?["fromNick"]?.GetValue<string>() ?? "?",
                    (n?["friends"] as JsonArray)?.Select(f => f?.GetValue<string>() ?? "?").ToList() ?? new(),
                    n?["going"]?.GetValue<int>() ?? 0))
                .Where(j => j.Id.Length > 0).ToList() ?? new();
        }

        /// <summary>
        /// Souhrn skončené pozvánky: kolik kdo z těch, co šli, za ten den vypil (tajný režim platí).
        /// Čtení: profil + denní součet za každého, kdo šel.
        /// </summary>
        public static async Task<List<(string Nick, int Total, string Text)>> SummaryAsync(InviteInfo invite)
        {
            var result = new List<(string, int, string)>();
            foreach (var m in invite.Members.Where(m => m.Status == "yes"))
            {
                try
                {
                    var user = m.Uid == AuthSession.Uid ? null : await FirestoreRest.GetAsync($"Users/{m.Uid}");
                    var doc = await FirestoreRest.GetAsync($"DailyDrinks/{FirestoreService.DailyId(m.Uid, invite.PubDay)}");
                    var day = doc == null ? new DailyDrinksModel() : FirestoreService.ToDaily(doc);
                    if (Mask.AppliesTo(user)) day = Mask.Day(day);
                    var nick = m.Uid == AuthSession.Uid ? "Ty" : FriendAliases.Display(m.Uid, m.Nick) ?? m.Nick;
                    result.Add((nick, day.Total, day.Total == 0 ? "nic (nebo nezapsal)" : Cz.Souhrn(day.Pivo, day.Vino, day.Panaky)));
                }
                catch { }
            }
            return result.OrderByDescending(r => r.Item2).ToList();
        }

        // ---------------- interní ----------------

        private static InviteInfo Parse(FsDoc d)
        {
            var status = d.StrMap("Status");
            var nicks = d.StrMap("Nicks");
            var by = d.StrMap("InvitedBy");
            double Dbl(string f) => d.Fields[f]?["doubleValue"]?.GetValue<double>() ?? FsDoc.ValueToLong(d.Fields[f]);
            return new InviteInfo
            {
                Id = d.Id,
                From = d.Str("From") ?? "",
                FromNick = d.Str("FromNick") ?? "?",
                PubId = d.Str("PubId") ?? "",
                PubName = d.Str("PubName") ?? "Hospoda",
                Lat = Dbl("Lat"),
                Lon = Dbl("Lon"),
                EndsAt = d.Time("EndsAt") ?? DateTime.UtcNow,
                Members = d.StrArray("Members").Select(u => new InviteMember(u,
                        FriendAliases.Display(u, nicks.GetValueOrDefault(u)) ?? "?",
                        status.GetValueOrDefault(u) ?? "wait",
                        by.GetValueOrDefault(u)))
                    .ToList()
            };
        }

        private static async Task<JsonNode?> CallAsync(JsonObject data)
        {
            var token = await AuthSession.GetTokenAsync();
            using var req = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Content = new StringContent(new JsonObject { ["data"] = data }.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            JsonNode? root;
            try { root = JsonNode.Parse(json); }
            catch (JsonException) { throw new InvalidOperationException($"Server neodpověděl ({(int)resp.StatusCode})."); }
            if (root?["error"] is JsonNode err)
                throw new InvalidOperationException(err["message"]?.GetValue<string>() ?? "Server vrátil chybu.");
            return root?["result"];
        }
    }
}
