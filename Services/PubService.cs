using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>
    /// Profily hospod ve Firestore + „překryv“ nad katalogem z OpenStreetMap:
    /// opravené názvy, zavřené podniky a podniky přidané uživateli.
    /// Čtení: překryv = počet takových podniků (jednou za spuštění), otevření hospody 1, historie ≤ 10.
    /// </summary>
    public static class PubService
    {
        public const int MaxLength = 300;
        public const int MaxNameLength = 80;
        public const int HistoryLimit = 10;
        public const string CustomPrefix = "c_";

        public static readonly string[] Kinds = { "pub", "bar", "restaurant", "biergarten" };

        private sealed record Overlay(string? DisplayName, bool Closed, PubInfo? Custom, string? CreatedBy);

        private static Dictionary<string, Overlay>? _overlay;

        // ---------------- překryv ----------------

        /// <summary>
        /// Načte podniky, které se liší od OpenStreetMap (Flagged = true; starší přejmenované mají jen Renamed).
        /// Bez signálu tiše nic.
        /// </summary>
        public static async Task LoadOverlayAsync(bool force = false)
        {
            if (_overlay != null && !force) return;
            try
            {
                var flagged = FirestoreRest.QueryAsync("Pubs", new[] { Filter.Eq("Flagged", true) });
                var renamed = FirestoreRest.QueryAsync("Pubs", new[] { Filter.Eq("Renamed", true) });
                await Task.WhenAll(flagged, renamed);

                var map = new Dictionary<string, Overlay>();
                foreach (var d in flagged.Result.Concat(renamed.Result))
                {
                    if (map.ContainsKey(d.Id)) continue;
                    PubInfo? custom = null;
                    if (d.Fields["Custom"]?["booleanValue"]?.GetValue<bool>() == true)
                    {
                        custom = new PubInfo(d.Id, d.Str("Name") ?? "?", d.Str("Kind") ?? "pub",
                            Dbl(d, "Lat"), Dbl(d, "Lon"), null, null, null);
                    }
                    map[d.Id] = new Overlay(
                        NullIfEmpty(d.Str("DisplayName")),
                        d.Fields["Closed"]?["booleanValue"]?.GetValue<bool>() == true,
                        custom,
                        d.Str("CreatedBy"));
                }
                _overlay = map;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Překryv hospod: {ex.Message}");
                _overlay ??= new Dictionary<string, Overlay>();
            }
        }

        private static double Dbl(FsDoc d, string field)
        {
            var v = d.Fields[field];
            if (v?["doubleValue"] is { } dv) return dv.GetValue<double>();
            return FsDoc.ValueToLong(v);
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        /// <summary>Název, jak ho opravili uživatelé (jinak z OpenStreetMap / jak ho zadal autor).</summary>
        public static string NameOf(PubInfo pub) =>
            _overlay != null && _overlay.TryGetValue(pub.Id, out var o) && o.DisplayName != null ? o.DisplayName : pub.Name;

        public static bool IsClosed(PubInfo pub) =>
            _overlay != null && _overlay.TryGetValue(pub.Id, out var o) && o.Closed;

        public static bool IsCustom(PubInfo pub) => pub.Id.StartsWith(CustomPrefix);

        public static bool IsMine(PubInfo pub) =>
            _overlay != null && _overlay.TryGetValue(pub.Id, out var o) && o.CreatedBy == AuthSession.Uid;

        /// <summary>Katalog z OSM + podniky přidané uživateli.</summary>
        public static IEnumerable<PubInfo> AllPubs(IReadOnlyList<PubInfo> catalog) =>
            _overlay == null
                ? catalog
                : catalog.Concat(_overlay.Values.Where(o => o.Custom != null).Select(o => o.Custom!));

        public static PubInfo? Find(string id) =>
            id.StartsWith(CustomPrefix)
                ? (_overlay != null && _overlay.TryGetValue(id, out var o) ? o.Custom : null)
                : PubCatalog.Find(id);

        /// <summary>Nejbližší otevřený podnik do maxMeters (OSM i přidané).</summary>
        public static PubInfo? Nearest(double lat, double lon, double maxMeters)
        {
            PubInfo? best = null;
            var bestDist = maxMeters;
            var candidates = new List<PubInfo>();
            if (PubCatalog.Nearest(lat, lon, maxMeters) is { } osm) candidates.Add(osm);
            if (_overlay != null) candidates.AddRange(_overlay.Values.Where(o => o.Custom != null).Select(o => o.Custom!));
            foreach (var p in candidates)
            {
                if (IsClosed(p)) continue;
                var d = PubCatalog.DistanceMeters(lat, lon, p.Lat, p.Lon);
                if (d <= bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        // ---------------- profil ----------------

        public static async Task<PubProfile?> GetProfileAsync(string pubId)
        {
            var d = await FirestoreRest.GetAsync($"Pubs/{pubId}");
            if (d == null) return null;
            return new PubProfile
            {
                DisplayName = d.Str("DisplayName"),
                Taps = d.Str("Taps"),
                Prices = d.Str("Prices"),
                Hours = d.Str("Hours"),
                Note = d.Str("Note"),
                Closed = d.Fields["Closed"]?["booleanValue"]?.GetValue<bool>() == true,
                CreatedByNick = d.Str("CreatedByNick"),
                UpdatedAt = d.Time("UpdatedAt")?.ToLocalTime(),
                UpdatedByNick = d.Str("UpdatedByNick")
            };
        }

        private static string Clean(string? s, int max = MaxLength)
        {
            var t = (s ?? "").Trim();
            return t.Length > max ? t[..max] : t;
        }

        /// <summary>
        /// Uloží profil a zároveň záznam do historie – jednou atomickou dávkou
        /// (pravidla ve Firestore bez záznamu v historii úpravu nepustí).
        /// Ukládá se s maskou, takže u přidaných podniků zůstanou poloha, autor atd.
        /// </summary>
        public static async Task SaveAsync(PubInfo pub, PubProfile profile, string? action = null)
        {
            var uid = AuthSession.Uid ?? throw new InvalidOperationException("Nejsi přihlášený.");
            await UserSession.EnsureReadyAsync();
            var nick = UserSession.Profile?.Nickname ?? "?";
            var now = DateTime.UtcNow;
            var editId = Guid.NewGuid().ToString("N");

            // název: prázdný nebo stejný jako původní = žádná oprava
            var name = Clean(profile.DisplayName, MaxNameLength);
            var renamed = name.Length > 0 && name != pub.Name;
            var custom = IsCustom(pub);

            var fields = new Dictionary<string, object?>
            {
                ["DisplayName"] = renamed ? name : "",
                ["Taps"] = Clean(profile.Taps),
                ["Prices"] = Clean(profile.Prices),
                ["Hours"] = Clean(profile.Hours),
                ["Note"] = Clean(profile.Note),
                ["Closed"] = profile.Closed
            };

            var edit = new Dictionary<string, object?>(fields)
            {
                ["PubId"] = pub.Id,
                ["By"] = uid,
                ["ByNick"] = nick,
                ["At"] = now
            };
            if (action != null) edit["Action"] = action;

            var doc = new Dictionary<string, object?>(fields)
            {
                ["PubId"] = pub.Id,
                ["Name"] = pub.Name,
                ["Renamed"] = renamed,
                ["Flagged"] = renamed || profile.Closed || custom,
                ["UpdatedAt"] = now,
                ["UpdatedBy"] = uid,
                ["UpdatedByNick"] = nick,
                ["EditId"] = editId
            };

            await FirestoreRest.CommitAsync(new[]
            {
                FirestoreRest.SetWrite($"PubEdits/{editId}", edit, mustNotExist: true),
                FirestoreRest.SetWrite($"Pubs/{pub.Id}", doc, mask: doc.Keys)
            });

            // hned i v mapě
            _overlay ??= new Dictionary<string, Overlay>();
            _overlay.TryGetValue(pub.Id, out var old);
            if (renamed || profile.Closed || custom)
                _overlay[pub.Id] = new Overlay(renamed ? name : null, profile.Closed, old?.Custom, old?.CreatedBy);
            else
                _overlay.Remove(pub.Id);
        }

        /// <summary>Přidá podnik, který v OpenStreetMap chybí.</summary>
        public static async Task<PubInfo> CreateAsync(string name, string kind, double lat, double lon)
        {
            var uid = AuthSession.Uid ?? throw new InvalidOperationException("Nejsi přihlášený.");
            await UserSession.EnsureReadyAsync();
            var nick = UserSession.Profile?.Nickname ?? "?";
            var now = DateTime.UtcNow;
            var editId = Guid.NewGuid().ToString("N");
            var id = CustomPrefix + Guid.NewGuid().ToString("N")[..16];

            name = Clean(name, MaxNameLength);
            if (name.Length == 0) throw new ArgumentException("Podnik musí mít název.");
            if (!Kinds.Contains(kind)) kind = "pub";
            lat = Math.Round(lat, 6);
            lon = Math.Round(lon, 6);

            var doc = new Dictionary<string, object?>
            {
                ["PubId"] = id,
                ["Name"] = name,
                ["Kind"] = kind,
                ["Lat"] = lat,
                ["Lon"] = lon,
                ["Custom"] = true,
                ["Flagged"] = true,
                ["Renamed"] = false,
                ["Closed"] = false,
                ["DisplayName"] = "",
                ["Taps"] = "",
                ["Prices"] = "",
                ["Hours"] = "",
                ["Note"] = "",
                ["CreatedBy"] = uid,
                ["CreatedByNick"] = nick,
                ["UpdatedAt"] = now,
                ["UpdatedBy"] = uid,
                ["UpdatedByNick"] = nick,
                ["EditId"] = editId
            };
            var edit = new Dictionary<string, object?>
            {
                ["PubId"] = id,
                ["By"] = uid,
                ["ByNick"] = nick,
                ["At"] = now,
                ["Action"] = "created",
                ["DisplayName"] = name
            };

            await FirestoreRest.CommitAsync(new[]
            {
                FirestoreRest.SetWrite($"PubEdits/{editId}", edit, mustNotExist: true),
                FirestoreRest.SetWrite($"Pubs/{id}", doc, mustNotExist: true)
            });

            var pub = new PubInfo(id, name, kind, lat, lon, null, null, null);
            _overlay ??= new Dictionary<string, Overlay>();
            _overlay[id] = new Overlay(null, false, pub, uid);
            return pub;
        }

        /// <summary>Smaže podnik, který přidal sám uživatel (cizí jde jen označit jako zavřený).</summary>
        public static async Task DeleteAsync(PubInfo pub)
        {
            var uid = AuthSession.Uid ?? throw new InvalidOperationException("Nejsi přihlášený.");
            if (!IsCustom(pub)) throw new InvalidOperationException("Podnik z OpenStreetMap jde jen označit jako zavřený.");
            var nick = UserSession.Profile?.Nickname ?? "?";

            await FirestoreRest.CommitAsync(new[]
            {
                FirestoreRest.SetWrite($"PubEdits/{Guid.NewGuid():N}", new Dictionary<string, object?>
                {
                    ["PubId"] = pub.Id,
                    ["By"] = uid,
                    ["ByNick"] = nick,
                    ["At"] = DateTime.UtcNow,
                    ["Action"] = "deleted",
                    ["DisplayName"] = NameOf(pub)
                }, mustNotExist: true),
                FirestoreRest.DeleteWrite($"Pubs/{pub.Id}")
            });

            _overlay?.Remove(pub.Id);
        }

        // ---------------- historie ----------------

        /// <summary>Posledních pár úprav (potřebuje index PubEdits: PubId↑ At↓).</summary>
        public static async Task<List<PubEdit>> GetHistoryAsync(string pubId, int limit = HistoryLimit)
        {
            var docs = await FirestoreRest.QueryAsync("PubEdits",
                new[] { Filter.Eq("PubId", pubId) }, orderBy: "At", descending: true, limit: limit);
            return docs.Select(d => new PubEdit
            {
                At = d.Time("At")?.ToLocalTime(),
                ByNick = d.Str("ByNick"),
                Action = d.Str("Action"),
                DisplayName = d.Str("DisplayName"),
                Taps = d.Str("Taps"),
                Prices = d.Str("Prices"),
                Hours = d.Str("Hours"),
                Note = d.Str("Note"),
                Closed = d.Fields["Closed"]?["booleanValue"]?.GetValue<bool>() == true
            }).ToList();
        }
    }
}
