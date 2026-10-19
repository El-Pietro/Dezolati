using System.Text.Json;
#if ANDROID
using Plugin.LocalNotification;
using Plugin.LocalNotification.AndroidOption;
#endif

namespace Dezolati.Services
{
    /// <summary>
    /// Připomínka „už X minut nic?“ – lokální notifikace, žádný server.
    ///  - po každém zápisu se naplánuje JEDNA připomínka za Minutes minut (další zápis ji posune, žádný spam),
    ///  - text a tlačítko podle toho, co dnes převažuje (pivo / víno / panák),
    ///  - „Dneska končím“ vypne připomínky do konce tahu (ruční zápis v appce je zase zapne),
    ///  - nikdy přes hranici 6:00 (konec tahu).
    /// Zatím jen Android; jinde se tiše nic neděje.
    /// </summary>
    public static class Reminders
    {
        public const int NotificationId = 4242;
        public const int ActionPlusOne = 1;
        public const int ActionDone = 2;

        private const string KeyEnabled = "rem_enabled";
        private const string KeyMinutes = "rem_minutes";
        private const string KeyStopDay = "rem_stop_day";
        private const string KeyTally = "rem_tally";
        private const string KeyAsked = "rem_permission_asked";

        /// <summary>Druh pití: (do textu, na tlačítko, pro zápis).</summary>
        public enum Kind { Pivo, Vino, Panak }

        private record Tally(DateTime Day, int Pivo, int Vino, int Panaky);

        /// <summary>Interval v minutách; 0 = vypnuto. Ukládá se jen v telefonu.</summary>
        public static int Minutes
        {
            get => Preferences.Get(KeyEnabled, true) ? Preferences.Get(KeyMinutes, 30) : 0;
            set
            {
                Preferences.Set(KeyEnabled, value > 0);
                if (value > 0) Preferences.Set(KeyMinutes, value);
                else Cancel();
            }
        }

        // ---------------- volá appka ----------------

        /// <summary>Po zápisu: započítat do dnešního přehledu a naplánovat další připomínku.</summary>
        public static async Task OnDrinkRecordedAsync(DateTime day, int pivo, int vino, int panaky, bool fromReminder)
        {
            AddToTally(day, pivo, vino, panaky);
            if (!fromReminder) Preferences.Remove(KeyStopDay); // ruční zápis = zjevně nekončíš
            await ScheduleAsync(day);
        }

        /// <summary>Po škrtnutí čárky jen upravit přehled (připomínku neposouvat).</summary>
        public static void OnCorrection(DateTime day, int pivo, int vino, int panaky) =>
            AddToTally(day, -Math.Abs(pivo), -Math.Abs(vino), -Math.Abs(panaky));

        /// <summary>Jednou požádat o povolení notifikací (Android 13+).</summary>
        public static async Task EnsurePermissionAsync()
        {
#if ANDROID
            if (Minutes == 0 || Preferences.Get(KeyAsked, false)) return;
            Preferences.Set(KeyAsked, true);
            try
            {
                if (!await LocalNotificationCenter.Current.AreNotificationsEnabled())
                    await LocalNotificationCenter.Current.RequestNotificationPermission();
            }
            catch { /* bez povolení prostě nepřijdou */ }
#else
            await Task.CompletedTask;
#endif
        }

        public static void Cancel()
        {
#if ANDROID
            try
            {
                LocalNotificationCenter.Current.Cancel(NotificationId);
                LocalNotificationCenter.Current.Clear(NotificationId);
            }
            catch { }
#endif
        }

        // ---------------- tlačítka v notifikaci ----------------

        /// <summary>Obsluha tlačítek z notifikace (může běžet i bez otevřené appky).</summary>
        public static async Task HandleActionAsync(int actionId, string? returningData)
        {
            var day = LoadTally()?.Day ?? UserSession.CurrentPubDay;

            if (actionId == ActionDone)
            {
                Preferences.Set(KeyStopDay, PubDay.Key(day));
                Cancel();
                return;
            }

            if (actionId == ActionPlusOne && AuthSession.Uid is string uid)
            {
                var kind = Enum.TryParse<Kind>(returningData, out var k) ? k : Kind.Pivo;
                try
                {
                    await new FirestoreService().RecordDrink(uid, null,
                        kind == Kind.Pivo ? 1 : 0, kind == Kind.Vino ? 1 : 0, kind == Kind.Panak ? 1 : 0,
                        fromReminder: true);
                    UserSession.RaiseDrinksChanged(); // otevřený lístek se hned překreslí
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Zápis z notifikace selhal: {ex.Message}");
                }
            }
        }

        // ---------------- plánování ----------------

        private static async Task ScheduleAsync(DateTime day)
        {
            Cancel();
            var minutes = Minutes;
            if (minutes <= 0) return;
            if (Preferences.Get(KeyStopDay, "") == PubDay.Key(day)) return;

            var now = DateTime.Now;
            var notifyAt = now.AddMinutes(minutes);

            // nepřipomínat přes 6:00 – tah stejně končí
            var cut = now.Date.AddHours(PubDay.HardCutHour);
            if (now < cut && notifyAt >= cut) return;

            var kind = Dominant(day);
            var what = kind switch
            {
                Kind.Vino => "víno",
                Kind.Panak => "panáka",
                _ => "pivo"
            };

#if ANDROID
            var request = new NotificationRequest
            {
                NotificationId = NotificationId,
                Title = $"Už {minutes} minut nic?",
                Description = $"Nezapomněl sis zapsat {what}? Nebo už dneska končíš?",
                CategoryType = CategoryFor(kind),
                ReturningData = kind.ToString(),
                Schedule = new NotificationRequestSchedule { NotifyTime = notifyAt },
                Android = new AndroidOptions
                {
                    IconSmallName = new AndroidIcon { ResourceName = "ic_stat_beer" }
                }
            };
            try
            {
                await LocalNotificationCenter.Current.Show(request);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Připomínku se nepodařilo naplánovat: {ex.Message}");
            }
#else
            _ = what;
            await Task.CompletedTask;
#endif
        }

#if ANDROID
        /// <summary>Každý druh má vlastní kategorii, aby tlačítko „+1 …“ neslo správný text.</summary>
        public static NotificationCategoryType CategoryFor(Kind kind) => kind switch
        {
            Kind.Vino => NotificationCategoryType.Reminder,
            Kind.Panak => NotificationCategoryType.Recommendation,
            _ => NotificationCategoryType.Status
        };
#endif

        // ---------------- dnešní přehled (jen v telefonu) ----------------

        private static Kind Dominant(DateTime day)
        {
            var t = LoadTally();
            if (t == null || t.Day.Date != day.Date) return Kind.Pivo;
            if (t.Vino > t.Pivo && t.Vino >= t.Panaky) return Kind.Vino;
            if (t.Panaky > t.Pivo && t.Panaky > t.Vino) return Kind.Panak;
            return Kind.Pivo;
        }

        private static void AddToTally(DateTime day, int pivo, int vino, int panaky)
        {
            var t = LoadTally();
            if (t == null || t.Day.Date != day.Date) t = new Tally(day.Date, 0, 0, 0);
            t = t with
            {
                Pivo = Math.Max(0, t.Pivo + pivo),
                Vino = Math.Max(0, t.Vino + vino),
                Panaky = Math.Max(0, t.Panaky + panaky)
            };
            try { Preferences.Set(KeyTally, JsonSerializer.Serialize(t)); } catch { }
        }

        private static Tally? LoadTally()
        {
            try
            {
                var json = Preferences.Get(KeyTally, null as string);
                return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Tally>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
