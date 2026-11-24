using System.Globalization;
using System.Text.Json.Nodes;

namespace Dezolati.Services
{
    /// <summary>Odznaky jednoho člověka v eventu (Achievements/{uid}, píše jen server – functions/vanoce.js).</summary>
    public sealed record BadgeState(string Event, Dictionary<string, (DateTime? At, string Info)> Got, Dictionary<string, int> Progress)
    {
        public static readonly BadgeState Empty = new("", new(), new());
        public bool Has(string id) => Got.ContainsKey(id);
    }

    /// <summary>Jeden odznak připravený k vykreslení (statistiky appky i webu).</summary>
    /// <summary>Sub = pod odznakem: získaný → datum / hospoda, nezískaný → postup („2/4 neděle“) nebo stručně jak („24. 12. do 15:00“).</summary>
    public sealed record BadgeView(EventBadge Badge, bool Got, string Sub, bool IsProgress = false);

    /// <summary>
    /// Odznaky: načtení (1 čtení, 5 min v paměti), popisky pod odznaky a hlídání nových
    /// (po pushi „badge“ hned, jinak nejvýš 1× za 10 min, jen když běží event).
    /// </summary>
    public static class Badges
    {
        private static readonly Dictionary<string, (DateTime At, BadgeState State)> Cache = new();
        private const string KeySeen = "badges_seen";
        private const string KeyCheckedAt = "badges_checked_at";
        private const string KeyForce = "badges_force";

        public static async Task<BadgeState> LoadAsync(string uid, bool fresh = false)
        {
            if (!fresh && Cache.TryGetValue(uid, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(5)) return c.State;
            var doc = await FirestoreRest.GetAsync($"Achievements/{uid}");
            var state = doc == null ? BadgeState.Empty : Parse(doc);
            Cache[uid] = (DateTime.UtcNow, state);
            return state;
        }

        private static BadgeState Parse(FsDoc doc)
        {
            var got = new Dictionary<string, (DateTime?, string)>();
            if (doc.Fields["Got"]?["mapValue"]?["fields"] is JsonObject g)
                foreach (var kv in g)
                {
                    var f = kv.Value?["mapValue"]?["fields"];
                    DateTime? at = f?["At"]?["timestampValue"]?.GetValue<string>() is string s
                        ? DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ToLocalTime()
                        : null;
                    got[kv.Key] = (at, f?["Info"]?["stringValue"]?.GetValue<string>() ?? "");
                }
            var prog = new Dictionary<string, int>();
            if (doc.Fields["Progress"]?["mapValue"]?["fields"] is JsonObject p)
                foreach (var kv in p)
                    prog[kv.Key] = (int)FsDoc.ValueToLong(kv.Value);
            return new(doc.Str("Event") ?? "", got, prog);
        }

        /// <summary>Odznaky eventu v pořadí z Events/aktualni s popiskem: datum / hospoda, nebo postup „2/4 neděle“.</summary>
        public static List<BadgeView> Views(BadgeState state) =>
            Events.Badges.Select(b =>
            {
                if (state.Got.TryGetValue(b.Id, out var g))
                    return new BadgeView(b, true, g.Info.Length > 0 ? g.Info : g.At is { } at ? $"{at.Day}. {at.Month}." : "");
                if (b.Need is int need && state.Progress.TryGetValue(b.Id, out var p) && p > 0)
                    return new BadgeView(b, false, $"{Math.Min(p, need)}/{need} {b.Unit}".Trim(), IsProgress: true);
                return new BadgeView(b, false, b.Hint ?? (b.Bonus ? "bonus" : ""));
            }).ToList();

        /// <summary>„7/23“ – kolik odznaků z eventu.</summary>
        public static (int Got, int Total) Count(BadgeState state) =>
            (Events.Badges.Count(b => state.Has(b.Id)), Events.Badges.Count);

        /// <summary>Přišel push o novém odznaku → při příštím otevření lístku hned zkontrolovat.</summary>
        public static void ForceCheck() => Preferences.Set(KeyForce, true);

        /// <summary>
        /// Nové odznaky od minula (pro okno „Nový odznak“). Prázdné, když event neběží nebo se nedávno kontrolovalo.
        /// Poprvé jen zapamatuje, co už mám (ať po instalaci nevyskočí všechno najednou).
        /// </summary>
        public static async Task<List<EventBadge>> CheckNewAsync()
        {
            if (AuthSession.Uid is not string uid || !Events.Running) return new();
            var force = Preferences.Get(KeyForce, false);
            var last = Preferences.Get(KeyCheckedAt, 0L);
            if (!force && DateTime.UtcNow.Ticks - last < TimeSpan.FromMinutes(10).Ticks) return new();
            try
            {
                var state = await LoadAsync(uid, fresh: true);
                Preferences.Set(KeyCheckedAt, DateTime.UtcNow.Ticks);
                Preferences.Set(KeyForce, false);
                var seenRaw = Preferences.Get(KeySeen, null as string);
                var seen = (seenRaw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var fresh = Events.Badges.Where(b => state.Has(b.Id) && !seen.Contains($"{state.Event}:{b.Id}")).ToList();
                foreach (var b in Events.Badges.Where(b => state.Has(b.Id))) seen.Add($"{state.Event}:{b.Id}");
                Preferences.Set(KeySeen, string.Join(",", seen));
                return seenRaw == null && fresh.Count > 3 ? new() : fresh;
            }
            catch
            {
                return new();
            }
        }
    }
}
