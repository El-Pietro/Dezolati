using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dezolati.Services
{
    /// <summary>Jeden zápis čekající na odeslání. Hodnoty můžou být záporné (škrtnutí čárky).</summary>
    public record PendingDrink(
        string Id,              // = ID dokumentu v Drinks → odeslání je idempotentní
        DateTime At,            // kdy se pilo (UTC)
        DateTime Day,           // hospodský den, určený v okamžiku pití
        int Pivo,
        int Vino,
        int Panaky,
        bool Correction,        // škrtnutí → neposouvá poslední drink
        string? Name);

    /// <summary>
    /// Fronta zápisů v telefonu. Každý zápis jde nejdřív sem a hned se zkusí odeslat.
    /// Bez signálu zůstane ve frontě a odešle se při dalším otevření lístku / návratu sítě.
    /// Pořadí se zachovává.
    /// </summary>
    public static class DrinkQueue
    {
        private static readonly SemaphoreSlim FlushLock = new(1, 1);   // jen jedno odesílání naráz
        private static readonly object FileLock = new();                // krátký zámek na čtení/zápis fronty
        private static bool _connectivityHooked;

        private static string Key(string uid) => $"pending_drinks_{uid}";

        public static event EventHandler? Changed;

        /// <summary>Při návratu sítě automaticky odeslat.</summary>
        public static void HookConnectivity()
        {
            if (_connectivityHooked) return;
            _connectivityHooked = true;
            try
            {
                Connectivity.Current.ConnectivityChanged += (_, e) =>
                {
                    if (e.NetworkAccess == NetworkAccess.Internet && AuthSession.Uid is string uid)
                        _ = FlushAsync(uid);
                };
            }
            catch { /* platforma bez podpory – nevadí */ }
        }

        public static List<PendingDrink> Load(string uid)
        {
            try
            {
                var json = Preferences.Get(Key(uid), null as string);
                return string.IsNullOrEmpty(json)
                    ? new List<PendingDrink>()
                    : JsonSerializer.Deserialize<List<PendingDrink>>(json) ?? new List<PendingDrink>();
            }
            catch
            {
                return new List<PendingDrink>();
            }
        }

        private static void Save(string uid, List<PendingDrink> items)
        {
            if (items.Count == 0) Preferences.Remove(Key(uid));
            else Preferences.Set(Key(uid), JsonSerializer.Serialize(items));
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void Enqueue(string uid, PendingDrink item)
        {
            lock (FileLock)
            {
                var items = Load(uid);
                items.Add(item);
                Save(uid, items);
            }
        }

        private static PendingDrink? Peek(string uid)
        {
            lock (FileLock) return Load(uid).FirstOrDefault();
        }

        private static void Remove(string uid, string id)
        {
            lock (FileLock)
            {
                var items = Load(uid);
                items.RemoveAll(i => i.Id == id);
                Save(uid, items);
            }
        }

        /// <summary>Součet čekajících zápisů pro daný den (aby je lístek ukázal hned).</summary>
        public static (int Pivo, int Vino, int Panaky, int Count) PendingFor(string uid, DateTime day)
        {
            var items = Load(uid).Where(i => i.Day.Date == day.Date).ToList();
            return (items.Sum(i => i.Pivo), items.Sum(i => i.Vino), items.Sum(i => i.Panaky), items.Count);
        }

        public static int Count(string uid) => Load(uid).Count;

        /// <summary>
        /// Pošle frontu na server (po jednom, v pořadí). Vrací true, když je fronta prázdná.
        /// Síťová chyba = konec, zkusí se příště. Jiná (trvalá) chyba = zápis se zahodí, ať fronta neuvízne.
        /// </summary>
        public static async Task<bool> FlushAsync(string uid)
        {
            if (!Net.IsOnline) return Load(uid).Count == 0;

            await FlushLock.WaitAsync();
            try
            {
                while (Peek(uid) is PendingDrink item)
                {
                    try
                    {
                        await FirestoreRest.CommitAsync(BuildWrites(uid, item));
                    }
                    catch (FirestoreException ex) when (ex.IsAlreadyExists)
                    {
                        // už to na serveru je (odpověď se minule ztratila) → hotovo
                    }
                    catch (Exception ex) when (Net.IsNetworkError(ex))
                    {
                        return false;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Zápis {item.Id} zahozen: {ex.Message}");
                    }

                    Remove(uid, item.Id);
                }
                return true;
            }
            finally
            {
                FlushLock.Release();
            }
        }

        /// <summary>3 zápisy v jedné atomické dávce (u škrtnutí 2).</summary>
        private static List<JsonObject> BuildWrites(string uid, PendingDrink d)
        {
            var key = PubDay.Key(d.Day);
            var iso = FirestoreService.IsoDay(d.Day);

            var writes = new List<JsonObject>
            {
                // 1) jednotlivý zápis (log); pevné ID = opakované odeslání nic nezdvojí
                FirestoreRest.SetWrite($"Drinks/{d.Id}", new()
                {
                    ["UserId"] = uid,
                    ["Name"] = d.Name,
                    ["Pivo"] = d.Pivo,
                    ["Vino"] = d.Vino,
                    ["Panaky"] = d.Panaky,
                    ["DrinkAt"] = d.At,
                    ["Date"] = key,
                    ["Day"] = iso,
                    ["Correction"] = d.Correction
                }, mustNotExist: true),

                // 2) denní součet – atomicky přičíst (u škrtnutí odečíst)
                FirestoreRest.SetWrite($"DailyDrinks/{FirestoreService.DailyId(uid, d.Day)}",
                    new()
                    {
                        ["UserId"] = uid,
                        ["Date"] = key,
                        ["Day"] = iso,
                        ["UpdatedAt"] = d.At
                    },
                    mask: new[] { "UserId", "Date", "Day", "UpdatedAt" },
                    increments: new Dictionary<string, long>
                    {
                        ["Pivo"] = d.Pivo,
                        ["Vino"] = d.Vino,
                        ["Panaky"] = d.Panaky,
                        ["Total"] = d.Pivo + d.Vino + d.Panaky
                    })
            };

            // 3) poslední drink kvůli 3h pauze (škrtnutí ho neposouvá)
            if (!d.Correction)
                writes.Add(FirestoreRest.SetWrite($"Users/{uid}",
                    new() { ["LastDrinkAt"] = d.At, ["LastDrinkDay"] = key },
                    mask: new[] { "LastDrinkAt", "LastDrinkDay" }));

            return writes;
        }
    }
}
