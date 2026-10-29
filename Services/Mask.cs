using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>
    /// Tajný režim: kámoš, kterému to někdo zapnul (Users.MaskFor obsahuje jeho uid), vidí jen věrohodnou
    /// třetinu – každý den zvlášť, panáky se počítají k pivu, když se pilo, aspoň 1 kus (nula by byla podezřelá).
    /// Kdo teď pije, kde sedí a promile neuvidí vůbec. Skutečná data v databázi zůstávají.
    /// </summary>
    public static class Mask
    {
        /// <summary>Platí pro mě maska v profilu daného člověka?</summary>
        public static bool AppliesTo(FsDoc? owner) =>
            owner != null && AuthSession.Uid is string me && owner.Id != me && owner.StrArray("MaskFor").Contains(me);

        /// <summary>Jeden den → třetina (aspoň 1 kus, když se pilo).</summary>
        public static DailyDrinksModel Day(DailyDrinksModel d)
        {
            var masked = Shrink(d, minOne: true);
            masked.Kc = d.Kc;
            masked.KcEstimate = d.KcEstimate;
            masked.PlacePub = d.PlacePub;
            masked.PlaceHome = d.PlaceHome;
            masked.PlaceOther = d.PlaceOther;
            return masked;
        }

        /// <summary>Součty (bez „aspoň 1“).</summary>
        public static DailyDrinksModel Shrink(DailyDrinksModel d, bool minOne = false)
        {
            var beer = (int)Math.Round((Math.Max(0, d.Pivo) + Math.Max(0, d.Panaky)) / 3.0, MidpointRounding.AwayFromZero);
            var wine = (int)Math.Round(Math.Max(0, d.Vino) / 3.0, MidpointRounding.AwayFromZero);
            if (minOne && d.Total > 0 && beer + wine == 0)
            {
                if (d.Vino > d.Pivo + d.Panaky) wine = 1; else beer = 1;
            }
            return new DailyDrinksModel
            {
                Id = d.Id, UserId = d.UserId, Date = d.Date, Day = d.Day, UpdatedAt = d.UpdatedAt,
                Pivo = beer, Vino = wine, Panaky = 0
            };
        }

        public static int Count(int count) => count <= 0 ? 0 : Math.Max(1, (int)Math.Round(count / 3.0, MidpointRounding.AwayFromZero));
    }
}
