using System.Text;
using System.Text.RegularExpressions;

namespace Dezolati.Services
{
    /// <summary>
    /// Převod otvírací doby z OpenStreetMap (opening_hours) do češtiny:
    /// "Mo-Fr 16:00-24:00; Sa 14:00-02:00; Su off" → "Po–Pá 16–24 / So 14–02 / Ne zavřeno" (každé na svém řádku).
    /// Co nezná, nechá být (lepší anglický kus než nic).
    /// </summary>
    public static class OsmHours
    {
        private static readonly Dictionary<string, string> Days = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mo"] = "Po", ["Tu"] = "Út", ["We"] = "St", ["Th"] = "Čt",
            ["Fr"] = "Pá", ["Sa"] = "So", ["Su"] = "Ne", ["PH"] = "svátky"
        };

        private static readonly Regex Time = new(@"\b(\d{1,2}):(\d{2})\b", RegexOptions.Compiled);
        private static readonly Regex DayToken = new(@"\b(Mo|Tu|We|Th|Fr|Sa|Su|PH)\b", RegexOptions.Compiled);

        /// <summary>16:00 → 16, 16:30 → 16:30, po půlnoci dvouciferně (02) ať je jasné, že je to ráno.</summary>
        private static string FormatTime(int hour, string minutes)
        {
            var h = hour <= 6 ? hour.ToString("00") : hour.ToString();
            return minutes == "00" ? h : $"{h}:{minutes}";
        }

        public static string? ToCzech(string? osm)
        {
            if (string.IsNullOrWhiteSpace(osm)) return null;
            var s = osm.Trim();
            if (s == "24/7") return "nonstop";

            var parts = s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = new List<string>();
            foreach (var part in parts)
            {
                var p = part;

                // časy: 16:00 → 16, 16:30 → 16:30
                p = Time.Replace(p, m => FormatTime(int.Parse(m.Groups[1].Value), m.Groups[2].Value));

                p = DayToken.Replace(p, m => Days[m.Value]);
                p = Regex.Replace(p, @"\boff\b|\bclosed\b", "zavřeno", RegexOptions.IgnoreCase);
                p = Regex.Replace(p, @"\bopen\b", "otevřeno", RegexOptions.IgnoreCase);

                // pomlčky v rozsazích (dny i časy) → en dash, čárky s mezerou
                p = Regex.Replace(p, @"(?<=\S)-(?=\S)", "–");
                p = Regex.Replace(p, @",(?=\S)", ", ");
                result.Add(p);
            }

            var sb = new StringBuilder();
            foreach (var r in result)
            {
                if (sb.Length > 0) sb.Append('\n');   // každá skupina dnů na svůj řádek
                sb.Append(r);
            }
            return sb.ToString();
        }
    }
}
