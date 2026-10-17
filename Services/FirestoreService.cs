using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>
    /// Doménové operace nad Firestore. Kolekce:
    ///   Users/{uid}                   – profil (přezdívka atd.)
    ///   Drinks/{auto}                 – každý jednotlivý zápis
    ///   DailyDrinks/{uid}_{yyyyMMdd}  – denní součet (hospodský den, viz PubDay)
    ///   Friends/{auto}                – žádosti/přátelství (UserId = žadatel, FriendId = adresát)
    /// </summary>
    public class FirestoreService
    {
        // ================= Uživatelé =================

        public async Task<UserModel?> GetUserData(string userId)
        {
            var doc = await FirestoreRest.GetAsync($"Users/{userId}");
            return doc == null ? null : ToUser(doc);
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
                ["Name"] = nickname
            });
        }

        public async Task<UserModel?> FindUserByNickname(string nickname)
        {
            var docs = await FirestoreRest.QueryAsync("Users", 1, ("Nickname", nickname.Trim()));
            return docs.Count > 0 ? ToUser(docs[0]) : null;
        }

        // ================= Pití =================

        private static string DailyId(string userId, DateTime day) => $"{userId}_{day:yyyyMMdd}";

        /// <summary>Denní součet pro daný hospodský den (null = čistý lístek).</summary>
        public async Task<DailyDrinksModel?> GetDailyDrinks(string userId, DateTime day)
        {
            var doc = await FirestoreRest.GetAsync($"DailyDrinks/{DailyId(userId, day)}");
            return doc == null ? null : ToDaily(doc);
        }

        /// <summary>
        /// Ke kterému dni patří zápis udělaný teď – podle posledního drinku (viz PubDay).
        /// Poslední drink si pamatujeme v Users/{uid} (LastDrinkAt, LastDrinkDay).
        /// </summary>
        public async Task<DateTime> GetCurrentPubDay(string userId)
        {
            var user = await FirestoreRest.GetAsync($"Users/{userId}");
            var lastAt = user?.Time("LastDrinkAt")?.ToLocalTime();
            var lastDay = PubDay.Parse(user?.Str("LastDrinkDay"));
            return PubDay.Resolve(DateTime.Now, lastAt, lastDay);
        }

        public async Task RecordDrink(string userId, string? name, int pivo, int vino, int panaky)
        {
            var now = DateTime.UtcNow;
            var day = await GetCurrentPubDay(userId);
            var key = PubDay.Key(day);

            // 1) jednotlivý zápis
            await FirestoreRest.CreateAsync("Drinks", new()
            {
                ["UserId"] = userId,
                ["Name"] = name,
                ["Pivo"] = pivo,
                ["Vino"] = vino,
                ["Panaky"] = panaky,
                ["DrinkAt"] = now,
                ["Date"] = key
            });

            // 2) denní součet – atomicky přičíst, dokument se případně založí
            await FirestoreRest.UpsertWithIncrementAsync(
                $"DailyDrinks/{DailyId(userId, day)}",
                set: new()
                {
                    ["UserId"] = userId,
                    ["Date"] = key,
                    ["UpdateAt"] = now
                },
                increment: new()
                {
                    ["Pivo"] = pivo,
                    ["Vino"] = vino,
                    ["Panaky"] = panaky
                });

            // 3) zapamatovat poslední drink kvůli 3h pauze
            await FirestoreRest.PatchAsync($"Users/{userId}", new()
            {
                ["LastDrinkAt"] = now,
                ["LastDrinkDay"] = key
            });
        }

        /// <summary>Všechny dny uživatele, nejnovější první. Staré záznamy se stejným datem se sečtou.</summary>
        public async Task<List<DailyDrinksModel>> GetDailyHistory(string userId)
        {
            var docs = await FirestoreRest.QueryAsync("DailyDrinks", 0, ("UserId", userId));

            return docs
                .Select(ToDaily)
                .Where(d => d.Day != DateTime.MinValue)
                .GroupBy(d => d.Day)
                .Select(g => new DailyDrinksModel
                {
                    Id = g.First().Id,
                    UserId = userId,
                    Date = g.First().Date,
                    Day = g.Key,
                    UpdateAt = g.Max(x => x.UpdateAt),
                    Pivo = g.Sum(x => x.Pivo),
                    Vino = g.Sum(x => x.Vino),
                    Panaky = g.Sum(x => x.Panaky)
                })
                .OrderByDescending(d => d.Day)
                .ToList();
        }

        // ================= Přátelé =================

        /// <summary>Pošle žádost. Vrací null při úspěchu, jinak text, proč to nešlo.</summary>
        public async Task<string?> SendFriendRequest(string fromUserId, string toUserId)
        {
            if (fromUserId == toUserId)
                return "Sám se sebou se kamarádit nemůžeš. Tedy můžeš, ale ne tady.";

            var mine = await FirestoreRest.QueryAsync("Friends", 0, ("UserId", fromUserId), ("FriendId", toUserId));
            var theirs = await FirestoreRest.QueryAsync("Friends", 0, ("UserId", toUserId), ("FriendId", fromUserId));

            var existing = mine.Concat(theirs).FirstOrDefault(d => d.Str("Status") is "Pending" or "Accepted");
            if (existing != null)
            {
                if (existing.Str("Status") == "Accepted")
                    return "Už jste přátelé.";
                return existing.Str("UserId") == fromUserId
                    ? "Žádost už jsi poslal, čeká se na něj."
                    : "On už poslal žádost tobě – přijmi ji nahoře v seznamu.";
            }

            await FirestoreRest.CreateAsync("Friends", new()
            {
                ["UserId"] = fromUserId,
                ["FriendId"] = toUserId,
                ["Status"] = "Pending",
                ["CreatedAt"] = DateTime.UtcNow
            });
            return null;
        }

        /// <summary>Žádosti, které čekají na MĚ.</summary>
        public async Task<List<FriendsModel>> GetPendingFriendRequests(string userId)
        {
            var docs = await FirestoreRest.QueryAsync("Friends", 0, ("FriendId", userId), ("Status", "Pending"));

            var result = new List<FriendsModel>();
            foreach (var d in docs)
            {
                var fromId = d.Str("UserId") ?? "";
                var from = string.IsNullOrEmpty(fromId) ? null : await GetUserData(fromId);
                result.Add(new FriendsModel
                {
                    RequestId = d.Id,
                    UserId = fromId,
                    FriendId = userId,
                    Status = "Pending",
                    CreatedAt = d.Time("CreatedAt"),
                    FromNickname = from?.Nickname
                });
            }
            return result;
        }

        public Task UpdateFriendRequest(string requestId, string newStatus) =>
            FirestoreRest.PatchAsync($"Friends/{requestId}", new()
            {
                ["Status"] = newStatus,
                ["UpdatedAt"] = DateTime.UtcNow
            });

        /// <summary>Přátelé oběma směry (ať jsem žádost poslal já, nebo on).</summary>
        public async Task<List<UserModel>> GetFriendsList(string userId)
        {
            var sent = await FirestoreRest.QueryAsync("Friends", 0, ("UserId", userId), ("Status", "Accepted"));
            var received = await FirestoreRest.QueryAsync("Friends", 0, ("FriendId", userId), ("Status", "Accepted"));

            var friendIds = sent.Select(d => d.Str("FriendId"))
                .Concat(received.Select(d => d.Str("UserId")))
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var friends = new List<UserModel>();
            foreach (var id in friendIds)
            {
                var user = await GetUserData(id!);
                friends.Add(user ?? new UserModel { Id = id!, UserId = id! });
            }

            return friends.OrderBy(f => f.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ================= mapování =================

        private static UserModel ToUser(FsDoc d) => new()
        {
            Id = d.Id,
            UserId = d.Str("UserId") ?? d.Id,
            Name = d.Str("Name"),
            Nickname = d.Str("Nickname"),
            Gender = d.Str("Gender"),
            DrinkerType = d.Str("DrinkerType"),
            Email = d.Str("Email")
        };

        private static DailyDrinksModel ToDaily(FsDoc d) => new()
        {
            Id = d.Id,
            UserId = d.Str("UserId") ?? "",
            Date = d.Str("Date") ?? "",
            Day = PubDay.Parse(d.Str("Date")) ?? DateTime.MinValue,
            UpdateAt = d.Time("UpdateAt"),
            Pivo = (int)d.Int("Pivo"),
            Vino = (int)d.Int("Vino"),
            Panaky = (int)d.Int("Panaky")
        };
    }
}
