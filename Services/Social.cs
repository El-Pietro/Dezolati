using System.Text.Json;
using Dezolati.Models;

namespace Dezolati.Services
{
    // =====================================================================
    // Oblíbené hospody
    // =====================================================================

    /// <summary>
    /// Oblíbené hospody v profilu (Users/{uid}.FavoritePubs). Profil se čte při startu tak jako tak,
    /// takže zobrazení nestojí nic; ťuknutí na hvězdičku = 1 zápis. Kámoši je vidí na tvé stránce.
    /// </summary>
    public static class Favorites
    {
        public static event Action? Changed;

        public static IReadOnlyDictionary<string, FavoritePub> All =>
            UserSession.Profile?.FavoritePubs ?? new Dictionary<string, FavoritePub>();

        public static bool Is(string pubId) => UserSession.Profile?.FavoritePubs.ContainsKey(pubId) == true;

        /// <summary>Přepne oblíbenou; vrací nový stav.</summary>
        public static async Task<bool> ToggleAsync(PubInfo pub)
        {
            var uid = AuthSession.Uid ?? throw new InvalidOperationException("Nejsi přihlášený.");
            await UserSession.EnsureReadyAsync();
            var profile = UserSession.Profile ?? throw new InvalidOperationException("Profil není načtený.");

            var on = !profile.FavoritePubs.ContainsKey(pub.Id);
            var seg = DrinkQueue.FieldSegment(pub.Id);
            var name = PubService.NameOf(pub);
            var fields = new Dictionary<string, object?>();
            if (on)
                fields["FavoritePubs"] = new Dictionary<string, object?>
                {
                    [pub.Id] = new Dictionary<string, object?>
                    {
                        ["Name"] = name, ["Kind"] = pub.Kind, ["Lat"] = pub.Lat, ["Lon"] = pub.Lon
                    }
                };
            // pole v masce, ale ne v datech = smazat
            await FirestoreRest.CommitAsync(new[]
            {
                FirestoreRest.SetWrite($"Users/{uid}", fields, mask: new[] { $"FavoritePubs.{seg}" })
            });

            if (on) profile.FavoritePubs[pub.Id] = new FavoritePub(name, pub.Kind, pub.Lat, pub.Lon);
            else profile.FavoritePubs.Remove(pub.Id);
            UserSession.SetProfile(profile);
            Changed?.Invoke();
            return on;
        }

        /// <summary>Oblíbená jako PubInfo (pro mapu mimo stažené čtverce).</summary>
        public static PubInfo ToPub(string id, FavoritePub f) => new(id, f.Name, f.Kind ?? "pub", f.Lat, f.Lon, null, null, null);
    }

    // =====================================================================
    // Kdo teď pije
    // =====================================================================

    /// <summary>Kámoš, který pil v posledních 3 hodinách.</summary>
    public sealed record DrinkingFriend(
        string Uid, string Nick, DateTime LastDrinkAt,
        string? PubName, double? PubLat, double? PubLon,
        string? SessionId, string? Gender, string? LastKind = null,
        double? PromileValue = null, DateTime? PromileAt = null, DateTime? LastDrinkDay = null)
    {
        public bool HasPubLocation => PubLat != null && PubLon != null;

        /// <summary>Jeho aktuální hospodský den (kvůli „dnes vypito“).</summary>
        public DateTime PubDayNow => PubDay.Resolve(DateTime.Now, LastDrinkAt.ToLocalTime(), LastDrinkDay);

        /// <summary>Promile teď (z odhadu při jeho zápisu, s odbouráváním). null = nesdílí / střízlivý.</summary>
        public (double Promile, DateTime SoberAtLocal)? PromileNow => Promile.FromShared(PromileValue, PromileAt);

        /// <summary>Co si zapsal naposledy (starší verze appky to neposílají → pivo).</summary>
        public string Emoji => LastKind switch { "vino" => "🍷", "panak" => "🥃", _ => "🍺" };
    }

    /// <summary>
    /// „Teď pijí“: jeden dotaz Users where UserId in [kámoši] && LastDrinkAt >= teď − 3 h
    /// → čtení jen za ty, kdo zrovna pijí (min. 1). Seznam kámošů se drží v telefonu
    /// (obnoví se při každém otevření Přátel), aby mapa nemusela číst přátelství.
    /// </summary>
    public static class FriendsNow
    {
        public static readonly TimeSpan Window = TimeSpan.FromHours(3);
        private const string KeyFriends = "friends_cache";
        private static (DateTime At, List<DrinkingFriend> List, bool WithMe)? _cache;

        /// <summary>Po načtení stránky Přátelé: kdo jsou kámoši (uid → přezdívka).</summary>
        public static void RememberFriends(IEnumerable<UserModel> friends)
        {
            try
            {
                var map = friends.Where(f => !string.IsNullOrEmpty(f.UserId)).ToDictionary(f => f.UserId, f => f.Display);
                Preferences.Set(KeyFriends, JsonSerializer.Serialize(map));
                _cache = null;
            }
            catch { }
        }

        public static Dictionary<string, string>? KnownFriends()
        {
            try
            {
                var json = Preferences.Get(KeyFriends, null as string);
                return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Kámoši (a volitelně já), kteří pili v posledních 3 h. Výsledek se drží 2 minuty.
        /// Kdo má hlášení vypnuté (úroveň 0), se neukazuje.
        /// </summary>
        public static async Task<List<DrinkingFriend>> GetAsync(bool includeMe = false, bool force = false)
        {
            if (!force && _cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(2) && (c.WithMe || !includeMe))
                return includeMe ? c.List : c.List.Where(f => f.Uid != AuthSession.Uid).ToList();

            var me = AuthSession.Uid;
            var friends = KnownFriends();
            if (friends == null && me != null)
            {
                // ještě nikdy neotevřel Přátele → jednou načíst
                var (_, list) = await new FirestoreService().GetFriendsOverview(me);
                RememberFriends(list);
                friends = KnownFriends();
            }
            friends ??= new();

            var ids = friends.Keys.ToList();
            if (includeMe && me != null) ids.Add(me);
            var result = new List<DrinkingFriend>();
            if (ids.Count > 0)
            {
                var since = DateTime.UtcNow - Window;
                foreach (var chunk in ids.Chunk(30))
                {
                    var docs = await FirestoreRest.QueryAsync("Users",
                        new[] { Filter.In("UserId", chunk), Filter.Gte("LastDrinkAt", since) });
                    foreach (var d in docs)
                    {
                        var uid = d.Str("UserId") ?? d.Id;
                        var level = d.Has("ShareLevel") ? (int)d.Int("ShareLevel")
                            : 2;   // výchozí vše zapnuté
                        if (level == 0 && uid != me) continue;   // nechce, aby se vědělo, že pije
                        if (Mask.AppliesTo(d)) continue;          // tajný režim: pro mě „nepije“

                        var pubAt = d.Time("CurrentPubAt");
                        var pubFresh = level == 2 && pubAt is DateTime pa && DateTime.UtcNow - pa < Window;
                        double? Dbl(string f) => d.Fields[f]?["doubleValue"]?.GetValue<double>();
                        friends.TryGetValue(uid, out var nick);
                        result.Add(new DrinkingFriend(uid,
                            nick ?? d.Str("Nickname") ?? "?",
                            d.Time("LastDrinkAt") ?? DateTime.UtcNow,
                            pubFresh ? d.Str("CurrentPubName") : null,
                            pubFresh ? Dbl("CurrentPubLat") : null,
                            pubFresh ? Dbl("CurrentPubLon") : null,
                            d.Str("SessionId"),
                            d.Str("Gender"),
                            d.Str("LastDrinkKind"),
                            FirestoreService.PromileOf(d).Value,
                            FirestoreService.PromileOf(d).At,
                            PubDay.Parse(d.Str("LastDrinkDay"))));
                    }
                }
            }
            result = result.OrderByDescending(f => f.LastDrinkAt).ToList();
            _cache = (DateTime.UtcNow, result, includeMe);
            return includeMe ? result : result.Where(f => f.Uid != me).ToList();
        }

        public static void Invalidate() => _cache = null;

        /// <summary>„před 20 min“ / „před hodinou“ / „právě teď“.</summary>
        public static string Ago(DateTime utc)
        {
            var ago = DateTime.UtcNow - utc.ToUniversalTime();
            return ago.TotalMinutes < 2 ? "právě teď"
                : ago.TotalMinutes < 60 ? $"před {(int)ago.TotalMinutes} min"
                : (int)ago.TotalHours == 1 ? "před hodinou"
                : $"před {(int)ago.TotalHours} hodinami";
        }
    }

    // =====================================================================
    // Připíjení (reakce na první drink sezení)
    // =====================================================================

    public sealed record CheerInfo(string FromUid, string FromNick, string Emoji, DateTime? At);

    /// <summary>
    /// Reakce 🍺 🍷 🥃 na kámošovo sezení: Cheers/{sessionId}_{já} (jedna na sezení, server pošle oznámení
    /// „Petr si s tebou připíjí 🍺“). Sezení = pití bez 3h pauzy; id sezení zapisuje server do profilu.
    /// </summary>
    public static class Cheers
    {
        public static readonly string[] Emojis = { "🍺", "🍷", "🥃" };
        private const string KeyReacted = "cheers_reacted";

        private static HashSet<string> LoadReacted()
        {
            try
            {
                var json = Preferences.Get(KeyReacted, null as string);
                return string.IsNullOrEmpty(json) ? new() : JsonSerializer.Deserialize<HashSet<string>>(json) ?? new();
            }
            catch
            {
                return new();
            }
        }

        public static bool Reacted(string sessionId) => LoadReacted().Contains(sessionId);

        private static void MarkReacted(string sessionId)
        {
            try
            {
                var set = LoadReacted();
                set.Add(sessionId);
                // stačí pár posledních
                Preferences.Set(KeyReacted, JsonSerializer.Serialize(set.TakeLast(100).ToHashSet()));
            }
            catch { }
        }

        /// <summary>Připít kámošovi. Vrací false, když už jsi na tohle sezení připil.</summary>
        public static async Task<bool> SendAsync(string toUid, string sessionId, string emoji)
        {
            var me = AuthSession.Uid ?? throw new InvalidOperationException("Nejsi přihlášený.");
            if (!Emojis.Contains(emoji)) throw new ArgumentException("Neznámá reakce.");
            if (Reacted(sessionId)) return false;
            await UserSession.EnsureReadyAsync();

            try
            {
                await FirestoreRest.CommitAsync(new[]
                {
                    FirestoreRest.SetWrite($"Cheers/{sessionId}_{me}", new()
                    {
                        ["From"] = me,
                        ["FromNick"] = UserSession.Profile?.Nickname ?? "?",
                        ["To"] = toUid,
                        ["SessionId"] = sessionId,
                        ["Emoji"] = emoji
                    }, mustNotExist: true, serverTime: new[] { "At" })
                });
            }
            catch (FirestoreException ex) when (ex.IsAlreadyExists)
            {
                MarkReacted(sessionId);
                return false;
            }
            MarkReacted(sessionId);
            FriendsNow.Invalidate();
            return true;
        }

        // ---------------- přijatá připití (z oznámení, bez čtení) ----------------

        private const string KeyReceived = "cheers_received";

        /// <summary>Přišlo připití (oznámení) – stránka Kámoši si ho připíše hned. Může přijít z jiného vlákna.</summary>
        public static event Action<CheerInfo, string>? Received;

        private sealed record ReceivedCheer(string Session, string FromUid, string FromNick, string Emoji, DateTime At);

        /// <summary>
        /// Uložit přijaté připití (pro spojené oznámení) a dát vědět stránce. Vrací všechna připití
        /// k tomuhle sezení (nové sezení po 3h pauze = jiné id → začíná se znovu).
        /// </summary>
        public static List<CheerInfo> OnReceived(string sessionId, string fromUid, string fromNick, string emoji)
        {
            List<ReceivedCheer> list;
            try { list = JsonSerializer.Deserialize<List<ReceivedCheer>>(Preferences.Get(KeyReceived, "[]")) ?? new(); }
            catch { list = new(); }
            list = list.Where(c => c.Session == sessionId && c.FromUid != fromUid).ToList();
            list.Add(new ReceivedCheer(sessionId, fromUid, fromNick, emoji, DateTime.UtcNow));
            try { Preferences.Set(KeyReceived, JsonSerializer.Serialize(list)); } catch { }
            var info = new CheerInfo(fromUid, fromNick, emoji, DateTime.UtcNow);
            try { Received?.Invoke(info, sessionId); } catch { }
            return list.Select(c => new CheerInfo(c.FromUid, c.FromNick, c.Emoji, c.At)).ToList();
        }

        /// <summary>Kdo mi připil na moje sezení (čtení = počet reakcí, min. 1).</summary>
        public static async Task<List<CheerInfo>> ForMeAsync(string mySessionId)
        {
            var me = AuthSession.Uid;
            if (me == null) return new();
            var docs = await FirestoreRest.QueryAsync("Cheers",
                new[] { Filter.Eq("To", me), Filter.Eq("SessionId", mySessionId) });
            return docs.Select(d => new CheerInfo(d.Str("From") ?? "", d.Str("FromNick") ?? "?", d.Str("Emoji") ?? "🍻", d.Time("At")))
                .OrderBy(c => c.At).ToList();
        }
    }

    // =====================================================================
    // Pozvánky
    // =====================================================================

    /// <summary>
    /// „Petr tě zve na pivo do U Peťky“: Invites/{id} → server pošle kámošům, jejichž telefon
    /// (podle poslední známé polohy z používání appky) je do 15 km. Max. jedna za 20 minut.
    /// </summary>
    public static class Invites
    {
        public const double RadiusKm = 15;
        private const string KeyLast = "invite_last";
        public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(20);

        public static TimeSpan? WaitLeft()
        {
            var last = Preferences.Get(KeyLast, 0L);
            if (last == 0) return null;
            var left = MinGap - (DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc));
            return left > TimeSpan.Zero ? left : null;
        }

        public static async Task SendAsync(PubInfo pub)
        {
            var me = AuthSession.Uid ?? throw new InvalidOperationException("Nejsi přihlášený.");
            if (WaitLeft() is TimeSpan left)
                throw new InvalidOperationException($"Pozvánku jsi poslal před chvílí, další až za {Math.Ceiling(left.TotalMinutes)} min.");
            await UserSession.EnsureReadyAsync();

            await FirestoreRest.CommitAsync(new[]
            {
                FirestoreRest.SetWrite($"Invites/{Guid.NewGuid():N}", new()
                {
                    ["From"] = me,
                    ["FromNick"] = UserSession.Profile?.Nickname ?? "?",
                    ["PubId"] = pub.Id,
                    ["PubName"] = PubService.NameOf(pub),
                    ["Lat"] = pub.Lat,
                    ["Lon"] = pub.Lon
                }, mustNotExist: true, serverTime: new[] { "At" })
            });
            Preferences.Set(KeyLast, DateTime.UtcNow.Ticks);
        }

        /// <summary>Má telefon pozvánku ukázat? Podle poslední polohy z appky; neznámá poloha = ukázat.</summary>
        public static bool IsNearMe(double lat, double lon)
        {
            var loc = LastLocation.Get(TimeSpan.FromHours(24));
            return loc is not { } l || PubCatalog.DistanceMeters(lat, lon, l.Lat, l.Lon) <= RadiusKm * 1000;
        }
    }
}
