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
        /// Zápis pití: 0 čtení (+ max. 1 za 12 h kvůli cenám hospody), 3–4 zápisy v jedné atomické dávce.
        /// Jde přes frontu v telefonu – bez signálu se odešle později.
        /// Vrací true = uloženo na serveru, false = čeká ve frontě.
        /// </summary>
        public async Task<bool> RecordDrink(string userId, string? name, int pivo, int vino, int panaky,
            bool fromReminder = false)
        {
            await UserSession.EnsureReadyAsync();

            var now = DateTime.UtcNow;
            var day = UserSession.CurrentPubDay;   // den se určí teď, ne až při odeslání

            // kde jsem: při ručním zápisu podle polohy, z připomínky (bez polohy) jako minule
            var last = PushService.LastPlace(userId, day);
            var place = fromReminder ? last : await PushService.PlaceHereAsync();
            var newVisit = place.PubId != null && place.PubId != last.PubId;
            var kc = await Prices.ForDrinkAsync(pivo, vino, panaky, place.PubId);

            DrinkQueue.Enqueue(userId, new PendingDrink(Guid.NewGuid().ToString("N"), now, day,
                pivo, vino, panaky, Correction: false, Name: name ?? UserSession.Profile?.Nickname,
                PubId: place.PubId, PubName: place.PubName, SharePub: PushService.SharePub,
                Place: place.Place, Kc: kc, NewVisit: newVisit));
            if (PushService.DetectPub && place.Place != null)
                PushService.RememberPlace(userId, day, place);   // pro lístek: „📍 U Peťky“
            Promile.Log(userId, day, now, pivo, vino, panaky);
            UserSession.OnDrinkRecorded(now, day);

            // další připomínka za X minut (lokální notifikace)
            try { await Reminders.OnDrinkRecordedAsync(day, pivo, vino, panaky, fromReminder); } catch { }

            return await DrinkQueue.FlushAsync(userId);
        }

        /// <summary>
        /// Škrtnutí čárky z lístku daného dne (záporné hodnoty). Neposouvá čas posledního drinku.
        /// Útrata se odečte cenou hospody, kde se ten den pilo naposledy, jinak výchozí cenou.
        /// Vrací true = uloženo, false = čeká ve frontě.
        /// </summary>
        public async Task<bool> RecordCorrection(string userId, DateTime day, int pivo, int vino, int panaky)
        {
            pivo = Math.Abs(pivo); vino = Math.Abs(vino); panaky = Math.Abs(panaky);
            var last = PushService.LastPlace(userId, day);
            var kc = await Prices.ForDrinkAsync(pivo, vino, panaky, last.PubId);

            DrinkQueue.Enqueue(userId, new PendingDrink(Guid.NewGuid().ToString("N"), DateTime.UtcNow, day.Date,
                -pivo, -vino, -panaky, Correction: true, Name: UserSession.Profile?.Nickname, Kc: -kc));
            Reminders.OnCorrection(day, pivo, vino, panaky);
            if (day.Date == UserSession.CurrentPubDay.Date)
                Promile.Log(userId, day, DateTime.UtcNow, -pivo, -vino, -panaky);

            return await DrinkQueue.FlushAsync(userId);
        }

        /// <summary>
        /// Úprava staršího dne (max. 30 dní zpět): dopsat (kladné) nebo škrtnout (záporné).
        /// Jako korekce – neposouvá poslední pivo, místo „nevím“, útrata výchozími cenami.
        /// </summary>
        public async Task<bool> RecordPastEdit(string userId, DateTime day, int pivo, int vino, int panaky)
        {
            if (day.Date < DateTime.Today.AddDays(-31)) throw new InvalidOperationException("Upravit jde jen posledních 30 dní.");
            var kc = Prices.Default(pivo, vino, panaky);
            DrinkQueue.Enqueue(userId, new PendingDrink(Guid.NewGuid().ToString("N"), DateTime.UtcNow, day.Date,
                pivo, vino, panaky, Correction: true, Name: UserSession.Profile?.Nickname, Kc: kc));
            if (day.Date == UserSession.CurrentPubDay.Date)
            {
                Reminders.Adjust(day, pivo, vino, panaky);
                Promile.Log(userId, day, DateTime.UtcNow, pivo, vino, panaky);
            }
            return await DrinkQueue.FlushAsync(userId);
        }

        /// <summary>
        /// Statistiky za ~35 čtení bez ohledu na délku historie:
        /// 2 agregace + posledních 30 dní (≤30) + rekord (1) + hospodský den kámoše (1) + hospody (1).
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

            // útrata a místa za celou historii (agregace bere max. 5 polí → druhý dotaz, 1 čtení)
            var agg2Task = FirestoreRest.AggregateAsync("DailyDrinks", byUser,
                new Dictionary<string, string>
                {
                    ["kc"] = "Kc", ["home"] = "PlaceHome", ["pub"] = "PlacePub", ["other"] = "PlaceOther"
                });

            var pubsTask = GetPubStats(userId);

            await Task.WhenAll(aggTask, lastTask, recordTask, agg2Task, pubsTask);

            var agg = aggTask.Result;
            long Get(string k) => agg.TryGetValue(k, out var v) ? v : 0;
            var agg2 = agg2Task.Result;
            long Get2(string k) => agg2.TryGetValue(k, out var v) ? v : 0;

            return new UserStats
            {
                Today = today,
                Totals = new DailyDrinksModel { Pivo = (int)Get("pivo"), Vino = (int)Get("vino"), Panaky = (int)Get("panaky") },
                Days = (int)Get("days"),
                Record = recordTask.Result.Select(ToDaily).FirstOrDefault(r => r.Total > 0),
                Last30 = lastTask.Result.Select(ToDaily).OrderByDescending(d => d.Day).ToList(),
                CurrentPubName = pubName,
                CurrentPubAt = pubAt,
                TotalKc = Get2("kc"),
                TotalPlaceHome = Get2("home"),
                TotalPlacePub = Get2("pub"),
                TotalPlaceOther = Get2("other"),
                Pubs = pubsTask.Result
            };
        }

        // ================= Statistika hospod =================

        /// <summary>
        /// Kde dotyčný pije nejčastěji (1 čtení). U sebe při prvním otevření jednou dopočítá zápisy
        /// od verze 3.5 (od té doby mají hospodu) – čtení = počet takových zápisů.
        /// </summary>
        public async Task<List<PubStat>> GetPubStats(string userId)
        {
            var doc = await FirestoreRest.GetAsync($"UserPubStats/{userId}");
            var backfilled = doc?.Fields["Backfilled"]?["booleanValue"]?.GetValue<bool>() == true;
            if (!backfilled && userId == AuthSession.Uid)
            {
                try { return await BackfillPubStats(userId); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Dopočet hospod: {ex.Message}"); }
            }
            return ParsePubStats(doc);
        }

        private static List<PubStat> ParsePubStats(FsDoc? doc)
        {
            var list = new List<PubStat>();
            if (doc?.Fields["Pubs"]?["mapValue"]?["fields"] is not JsonObject pubs) return list;
            foreach (var (id, node) in pubs)
            {
                if (node?["mapValue"]?["fields"] is not JsonObject f) continue;
                var lastAt = f["LastAt"]?["timestampValue"]?.GetValue<string>();
                list.Add(new PubStat(id,
                    f["Name"]?["stringValue"]?.GetValue<string>(),
                    (int)FsDoc.ValueToLong(f["Count"]),
                    (int)FsDoc.ValueToLong(f["Visits"]),
                    (int)FsDoc.ValueToLong(f["Kc"]),
                    lastAt == null ? null : DateTime.Parse(lastAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)));
            }
            return list.Where(p => p.Count > 0)
                .OrderByDescending(p => p.Count).ThenByDescending(p => p.LastAt).ToList();
        }

        /// <summary>
        /// Jednorázově: statistika hospod ze zápisů, které hospodu mají (od 3.5 mají pole SharePub).
        /// Dva dotazy jen na rovnost → nepotřebují složený index.
        /// </summary>
        private async Task<List<PubStat>> BackfillPubStats(string userId)
        {
            var a = FirestoreRest.QueryAsync("Drinks", new[] { Filter.Eq("UserId", userId), Filter.Eq("SharePub", true) });
            var b = FirestoreRest.QueryAsync("Drinks", new[] { Filter.Eq("UserId", userId), Filter.Eq("SharePub", false) });
            await Task.WhenAll(a, b);

            var pubs = new Dictionary<string, (string? Name, int Count, HashSet<string> Days, DateTime? Last, int Kc)>();
            foreach (var d in a.Result.Concat(b.Result))
            {
                if (d.Str("PubId") is not string pubId) continue;
                if (d.Fields["Correction"]?["booleanValue"]?.GetValue<bool>() == true) continue;
                var total = (int)(d.Int("Pivo") + d.Int("Vino") + d.Int("Panaky"));
                if (total <= 0) continue;
                var at = d.Time("DrinkAt");
                var cur = pubs.TryGetValue(pubId, out var x) ? x : (null, 0, new HashSet<string>(), null, 0);
                cur.Count += total;
                cur.Kc += (int)d.Int("Kc");
                cur.Days.Add(d.Str("Day") ?? "");
                if (cur.Last == null || at > cur.Last) { cur.Last = at; cur.Name = d.Str("PubName") ?? cur.Name; }
                cur.Name ??= d.Str("PubName");
                pubs[pubId] = cur;
            }

            var map = pubs.ToDictionary(kv => kv.Key, kv => (object?)new Dictionary<string, object?>
            {
                ["Name"] = kv.Value.Name,
                ["Count"] = kv.Value.Count,
                ["Visits"] = kv.Value.Days.Count,
                ["Kc"] = kv.Value.Kc,
                ["LastAt"] = kv.Value.Last
            });

            // celý dokument naráz (zahrnuje i zápisy z 3.7, takže nic nechybí ani se nezdvojí)
            await FirestoreRest.CommitAsync(new[]
            {
                FirestoreRest.SetWrite($"UserPubStats/{userId}", new()
                {
                    ["UserId"] = userId,
                    ["Backfilled"] = true,
                    ["Pubs"] = map
                })
            });

            return pubs.Select(kv => new PubStat(kv.Key, kv.Value.Name, kv.Value.Count, kv.Value.Days.Count, kv.Value.Kc, kv.Value.Last))
                .OrderByDescending(p => p.Count).ThenByDescending(p => p.LastAt).ToList();
        }

        // ================= Útrata – zpětný odhad =================

        /// <summary>
        /// Dny bez útraty dostanou odhad podle výchozích cen (KcEstimate = true).
        /// Čtení = počet dní s pitím, zápisy = počet dní bez útraty. Vrací (dní, Kč).
        /// </summary>
        public async Task<(int Days, long Kc)> EstimatePastSpend(string userId)
        {
            if (!Prices.AnyDefault) throw new InvalidOperationException("Nejdřív si v Nastavení vyplň výchozí ceny.");
            var docs = await FirestoreRest.QueryAsync("DailyDrinks", new[] { Filter.Eq("UserId", userId) });
            var writes = new List<JsonObject>();
            long sum = 0;
            foreach (var d in docs)
            {
                if (d.Has("Kc")) continue;
                var kc = Prices.Default((int)d.Int("Pivo"), (int)d.Int("Vino"), (int)d.Int("Panaky"));
                if (kc <= 0) continue;
                sum += kc;
                writes.Add(FirestoreRest.SetWrite($"DailyDrinks/{d.Id}",
                    new() { ["Kc"] = kc, ["KcEstimate"] = true },
                    mask: new[] { "Kc", "KcEstimate" }));
            }
            await FirestoreRest.CommitAsync(writes);
            return (writes.Count, sum);
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
            Panaky = (int)d.Int("Panaky"),
            Kc = d.Has("Kc") ? (int)d.Int("Kc") : null,
            KcEstimate = d.Fields["KcEstimate"]?["booleanValue"]?.GetValue<bool>() == true,
            PlacePub = (int)d.Int("PlacePub"),
            PlaceHome = (int)d.Int("PlaceHome"),
            PlaceOther = (int)d.Int("PlaceOther")
        };
    }
}
