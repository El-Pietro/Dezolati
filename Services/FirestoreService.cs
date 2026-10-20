using System.Globalization;
using System.Text.Json.Nodes;
using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>
    /// Doménové operace nad Firestore. Schéma (verze 2):
    ///   Users/{uid}                   – profil + LastDrinkAt/LastDrinkDay + SchemaVersion
    ///   Drinks/{guid}                 – každý jednotlivý zápis (log, appka ho nečte)
    ///   DailyDrinks/{uid}_{yyyyMMdd}  – denní součet: Pivo, Vino, Panaky, Total, Day (yyyy-MM-dd), Date (dd-MM-yyyy)
    ///   Friends/{uidA}_{uidB}         – UserId (žadatel), FriendId (adresát), Members [obě uid], Nicks {uid: přezdívka}, Status
    ///
    /// Potřebné composite indexy (Firestore → Indexes):
    ///   DailyDrinks: UserId ↑, Day ↓
    ///   DailyDrinks: UserId ↑, Total ↓
    /// </summary>
    public class FirestoreService
    {
        public const string DayFormat = "yyyy-MM-dd";

        public static string DailyId(string userId, DateTime day) => $"{userId}_{day:yyyyMMdd}";
        public static string IsoDay(DateTime day) => day.ToString(DayFormat, CultureInfo.InvariantCulture);

        // ================= Uživatelé =================

        public async Task<UserModel?> GetUserData(string userId)
        {
            var doc = await FirestoreRest.GetAsync($"Users/{userId}");
            return doc == null ? null : ToUser(doc);
        }

        /// <summary>Nastavení oznámení v profilu (čte je Cloud Function).</summary>
        public Task UpdateNotificationPrefs(string userId, bool notifyFriends, bool sharePub)
        {
            var fields = new Dictionary<string, object?>
            {
                ["NotifyFriends"] = notifyFriends,
                ["SharePub"] = sharePub
            };
            if (!sharePub)
            {
                // přestal hlásit → kámoši už nemají vidět, kde sedí
                fields["CurrentPubId"] = null;
                fields["CurrentPubName"] = null;
                fields["CurrentPubAt"] = null;
            }
            return FirestoreRest.PatchAsync($"Users/{userId}", fields);
        }

        public Task UpdateUserData(string userId, UserModel user) =>
            FirestoreRest.PatchAsync($"Users/{userId}", new()
            {
                ["UserId"] = userId,
                ["Name"] = user.Name,
                ["Nickname"] = user.Nickname,
                ["Gender"] = user.Gender,
                ["DrinkerType"] = user.DrinkerType,
                ["Email"] = user.Email
            });

        /// <summary>Založí profil v Users, pokud ještě neexistuje (bez něj tě kámoši nenajdou).</summary>
        public async Task EnsureUserDoc(string userId, string? email, string? nickname)
        {
            if (await FirestoreRest.GetAsync($"Users/{userId}") != null)
                return;

            await FirestoreRest.PatchAsync($"Users/{userId}", new()
            {
                ["UserId"] = userId,
                ["Email"] = email,
                ["Nickname"] = nickname,
                ["Name"] = nickname,
                ["SchemaVersion"] = Migration.Version
            });
        }

        public async Task<UserModel?> FindUserByNickname(string nickname)
        {
            var docs = await FirestoreRest.QueryAsync("Users", 1, ("Nickname", nickname.Trim()));
            return docs.Count > 0 ? ToUser(docs[0]) : null;
        }

        /// <summary>Po změně přezdívky ji přepíše ve všech přátelstvích (Nicks.{uid}).</summary>
        public async Task PropagateNickname(string userId, string nickname)
        {
            var docs = await FirestoreRest.QueryAsync("Friends", new[] { Filter.ArrayContains("Members", userId) });
            var writes = docs.Select(d => FirestoreRest.SetWrite(
                $"Friends/{d.Id}",
                new() { ["Nicks"] = new Dictionary<string, object?> { [userId] = nickname } },
                mask: new[] { $"Nicks.{userId}" }));
            await FirestoreRest.CommitAsync(writes);
        }

        // ================= Pití =================

        /// <summary>Denní součet pro daný hospodský den (null = čistý lístek). 1 čtení.</summary>
        public async Task<DailyDrinksModel?> GetDailyDrinks(string userId, DateTime day)
        {
            var doc = await FirestoreRest.GetAsync($"DailyDrinks/{DailyId(userId, day)}");
            return doc == null ? null : ToDaily(doc);
        }

        /// <summary>Hospodský den jiného uživatele (podle jeho posledního drinku). 1 čtení.</summary>
        public async Task<DateTime> GetCurrentPubDayOf(string userId) => (await GetDayAndPubOf(userId)).Day;

        /// <summary>
        /// Hospodský den + hospoda, kde dotyčný naposledy zapisoval (jen když ji hlásí). 1 čtení (u sebe 0).
        /// </summary>
        private async Task<(DateTime Day, string? PubName, DateTime? PubAt)> GetDayAndPubOf(string userId)
        {
            if (userId == AuthSession.Uid)
            {
                await UserSession.EnsureReadyAsync();
                var day = UserSession.CurrentPubDay;
                return (day, PushService.LastPubName(userId, day), UserSession.LastDrinkAt);
            }

            var user = await FirestoreRest.GetAsync($"Users/{userId}");
            var friendDay = PubDay.Resolve(DateTime.Now, user?.Time("LastDrinkAt")?.ToLocalTime(), PubDay.Parse(user?.Str("LastDrinkDay")));
            return (friendDay, user?.Str("CurrentPubName"), user?.Time("CurrentPubAt"));
        }

        /// <summary>
        /// Zápis pití: 0 čtení, 3 zápisy v jedné atomické dávce.
        /// Jde přes frontu v telefonu – bez signálu se odešle později.
        /// Vrací true = uloženo na serveru, false = čeká ve frontě.
        /// </summary>
        public async Task<bool> RecordDrink(string userId, string? name, int pivo, int vino, int panaky,
            bool fromReminder = false)
        {
            await UserSession.EnsureReadyAsync();

            var now = DateTime.UtcNow;
            var day = UserSession.CurrentPubDay;   // den se určí teď, ne až při odeslání

            // hospoda podle polohy (jen při ručním zápisu a se zapnutým poznáváním hospody)
            var (pubId, pubName) = fromReminder ? (null, null) : await PushService.PubHereAsync();

            DrinkQueue.Enqueue(userId, new PendingDrink(Guid.NewGuid().ToString("N"), now, day,
                pivo, vino, panaky, Correction: false, Name: name ?? UserSession.Profile?.Nickname,
                PubId: pubId, PubName: pubName, SharePub: PushService.SharePub));
            if (!fromReminder && PushService.DetectPub)
                PushService.RememberPub(userId, day, pubId, pubName);   // pro lístek: „📍 U Peťky“
            UserSession.OnDrinkRecorded(now, day);

            // další připomínka za X minut (lokální notifikace)
            try { await Reminders.OnDrinkRecordedAsync(day, pivo, vino, panaky, fromReminder); } catch { }

            return await DrinkQueue.FlushAsync(userId);
        }

        /// <summary>
        /// Škrtnutí čárky z lístku daného dne (záporné hodnoty). Neposouvá čas posledního drinku.
        /// Vrací true = uloženo, false = čeká ve frontě.
        /// </summary>
        public async Task<bool> RecordCorrection(string userId, DateTime day, int pivo, int vino, int panaky)
        {
            DrinkQueue.Enqueue(userId, new PendingDrink(Guid.NewGuid().ToString("N"), DateTime.UtcNow, day.Date,
                -Math.Abs(pivo), -Math.Abs(vino), -Math.Abs(panaky), Correction: true, Name: UserSession.Profile?.Nickname));
            Reminders.OnCorrection(day, pivo, vino, panaky);

            return await DrinkQueue.FlushAsync(userId);
        }

        /// <summary>
        /// Statistiky za ~33 čtení bez ohledu na délku historie:
        /// agregace (1) + posledních 30 dní (≤30) + rekord (1) + hospodský den kámoše (1).
        /// </summary>
        public async Task<UserStats> GetStats(string userId)
        {
            var (today, pubName, pubAt) = await GetDayAndPubOf(userId);
            var since = IsoDay(today.AddDays(-29));
            var byUser = new[] { Filter.Eq("UserId", userId) };

            var aggTask = FirestoreRest.AggregateAsync("DailyDrinks", byUser,
                new Dictionary<string, string> { ["pivo"] = "Pivo", ["vino"] = "Vino", ["panaky"] = "Panaky" },
                countAlias: "days");

            var lastTask = FirestoreRest.QueryAsync("DailyDrinks",
                new[] { Filter.Eq("UserId", userId), Filter.Gte("Day", since) },
                orderBy: "Day", descending: true);

            var recordTask = FirestoreRest.QueryAsync("DailyDrinks", byUser,
                orderBy: "Total", descending: true, limit: 1);

            await Task.WhenAll(aggTask, lastTask, recordTask);

            var agg = aggTask.Result;
            long Get(string k) => agg.TryGetValue(k, out var v) ? v : 0;

            return new UserStats
            {
                Today = today,
                Totals = new DailyDrinksModel { Pivo = (int)Get("pivo"), Vino = (int)Get("vino"), Panaky = (int)Get("panaky") },
                Days = (int)Get("days"),
                Record = recordTask.Result.Select(ToDaily).FirstOrDefault(r => r.Total > 0),
                Last30 = lastTask.Result.Select(ToDaily).OrderByDescending(d => d.Day).ToList(),
                CurrentPubName = pubName,
                CurrentPubAt = pubAt
            };
        }

        // ================= Přátelé =================

        /// <summary>
        /// Jedním dotazem (1 čtení za každé přátelství/žádost) vrátí příchozí žádosti i přátele.
        /// Přezdívky jsou uložené přímo v přátelství, takže se nedočítají profily.
        /// </summary>
        public async Task<(List<FriendsModel> Incoming, List<UserModel> Friends)> GetFriendsOverview(string userId)
        {
            var docs = await FirestoreRest.QueryAsync("Friends", new[] { Filter.ArrayContains("Members", userId) });

            var incoming = new List<FriendsModel>();
            var friends = new List<UserModel>();

            foreach (var d in docs)
            {
                var from = d.Str("UserId") ?? "";
                var to = d.Str("FriendId") ?? "";
                var other = from == userId ? to : from;
                var nicks = d.StrMap("Nicks");
                nicks.TryGetValue(other, out var otherNick);

                switch (d.Str("Status"))
                {
                    case "Accepted":
                        friends.Add(new UserModel { Id = other, UserId = other, Nickname = otherNick });
                        break;
                    case "Pending" when to == userId:
                        incoming.Add(new FriendsModel
                        {
                            RequestId = d.Id,
                            UserId = other,
                            FriendId = userId,
                            Status = "Pending",
                            CreatedAt = d.Time("CreatedAt"),
                            FromNickname = otherNick
                        });
                        break;
                }
            }

            return (incoming, friends.OrderBy(f => f.Display, StringComparer.CurrentCultureIgnoreCase).ToList());
        }

        /// <summary>Pošle žádost. Vrací null při úspěchu, jinak text, proč to nešlo.</summary>
        public async Task<string?> SendFriendRequest(string fromUserId, UserModel target)
        {
            var toUserId = target.UserId;
            if (fromUserId == toUserId)
                return "Sám se sebou se kamarádit nemůžeš. Tedy můžeš, ale ne tady.";

            var mine = await FirestoreRest.QueryAsync("Friends", new[] { Filter.ArrayContains("Members", fromUserId) });
            var existing = mine.FirstOrDefault(d => d.StrArray("Members").Contains(toUserId));
            if (existing != null)
            {
                if (existing.Str("Status") == "Accepted")
                    return "Už jste přátelé.";
                return existing.Str("UserId") == fromUserId
                    ? "Žádost už jsi poslal, čeká se na něj."
                    : "On už poslal žádost tobě – přijmi ji nahoře v seznamu.";
            }

            await UserSession.EnsureReadyAsync();
            var ids = new[] { fromUserId, toUserId }.OrderBy(x => x, StringComparer.Ordinal).ToArray();

            await FirestoreRest.CreateAsync("Friends", new()
            {
                ["UserId"] = fromUserId,
                ["FriendId"] = toUserId,
                ["Members"] = new List<string> { fromUserId, toUserId },
                ["Nicks"] = new Dictionary<string, object?>
                {
                    [fromUserId] = UserSession.Profile?.Nickname,
                    [toUserId] = target.Nickname
                },
                ["Status"] = "Pending",
                ["CreatedAt"] = DateTime.UtcNow
            }, documentId: $"{ids[0]}_{ids[1]}");
            return null;
        }

        /// <summary>Přijmout = Status Accepted; odmítnout = smazat (ať se neválí a nežerou čtení).</summary>
        public Task UpdateFriendRequest(string requestId, string newStatus) =>
            newStatus == "Accepted"
                ? FirestoreRest.PatchAsync($"Friends/{requestId}", new()
                {
                    ["Status"] = "Accepted",
                    ["UpdatedAt"] = DateTime.UtcNow
                })
                : FirestoreRest.DeleteAsync($"Friends/{requestId}");

        /// <summary>Kolik žádostí o přátelství čeká na mě (odznak na tlačítku Přátelé). Čtení = počet žádostí, min. 1.</summary>
        public async Task<int> CountIncomingRequests(string userId)
        {
            try
            {
                var docs = await FirestoreRest.QueryAsync("Friends", new[]
                {
                    Filter.Eq("FriendId", userId),
                    Filter.Eq("Status", "Pending")
                });
                return docs.Count;
            }
            catch (FirestoreException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // kdyby pravidla dotaz podle FriendId nepustila → dražší cesta přes všechna přátelství
                var (incoming, _) = await GetFriendsOverview(userId);
                return incoming.Count;
            }
        }

        /// <summary>Odebrat z kámošů = smazat přátelství (druhému nic nepřijde, jen zmizí ze seznamu).</summary>
        public async Task RemoveFriend(string userId, string friendId)
        {
            var docs = await FirestoreRest.QueryAsync("Friends", new[] { Filter.ArrayContains("Members", userId) });
            foreach (var d in docs.Where(d => d.StrArray("Members").Contains(friendId)))
                await FirestoreRest.DeleteAsync($"Friends/{d.Id}");
        }

        // ================= mapování =================

        internal static UserModel ToUser(FsDoc d) => new()
        {
            Id = d.Id,
            UserId = d.Str("UserId") ?? d.Id,
            Name = d.Str("Name"),
            Nickname = d.Str("Nickname"),
            Gender = d.Str("Gender"),
            DrinkerType = d.Str("DrinkerType"),
            Email = d.Str("Email"),
            NotifyFriends = d.Fields["NotifyFriends"]?["booleanValue"]?.GetValue<bool>() ?? true,
            SharePub = d.Fields["SharePub"]?["booleanValue"]?.GetValue<bool>() ?? false
        };

        internal static DateTime? ParseIso(string? s) =>
            DateTime.TryParseExact(s, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

        internal static DailyDrinksModel ToDaily(FsDoc d) => new()
        {
            Id = d.Id,
            UserId = d.Str("UserId") ?? "",
            Date = d.Str("Date") ?? "",
            Day = ParseIso(d.Str("Day")) ?? PubDay.Parse(d.Str("Date")) ?? DateTime.MinValue,
            UpdatedAt = d.Time("UpdatedAt") ?? d.Time("UpdateAt"),
            Pivo = (int)d.Int("Pivo"),
            Vino = (int)d.Int("Vino"),
            Panaky = (int)d.Int("Panaky")
        };
    }
}
