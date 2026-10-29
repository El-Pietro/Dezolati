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
        /// <summary>
        /// Oznámení a hlášení kámošům (čte je Cloud Function): shareLevel 0 nehlásit, 1 že piju, 2 i kde.
        /// </summary>
        public Task UpdateNotificationPrefs(string userId, bool notifyFriends, int shareLevel)
        {
            var fields = new Dictionary<string, object?>
            {
                ["NotifyFriends"] = notifyFriends,
                ["ShareLevel"] = shareLevel,
                ["SharePub"] = shareLevel == 2
            };
            if (shareLevel < 2)
            {
                // přestal hlásit hospodu → kámoši už nemají vidět, kde sedí
                fields["CurrentPubId"] = null;
                fields["CurrentPubName"] = null;
                fields["CurrentPubAt"] = null;
                fields["CurrentPubLat"] = null;
                fields["CurrentPubLon"] = null;
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
        private async Task<(DateTime Day, string? PubName, DateTime? PubAt, Dictionary<string, FavoritePub> Favorites, FsDoc? User)> GetDayAndPubOf(string userId)
        {
            if (userId == AuthSession.Uid)
            {
                await UserSession.EnsureReadyAsync();
                var day = UserSession.CurrentPubDay;
                return (day, PushService.LastPubName(userId, day), UserSession.LastDrinkAt,
                    UserSession.Profile?.FavoritePubs ?? new(), null);
            }

            var user = await FirestoreRest.GetAsync($"Users/{userId}");
            var friendDay = PubDay.Resolve(DateTime.Now, user?.Time("LastDrinkAt")?.ToLocalTime(), PubDay.Parse(user?.Str("LastDrinkDay")));
            return (friendDay, user?.Str("CurrentPubName"), user?.Time("CurrentPubAt"),
                user == null ? new() : ParseFavorites(user), user);
        }

        /// <summary>Odhad promile uložený v profilu (null = nesdílí).</summary>
        internal static (double? Value, DateTime? At) PromileOf(FsDoc? user)
        {
            if (user == null) return (null, null);
            var node = user.Fields["PromileValue"];
            double? v = node?["doubleValue"]?.GetValue<double>()
                        ?? (node?["integerValue"] != null ? (double?)FsDoc.ValueToLong(node) : null);
            return (v, user.Time("PromileAt"));
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

            Promile.Log(userId, day, now, pivo, vino, panaky);   // dřív než zápis – odhad promile jde s ním
            DrinkQueue.Enqueue(userId, new PendingDrink(Guid.NewGuid().ToString("N"), now, day,
                pivo, vino, panaky, Correction: false, Name: name ?? UserSession.Profile?.Nickname,
                PubId: place.PubId, PubName: place.PubName, SharePub: PushService.SharePub,
                Place: place.Place, Kc: kc, NewVisit: newVisit,
                ShareDrink: PushService.ShareDrink, PubLat: place.Lat, PubLon: place.Lon,
                PromileSet: true, PromileValue: SharedPromile(userId)));
            if (PushService.DetectPub && place.Place != null)
                PushService.RememberPlace(userId, day, place);   // pro lístek: „📍 U Peťky“
            UserSession.OnDrinkRecorded(now, day);

            // další připomínka za X minut (lokální notifikace)
            try { await Reminders.OnDrinkRecordedAsync(day, pivo, vino, panaky, fromReminder); } catch { }

            return await DrinkQueue.FlushAsync(userId);
        }

        /// <summary>
        /// Odhad promile pro kámoše (zapíše se s pitím do profilu). Jen když počítám promile a hlásím, že piju;
        /// jinak null = v profilu se smaže. Váha nikam neodchází, jen výsledek.
        /// </summary>
        private static double? SharedPromile(string userId)
        {
            if (!PushService.ShareDrink) return null;
            var est = Promile.Estimate(userId, UserSession.Profile?.Gender);
            return est is { } e ? Math.Round(e.Promile, 3) : null;
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

            // škrtnutí ubere i z místa, kde se ten den pilo naposledy – jinak by „Kde se pije“ nesedělo
            // (2 piva na lístku, ale 6 kousků v místech)
            var isToday = day.Date == UserSession.CurrentPubDay.Date;
            if (isToday)
                Promile.Log(userId, day, DateTime.UtcNow, -pivo, -vino, -panaky);
            DrinkQueue.Enqueue(userId, new PendingDrink(Guid.NewGuid().ToString("N"), DateTime.UtcNow, day.Date,
                -pivo, -vino, -panaky, Correction: true, Name: UserSession.Profile?.Nickname, Kc: -kc,
                Place: last.Place, PromileSet: isToday, PromileValue: isToday ? SharedPromile(userId) : null));
            Reminders.OnCorrection(day, pivo, vino, panaky);

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
            var isToday = day.Date == UserSession.CurrentPubDay.Date;
            if (isToday)
            {
                Reminders.Adjust(day, pivo, vino, panaky);
                Promile.Log(userId, day, DateTime.UtcNow, pivo, vino, panaky);
            }
            DrinkQueue.Enqueue(userId, new PendingDrink(Guid.NewGuid().ToString("N"), DateTime.UtcNow, day.Date,
                pivo, vino, panaky, Correction: true, Name: UserSession.Profile?.Nickname, Kc: kc,
                PromileSet: isToday, PromileValue: isToday ? SharedPromile(userId) : null));
            return await DrinkQueue.FlushAsync(userId);
        }

        /// <summary>
        /// Statistiky za ~35 čtení bez ohledu na délku historie:
        /// 2 agregace + posledních 30 dní (≤30) + rekord (1) + hospodský den kámoše (1) + hospody (1).
        /// </summary>
        public async Task<UserStats> GetStats(string userId)
        {
            var (today, pubName, pubAt, favorites, userDoc) = await GetDayAndPubOf(userId);
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

            // útrata a místa za celou historii – každé přes vlastní složený index (firestore.indexes.json),
            // protože index obsahuje jen dny, které ta pole mají (útrata i odhad zpětně, místa od 3.7).
            // Doplňkové věci: když selžou (třeba index se ještě staví), statistiky se ukážou i bez nich.
            var kcTask = Optional(FirestoreRest.AggregateAsync("DailyDrinks", byUser,
                new Dictionary<string, string> { ["kc"] = "Kc" }), "útrata");
            var placesTask = Optional(FirestoreRest.AggregateAsync("DailyDrinks", byUser,
                new Dictionary<string, string> { ["home"] = "PlaceHome", ["other"] = "PlaceOther", ["pub"] = "PlacePub" }), "místa");
            var pubsTask = Optional(GetPubStats(userId), "hospody");
            // první zápis (průměr na den) – index UserId + Day vzestupně
            var firstTask = Optional(FirestoreRest.QueryAsync("DailyDrinks", byUser,
                orderBy: "Day", descending: false, limit: 3), "první den");

            await Task.WhenAll(aggTask, lastTask, recordTask, kcTask, placesTask, pubsTask, firstTask);

            var agg = aggTask.Result;
            long Get(string k) => agg.TryGetValue(k, out var v) ? v : 0;
            var agg2 = new Dictionary<string, long>();
            foreach (var kv in (kcTask.Result ?? new()).Concat(placesTask.Result ?? new())) agg2[kv.Key] = kv.Value;
            long Get2(string k) => agg2.TryGetValue(k, out var v) ? v : 0;

            var (promile, promileAt) = PromileOf(userDoc);
            var stats = new UserStats
            {
                Today = today,
                Gender = userId == AuthSession.Uid ? UserSession.Profile?.Gender : userDoc?.Str("Gender"),
                PromileValue = promile,
                PromileAt = promileAt,
                Totals = new DailyDrinksModel { Pivo = (int)Get("pivo"), Vino = (int)Get("vino"), Panaky = (int)Get("panaky") },
                Days = (int)Get("days"),
                FirstDay = firstTask.Result?.Select(ToDaily).FirstOrDefault(d => d.Total > 0)?.Day,
                Record = recordTask.Result.Select(ToDaily).FirstOrDefault(r => r.Total > 0),
                Last30 = lastTask.Result.Select(ToDaily).OrderByDescending(d => d.Day).ToList(),
                CurrentPubName = pubName,
                CurrentPubAt = pubAt,
                TotalKc = Get2("kc"),
                TotalPlaceHome = Get2("home"),
                TotalPlacePub = Get2("pub"),
                TotalPlaceOther = Get2("other"),
                Pubs = pubsTask.Result ?? new(),
                FavoritePubs = favorites
            };
            if (Mask.AppliesTo(userDoc)) ApplyMask(stats);
            return stats;
        }

        /// <summary>Tajný režim: třetina každého dne, součty z toho, nic „kde teď sedí“ ani promile.</summary>
        private static void ApplyMask(UserStats s)
        {
            var raw30 = new DailyDrinksModel
            {
                Pivo = s.Last30.Sum(d => d.Pivo), Vino = s.Last30.Sum(d => d.Vino), Panaky = s.Last30.Sum(d => d.Panaky)
            };
            s.Last30 = s.Last30.Select(Mask.Day).ToList();
            // celkem = zamaskovaných 30 dní + třetina zbytku (ať „celkem“ nikdy není míň než „30 dní“)
            var rest = Mask.Shrink(new DailyDrinksModel
            {
                Pivo = s.Totals.Pivo - raw30.Pivo, Vino = s.Totals.Vino - raw30.Vino, Panaky = s.Totals.Panaky - raw30.Panaky
            });
            s.Totals = new DailyDrinksModel
            {
                Pivo = s.Last30.Sum(d => d.Pivo) + rest.Pivo,
                Vino = s.Last30.Sum(d => d.Vino) + rest.Vino
            };
            if (s.Record != null) s.Record = Mask.Day(s.Record);
            if (s.Record != null && s.Last30.FirstOrDefault(d => d.Total > s.Record.Total) is { } higher) s.Record = higher;
            s.Pubs = s.Pubs.Select(p => p with { Count = Mask.Count(p.Count), Kc = 0 }).ToList();
            s.CurrentPubName = null;
            s.CurrentPubAt = null;
            s.PromileValue = null;
            s.PromileAt = null;
            s.Masked = true;
        }

        /// <summary>
        /// Dny jednoho měsíce (statistika po měsících). Čtení = dny s pitím v tom měsíci.
        /// Tajný režim se uplatní stejně jako ve statistikách.
        /// </summary>
        public async Task<DailyDrinksModel> GetMonthTotal(string userId, int year, int month, bool masked)
        {
            var from = new DateTime(year, month, 1);
            var docs = await FirestoreRest.QueryAsync("DailyDrinks", new[]
            {
                Filter.Eq("UserId", userId), Filter.Gte("Day", IsoDay(from)), Filter.Lt("Day", IsoDay(from.AddMonths(1)))
            });
            var days = docs.Select(ToDaily).Select(d => masked ? Mask.Day(d) : d).ToList();
            return new DailyDrinksModel
            {
                Day = from,
                Pivo = days.Sum(d => d.Pivo), Vino = days.Sum(d => d.Vino), Panaky = days.Sum(d => d.Panaky)
            };
        }

        /// <summary>Doplňková část statistik: chyba = null (a do logu), ne pád celé stránky.</summary>
        private static async Task<T?> Optional<T>(Task<T> task, string what) where T : class
        {
            try { return await task; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Statistiky ({what}): {ex.Message}");
                return null;
            }
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
            SharePub = d.Fields["SharePub"]?["booleanValue"]?.GetValue<bool>() ?? false,
            // starší profily úroveň nemají: výchozí 2 (vše zapnuté, kdo nechce, vypne)
            ShareLevel = d.Has("ShareLevel") ? (int)Math.Clamp(d.Int("ShareLevel"), 0, 2) : 2,
            FavoritePubs = ParseFavorites(d),
            MaskFor = d.StrArray("MaskFor")
        };

        /// <summary>FavoritePubs: mapa id → { Name, Kind, Lat, Lon }.</summary>
        internal static Dictionary<string, FavoritePub> ParseFavorites(FsDoc d)
        {
            var result = new Dictionary<string, FavoritePub>();
            if (d.Fields["FavoritePubs"]?["mapValue"]?["fields"] is not JsonObject map) return result;
            foreach (var (id, node) in map)
            {
                if (node?["mapValue"]?["fields"] is not JsonObject f) continue;
                double D(string k) => f[k]?["doubleValue"]?.GetValue<double>() ?? FsDoc.ValueToLong(f[k]);
                var name = f["Name"]?["stringValue"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;
                result[id] = new FavoritePub(name, f["Kind"]?["stringValue"]?.GetValue<string>(), D("Lat"), D("Lon"));
            }
            return result;
        }

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
