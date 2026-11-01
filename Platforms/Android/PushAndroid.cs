using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Firebase.Messaging;

namespace Dezolati.Droid
{
    /// <summary>
    /// Přijímá oznámení z Firebase Cloud Messaging.
    /// Když je appka v pozadí, oznámení zobrazí systém sám (kanál a ikona z AndroidManifestu),
    /// když je v popředí, zobrazíme ho tady.
    /// </summary>
    [Service(Exported = false)]
    [IntentFilter(new[] { "com.google.firebase.MESSAGING_EVENT" })]
    public class DezolatiMessagingService : FirebaseMessagingService
    {
        public override void OnNewToken(string token)
        {
            base.OnNewToken(token);
            _ = Services.PushService.OnNewTokenAsync(token);
        }

        public override void OnMessageReceived(RemoteMessage message)
        {
            base.OnMessageReceived(message);
            var n = message.GetNotification();
            string? D(string key) => message.Data.TryGetValue(key, out var v) ? v : null;
            var type = D("type");
            var title = n?.Title ?? D("title");
            var body = n?.Body ?? D("body");

            // moje přezdívka pro kámoše místo jeho vlastní („Pepa si právě dal…“ → „Strejda si právě dal…“)
            if (D("fromUid") is string aliasUid && D("fromNick") is string serverNick && serverNick.Length > 0
                && Services.FriendAliases.Of(aliasUid) is string alias)
            {
                if (title?.StartsWith(serverNick) == true) title = alias + title[serverNick.Length..];
                if (body?.StartsWith(serverNick) == true) body = alias + body[serverNick.Length..];
            }

            switch (type)
            {
                case "drink":
                    // „Žanet si právě dala pivo“ + tlačítka reakcí (jen jednou na sezení)
                    Services.FriendsNow.Invalidate();
                    var session = D("sessionId");
                    if (D("fromUid") is string from && session != null && !Services.Cheers.Reacted(session))
                        PushAndroid.AddDrinker(this, from, title ?? "", body, session,
                            D("inviteId"), D("inviteName"));   // spojené „kdo pije“ (zvlášť lidi z pozvánky) + Připít všem
                    else
                        PushAndroid.Show(this, title, body);
                    break;

                case "cheer":
                    // připití: jedno oznámení, které roste („6 kámošů si s tebou připilo“), a hned i do Kámošů
                    if (D("sessionId") is string mySession && D("fromUid") is string cheerFrom)
                    {
                        var all = Services.Cheers.OnReceived(mySession, cheerFrom, D("fromNick") ?? "Někdo", D("emoji") ?? "🍻");
                        PushAndroid.ShowCheers(this, all);
                    }
                    else
                        PushAndroid.Show(this, title, body);   // starší server
                    break;

                case "invite" when D("inviteId") != null:
                    // nová pozvánka na vybrané kámoše (nebo odpověď na ni) → odznak na Přátelích
                    Services.FriendBadge.Invalidate();
                    _ = Services.FriendBadge.RefreshAsync();
                    PushAndroid.Show(this, title, body);
                    break;

                case "invite":
                    // stará pozvánka (appka do 3.10) jen do 15 km (podle poslední polohy z appky; neznámá = ukázat)
                    if (double.TryParse(D("lat"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat)
                        && double.TryParse(D("lon"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon)
                        && !Services.Invites.IsNearMe(lat, lon))
                        break;
                    PushAndroid.Show(this, title, body);
                    break;

                default:
                    PushAndroid.Show(this, title, body);
                    break;
            }

            // žádost o přátelství → odznak na tlačítku Přátelé
            if (type == "friend")
            {
                Services.FriendBadge.Invalidate();
                _ = Services.FriendBadge.RefreshAsync();
            }
        }
    }

    /// <summary>
    /// Ťuknutí na reakci 🍺 🍷 🥃 v oznámení: připít bez otevírání appky.
    /// Proces appky může být „studený“ – MauiApplication se ale nastartuje i kvůli broadcastu,
    /// takže přihlášení (AuthSession) je k dispozici.
    /// </summary>
    [BroadcastReceiver(Exported = false)]
    public class CheerReceiver : BroadcastReceiver
    {
        public const string Action = "cz.elpietro.dezolati.CHEER";

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context == null || intent == null) return;
            var to = intent.GetStringExtra("to");
            var session = intent.GetStringExtra("session");
            var emoji = intent.GetStringExtra("emoji");
            var notifId = intent.GetIntExtra("notif", 0);
            var title = intent.GetStringExtra("title");
            var all = intent.GetBooleanExtra("all", false);
            var group = intent.GetStringExtra("group") ?? "";
            if (emoji == null || (!all && (to == null || session == null))) return;

            var pending = GoAsync();
            var appContext = context.ApplicationContext ?? context;
            _ = Task.Run(async () =>
            {
                string text;
                try
                {
                    if (all)
                    {
                        // „Připít všem“: každému, kdo v oznámení je a komu jsem ještě nepřipil
                        var targets = PushAndroid.Drinkers().Where(d => (d.Group ?? "") == group).ToList();
                        var sent = 0;
                        foreach (var d in targets)
                            if (await Services.Cheers.SendAsync(d.Uid, d.Session, emoji)) sent++;
                        text = sent == 0 ? "Všem už jsi připil(a) 👍"
                            : $"Připito {emoji} – {Services.Cz.Plural(sent, "kámošovi", "kámošům", "kámošům")}";
                        title = "Na zdraví!";
                    }
                    else
                    {
                        text = await Services.Cheers.SendAsync(to!, session!, emoji)
                            ? $"Připito {emoji}"
                            : "Už jsi připil(a) 👍";
                    }
                }
                catch (Exception ex)
                {
                    text = Services.Net.IsNetworkError(ex) ? "Bez signálu – připij v appce" : $"Nepovedlo se: {ex.Message}";
                }
                finally
                {
                    pending?.Finish();
                }
                PushAndroid.ClearDrinkers(all ? null : session, group);
                PushAndroid.Replace(appContext, notifId, title, text);
            });
        }
    }

    public static class PushAndroid
    {
        // všechna oznámení od kámošů v jedné skupině (Android je sbalí pod Dezoláty)
        private const string Group = "cz.elpietro.dezolati.KAMOSI";
        private const int SummaryId = 4243;
        private const int CheersId = 4242;

        /// <summary>Spojené připíjení: „6 kámošů si s tebou připilo 🍻“ + řádek za každého (přepisuje se).</summary>
        public static void ShowCheers(Context context, List<Services.CheerInfo> cheers)
        {
            if (cheers.Count == 0) return;
            try
            {
                EnsureChannel();
                var last = cheers[^1];
                var title = cheers.Count == 1
                    ? $"{last.FromNick} si s tebou připíjí {last.Emoji}"
                    : $"{Services.Cz.Plural(cheers.Count, "kámoš si", "kámoši si", "kámošů si")} s tebou {(cheers.Count < 5 ? "připili" : "připilo")} 🍻";
                var line = string.Join(" · ", cheers.Select(c => $"{c.FromNick} {c.Emoji}"));
                var builder = Base(context, title, cheers.Count == 1 ? "Na zdraví! 🍻" : line);
                if (cheers.Count > 1)
                {
                    var inbox = new NotificationCompat.InboxStyle().SetBigContentTitle(title);
                    foreach (var c in cheers.AsEnumerable().Reverse()) inbox.AddLine($"{c.FromNick} {c.Emoji}");
                    builder.SetStyle(inbox);
                }
                NotificationManagerCompat.From(context).Notify(CheersId, builder.Build());
                ShowSummary(context);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Push cheers: {ex.Message}");
            }
        }

        /// <summary>Souhrn skupiny (bez něj by Android oznámení nesbalil).</summary>
        private static void ShowSummary(Context context)
        {
            try
            {
                var summary = new NotificationCompat.Builder(context, ChannelId)
                    .SetSmallIcon(Resource.Drawable.ic_stat_beer)
                    .SetColor(unchecked((int)0xFF6B3E1E))
                    .SetContentTitle("Dezoláti")
                    .SetGroup(Group)
                    .SetGroupSummary(true)
                    .SetOnlyAlertOnce(true)
                    .SetAutoCancel(true);
                NotificationManagerCompat.From(context).Notify(SummaryId, summary.Build());
            }
            catch { }
        }

        // ---------------- spojené „kdo pije“ ----------------

        private const int DrinkersId = 4244;
        private const string KeyDrinkers = "push_pijou";
        private static readonly TimeSpan DrinkerMaxAge = TimeSpan.FromHours(3);
        private static readonly object DrinkersGate = new();

        /// <summary>Kámoš v oznámení „kdo pije“ (řádek = text ze serveru, sezení = na co se připíjí).</summary>
        /// <summary>Group = id pozvánky (lidi z ní mají vlastní oznámení), "" = ostatní kámoši.</summary>
        public sealed record Drinker(string Uid, string Line, string Session, DateTime At, string? Group = null, string? GroupName = null);

        /// <summary>Id oznámení pro skupinu: ostatní 4244, pozvánky stabilně podle id (ať se přepisuje to samé).</summary>
        private static int NotifIdFor(string group)
        {
            if (group.Length == 0) return DrinkersId;
            var h = 17;
            foreach (var c in group) h = unchecked(h * 31 + c);
            return 50_000 + (h & 0x3FFF);
        }

        /// <summary>Kdo je teď v oznámení: posledních 3 h, jen sezení, kterým jsem ještě nepřipil.</summary>
        public static List<Drinker> Drinkers()
        {
            lock (DrinkersGate)
            {
                List<Drinker> list;
                try { list = System.Text.Json.JsonSerializer.Deserialize<List<Drinker>>(Microsoft.Maui.Storage.Preferences.Get(KeyDrinkers, "[]")) ?? new(); }
                catch { list = new(); }
                return list.Where(d => DateTime.UtcNow - d.At < DrinkerMaxAge && !Services.Cheers.Reacted(d.Session)).ToList();
            }
        }

        private static void SaveDrinkers(List<Drinker> list)
        {
            lock (DrinkersGate)
            {
                try { Microsoft.Maui.Storage.Preferences.Set(KeyDrinkers, System.Text.Json.JsonSerializer.Serialize(list)); } catch { }
            }
        }

        /// <summary>Po připití: vyřadit jedno sezení (null = všechny).</summary>
        public static void ClearDrinkers(string? session, string group = "") =>
            SaveDrinkers(Drinkers().Where(d => session == null ? (d.Group ?? "") != group : d.Session != session).ToList());

        /// <summary>
        /// Další kámoš začal pít: místo dalšího oznámení přepsat jedno společné.
        /// Jeden = „Pepa si právě dal pivo“ + 🍺 🍷 🥃, víc = „Pijou: Pepa, Žanet a Karel“ + „Připít všem 🍻“.
        /// </summary>
        public static void AddDrinker(Context context, string fromUid, string line, string? body, string sessionId,
            string? inviteId = null, string? inviteName = null)
        {
            var group = inviteId ?? "";
            var all = Drinkers().Where(d => d.Uid != fromUid).ToList();
            all.Add(new Drinker(fromUid, line, sessionId, DateTime.UtcNow, group, inviteName));
            SaveDrinkers(all);

            var list = all.Where(d => (d.Group ?? "") == group).ToList();
            var notifId = NotifIdFor(group);
            var prefix = group.Length > 0 ? $"{inviteName ?? "Pozvánka"}: " : "";

            if (list.Count == 1)
            {
                ShowWithCheers(context, prefix + line, body, fromUid, sessionId, notifId);
                return;
            }
            try
            {
                EnsureChannel();
                // přezdívka = začátek řádku před „ si právě“
                string Nick(Drinker d) { var i = d.Line.IndexOf(" si ", StringComparison.Ordinal); return i > 0 ? d.Line[..i] : d.Line; }
                var nicks = list.Select(Nick).ToList();
                var names = nicks.Count <= 3
                    ? string.Join(", ", nicks.Take(nicks.Count - 1)) + " a " + nicks[^1]
                    : $"{string.Join(", ", nicks.TakeLast(2))} a {Services.Cz.Plural(nicks.Count - 2, "další", "další", "dalších")}";
                var title = group.Length > 0 ? $"{inviteName ?? "Pozvánka"}: pijou {names}" : $"Pijou: {names}";
                var builder = Base(context, title, "Připij si s nimi! 🍻");
                var inbox = new NotificationCompat.InboxStyle().SetBigContentTitle(title);
                foreach (var d in list.AsEnumerable().Reverse()) inbox.AddLine(d.Line);
                builder.SetStyle(inbox).SetOnlyAlertOnce(false);

                var intent = new Intent(context, typeof(CheerReceiver));
                intent.SetAction(CheerReceiver.Action);
                intent.PutExtra("all", true);
                intent.PutExtra("group", group);
                intent.PutExtra("emoji", "🍺");
                intent.PutExtra("notif", notifId);
                intent.PutExtra("title", title);
                var pi = PendingIntent.GetBroadcast(context, notifId * 4, intent,
                    PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
                builder.AddAction(0, "Připít všem 🍺", pi);

                NotificationManagerCompat.From(context).Notify(notifId, builder.Build());
                ShowSummary(context);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Push drinkers: {ex.Message}");
            }
        }

        /// <summary>Oznámení „X si právě dal(a)…“ s tlačítky 🍺 🍷 🥃.</summary>
        public static void ShowWithCheers(Context context, string? title, string? body, string fromUid, string sessionId, int? id = null)
        {
            try
            {
                EnsureChannel();
                var notifId = id ?? Random.Shared.Next(10_000, 1_000_000);
                var builder = Base(context, title, body);
                var req = 0;
                foreach (var emoji in Services.Cheers.Emojis)
                {
                    var intent = new Intent(context, typeof(CheerReceiver));
                    intent.SetAction(CheerReceiver.Action);
                    intent.PutExtra("to", fromUid);
                    intent.PutExtra("session", sessionId);
                    intent.PutExtra("emoji", emoji);
                    intent.PutExtra("notif", notifId);
                    intent.PutExtra("title", title);
                    var pi = PendingIntent.GetBroadcast(context, notifId * 4 + req++, intent,
                        PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
                    builder.AddAction(0, emoji, pi);
                }
                NotificationManagerCompat.From(context).Notify(notifId, builder.Build());
                ShowSummary(context);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Push show: {ex.Message}");
            }
        }

        /// <summary>Přepsat oznámení (po reakci) – bez tlačítek, samo zmizí po chvíli.</summary>
        public static void Replace(Context context, int notifId, string? title, string body)
        {
            try
            {
                var builder = Base(context, title, body).SetTimeoutAfter(8000);
                NotificationManagerCompat.From(context).Notify(notifId, builder.Build());
            }
            catch { }
        }

        private static NotificationCompat.Builder Base(Context context, string? title, string? body)
        {
            var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? "");
            intent?.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            var pending = intent == null ? null : PendingIntent.GetActivity(context, 0, intent,
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var b = new NotificationCompat.Builder(context, ChannelId)
                .SetSmallIcon(Resource.Drawable.ic_stat_beer)
                .SetColor(unchecked((int)0xFF6B3E1E))
                .SetContentTitle(title)
                .SetContentText(body)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))
                .SetPriority(NotificationCompat.PriorityHigh)
                .SetGroup(Group)
                .SetAutoCancel(true);
            if (pending != null) b.SetContentIntent(pending);
            return b;
        }

        public const string ChannelId = "kamosi";

        private static Context Ctx => global::Android.App.Application.Context;

        /// <summary>Kanál „Kámoši“ (Android 8+). Jde vypnout v nastavení telefonu zvlášť od připomínek.</summary>
        public static void EnsureChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            var nm = (NotificationManager?)Ctx.GetSystemService(Context.NotificationService);
            if (nm?.GetNotificationChannel(ChannelId) != null) return;
            nm?.CreateNotificationChannel(new NotificationChannel(ChannelId, "Kámoši", NotificationImportance.High)
            {
                Description = "Žádosti o přátelství, kdo z kámošů pije, připíjení a pozvánky"
            });
        }

        public static void Show(Context context, string? title, string? body)
        {
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body)) return;
            try
            {
                EnsureChannel();
                var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? "");
                intent?.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
                var pending = intent == null ? null : PendingIntent.GetActivity(context, 0, intent,
                    PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

                var builder = new NotificationCompat.Builder(context, ChannelId)
                    .SetSmallIcon(Resource.Drawable.ic_stat_beer)
                    .SetColor(unchecked((int)0xFF6B3E1E))
                    .SetContentTitle(title)
                    .SetContentText(body)
                    .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))
                    .SetPriority(NotificationCompat.PriorityHigh)
                    .SetGroup(Group)
                    .SetAutoCancel(true);
                if (pending != null) builder.SetContentIntent(pending);

                NotificationManagerCompat.From(context).Notify(Random.Shared.Next(10_000, 1_000_000), builder.Build());
                ShowSummary(context);
            }
            catch (Exception ex)
            {
                // bez povolení oznámení (Android 13+) se prostě nic nezobrazí
                System.Diagnostics.Debug.WriteLine($"Push show: {ex.Message}");
            }
        }

        /// <summary>Token tohoto telefonu pro FCM.</summary>
        public static Task<string?> GetTokenAsync()
        {
            var tcs = new TaskCompletionSource<string?>();
            FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new TokenListener(tcs));
            return tcs.Task;
        }

        private sealed class TokenListener : Java.Lang.Object, global::Android.Gms.Tasks.IOnCompleteListener
        {
            private readonly TaskCompletionSource<string?> _tcs;
            public TokenListener(TaskCompletionSource<string?> tcs) => _tcs = tcs;

            public void OnComplete(global::Android.Gms.Tasks.Task task)
            {
                if (task.IsSuccessful) _tcs.TrySetResult(task.Result?.ToString());
                else _tcs.TrySetException(task.Exception ?? new Exception("FCM token se nepodařilo získat."));
            }
        }
    }
}
