using System.Text.Json;
using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>
    /// Poslední známý stav v telefonu (Preferences), aby appka šla otevřít i bez signálu.
    /// Nic z toho není zdroj pravdy – jen záloha, když server není k dispozici.
    /// </summary>
    public static class LocalCache
    {
        private record ProfileSnapshot(UserModel Profile, DateTime? LastDrinkAt, DateTime? LastDrinkDay);
        private record DailySnapshot(DateTime Day, int Pivo, int Vino, int Panaky);

        private static string ProfileKey(string uid) => $"cache_profile_{uid}";
        private static string DailyKey(string uid) => $"cache_daily_{uid}";

        public static void SaveProfile(string uid, UserModel profile, DateTime? lastDrinkAt, DateTime? lastDrinkDay) =>
            Save(ProfileKey(uid), new ProfileSnapshot(profile, lastDrinkAt, lastDrinkDay));

        public static (UserModel Profile, DateTime? LastDrinkAt, DateTime? LastDrinkDay)? LoadProfile(string uid)
        {
            var s = Load<ProfileSnapshot>(ProfileKey(uid));
            return s == null ? null : (s.Profile, s.LastDrinkAt, s.LastDrinkDay);
        }

        /// <summary>Ukládá jen jeden (poslední zobrazený) den – víc lístek nepotřebuje.</summary>
        public static void SaveDaily(string uid, DateTime day, DailyDrinksModel? daily) =>
            Save(DailyKey(uid), new DailySnapshot(day.Date, daily?.Pivo ?? 0, daily?.Vino ?? 0, daily?.Panaky ?? 0));

        public static DailyDrinksModel? LoadDaily(string uid, DateTime day)
        {
            var s = Load<DailySnapshot>(DailyKey(uid));
            if (s == null || s.Day.Date != day.Date) return null;
            return new DailyDrinksModel { UserId = uid, Day = s.Day, Pivo = s.Pivo, Vino = s.Vino, Panaky = s.Panaky };
        }

        public static void Clear(string uid)
        {
            Preferences.Remove(ProfileKey(uid));
            Preferences.Remove(DailyKey(uid));
        }

        private static void Save<T>(string key, T value)
        {
            try { Preferences.Set(key, JsonSerializer.Serialize(value)); }
            catch { /* cache není kritická */ }
        }

        private static T? Load<T>(string key) where T : class
        {
            try
            {
                var json = Preferences.Get(key, null as string);
                return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
