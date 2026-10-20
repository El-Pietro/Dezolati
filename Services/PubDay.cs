using System.Globalization;

namespace Dezolati.Services
{
    /// <summary>
    /// Hospodský den (tah):
    ///  - mezi půlnocí a HardCutHour (6:00) tah pokračuje, jen pokud jsi včera pil a od posledního drinku
    ///    (nejdřív ale od 23:00) neuběhlo MaxPause (3 h); jinak začíná nový den (pojistka pro ranní ptáčata),
    ///  - v 6:00 končí každý tah, i nonstop,
    ///  - přes den je den = kalendářní datum.
    /// </summary>
    public static class PubDay
    {
        public const int HardCutHour = 6;
        public static readonly TimeSpan MaxPause = TimeSpan.FromHours(3);
        /// <summary>Od kolika hodin večer se pauza začíná počítat (dřívější pauzy se ignorují).</summary>
        public const int PauseCountsFromHour = 23;

        /// <param name="nowLocal">teď (místní čas)</param>
        /// <param name="lastDrinkLocal">čas posledního zápisu (místní), null = nikdy</param>
        /// <param name="lastDrinkDay">ke kterému dni se poslední zápis počítal</param>
        public static DateTime Resolve(DateTime nowLocal, DateTime? lastDrinkLocal, DateTime? lastDrinkDay)
        {
            var yesterday = nowLocal.Date.AddDays(-1);

            // pokračovat se dá jen v tahu, který patří ke včerejšku (musel jsi včera pít)
            if (nowLocal.Hour < HardCutHour
                && lastDrinkLocal is DateTime last
                && lastDrinkDay is DateTime day
                && day.Date == yesterday
                && nowLocal >= last)
            {
                // pauza se počítá nejdřív od 23:00 včerejšího večera
                var pauseFrom = yesterday.AddHours(PauseCountsFromHour);
                if (last > pauseFrom) pauseFrom = last;

                if (nowLocal - pauseFrom < MaxPause)
                    return yesterday; // tah pokračuje
            }

            return nowLocal.Date;
        }

        /// <summary>Textový klíč dne – stejný formát jako ve starých datech (dd-MM-yyyy).</summary>
        public static string Key(DateTime day) => day.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

        public static DateTime? Parse(string? key) =>
            DateTime.TryParseExact(key, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
