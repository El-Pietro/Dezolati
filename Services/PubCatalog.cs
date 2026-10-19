using System.Globalization;
using System.Text.Json;
using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>
    /// Katalog hospod, barů a pivních zahrádek z OpenStreetMap (Overpass API).
    /// Stáhne se jednou do telefonu a drží se 30 dní → mapa nestojí žádné čtení z Firestore.
    /// Data © přispěvatelé OpenStreetMap (ODbL).
    /// </summary>
    public static class PubCatalog
    {
        /// <summary>Oblast podle ISO 3166-2 (CZ-64 = Jihomoravský kraj).</summary>
        public const string Area = "CZ-64";

        private const string FileName = "hospody_cz64.json";
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

        private static readonly string[] Endpoints =
        {
            "https://overpass-api.de/api/interpreter",
            "https://overpass.private.coffee/api/interpreter",
            "https://overpass.kumi.systems/api/interpreter"
        };

        private static readonly HttpClient Http = CreateHttp();
        private static readonly SemaphoreSlim Lock = new(1, 1);
        private static List<PubInfo>? _pubs;
        private static Dictionary<string, PubInfo>? _byId;

        private static string FilePath => Path.Combine(FileSystem.AppDataDirectory, FileName);

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            // Overpass chce slušně představenou appku (vlastní User-Agent + Referer),
            // jinak vrací 406 Not Acceptable
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Dezolati/2.2 (+https://github.com/El-Pietro/Dezolati)");
            http.DefaultRequestHeaders.Referrer = new Uri("https://github.com/El-Pietro/Dezolati");
            return http;
        }

        /// <summary>Kdy se katalog naposledy stáhl (null = ještě nikdy).</summary>
        public static DateTime? DownloadedAt =>
            File.Exists(FilePath) ? File.GetLastWriteTime(FilePath) : null;

        /// <summary>
        /// Vrátí katalog. Poprvé stáhne (chvíli to trvá), pak bere z telefonu.
        /// Starší než 30 dní se potichu obnoví na pozadí. force = stáhnout hned znovu.
        /// </summary>
        public static async Task<IReadOnlyList<PubInfo>> GetAsync(bool force = false)
        {
            await Lock.WaitAsync();
            try
            {
                if (!force && _pubs != null) return _pubs;

                if (!force && File.Exists(FilePath))
                {
                    var cached = await LoadFileAsync();
                    if (cached != null)
                    {
                        Set(cached);
                        if (DateTime.Now - File.GetLastWriteTime(FilePath) > MaxAge)
                            _ = Task.Run(RefreshInBackgroundAsync);
                        return cached;
                    }
                }

                var fresh = await DownloadAsync();
                await SaveFileAsync(fresh);
                Set(fresh);
                return fresh;
            }
            finally
            {
                Lock.Release();
            }
        }

        public static PubInfo? Find(string id) =>
            _byId != null && _byId.TryGetValue(id, out var p) ? p : null;

        /// <summary>Nejbližší hospoda do maxMeters (null = nic v dosahu).</summary>
        public static PubInfo? Nearest(double lat, double lon, double maxMeters)
        {
            if (_pubs == null) return null;
            PubInfo? best = null;
            var bestDist = maxMeters;
            foreach (var p in _pubs)
            {
                var d = DistanceMeters(lat, lon, p.Lat, p.Lon);
                if (d <= bestDist)
                {
                    bestDist = d;
                    best = p;
                }
            }
            return best;
        }

        public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000;
            double Rad(double deg) => deg * Math.PI / 180;
            var dLat = Rad(lat2 - lat1);
            var dLon = Rad(lon2 - lon1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        // ---------------- interní ----------------

        private static void Set(List<PubInfo> pubs)
        {
            _pubs = pubs;
            _byId = pubs.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        }

        private static async Task RefreshInBackgroundAsync()
        {
            try
            {
                var fresh = await DownloadAsync();
                await SaveFileAsync(fresh);
                await Lock.WaitAsync();
                try { Set(fresh); } finally { Lock.Release(); }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Obnova katalogu hospod selhala: {ex.Message}");
            }
        }

        private static async Task<List<PubInfo>?> LoadFileAsync()
        {
            try
            {
                await using var stream = File.OpenRead(FilePath);
                return await JsonSerializer.DeserializeAsync<List<PubInfo>>(stream);
            }
            catch
            {
                return null; // poškozený soubor → stáhne se znovu
            }
        }

        private static async Task SaveFileAsync(List<PubInfo> pubs)
        {
            var tmp = FilePath + ".tmp";
            await using (var stream = File.Create(tmp))
                await JsonSerializer.SerializeAsync(stream, pubs);
            File.Move(tmp, FilePath, overwrite: true);
        }

        // sloupce CSV výstupu (pořadí musí sedět s Parse)
        private const string Columns =
            "::type,::id,::lat,::lon,name,amenity,\"addr:street\",\"addr:place\",\"addr:housenumber\"," +
            "\"addr:conscriptionnumber\",\"addr:city\",brewery,opening_hours";

        private static async Task<List<PubInfo>> DownloadAsync()
        {
            // CSV místo JSON = několikanásobně menší přenos; out center = u budov střed
            var query =
                $"[out:csv({Columns})][timeout:90];" +
                $"area[\"ISO3166-2\"=\"{Area}\"]->.k;" +
                "nwr[\"amenity\"~\"^(pub|bar|biergarten)$\"](area.k);" +
                "out center;";

            var errors = new List<string>();
            foreach (var url in Endpoints)
            {
                var host = new Uri(url).Host;
                try
                {
                    using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) });
                    using var resp = await Http.PostAsync(url, content);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        errors.Add($"{host}: HTTP {(int)resp.StatusCode} {Snippet(body)}");
                        continue;
                    }
                    var pubs = Parse(body);
                    if (pubs.Count == 0)
                    {
                        // Overpass umí vrátit 200 s chybovou hláškou místo dat
                        errors.Add($"{host}: žádná data – {Snippet(body)}");
                        continue;
                    }
                    return pubs;
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException?.Message;
                    errors.Add($"{host}: {ex.GetType().Name}: {ex.Message}" + (inner != null ? $" ({inner})" : ""));
                }
            }

            foreach (var e in errors) System.Diagnostics.Debug.WriteLine("Overpass: " + e);
            throw new InvalidOperationException(
                "Seznam hospod se nepodařilo stáhnout.\n" + string.Join("\n", errors));
        }

        private static string Snippet(string body)
        {
            var oneLine = string.Join(' ', body.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            // z HTML chybové stránky vytáhnout jen text
            oneLine = System.Text.RegularExpressions.Regex.Replace(oneLine, "<[^>]+>", " ");
            oneLine = System.Text.RegularExpressions.Regex.Replace(oneLine, @"\s+", " ").Trim();
            return oneLine.Length > 160 ? oneLine[..160] + "…" : oneLine;
        }

        internal static List<PubInfo> Parse(string csv)
        {
            var result = new List<PubInfo>();
            var lines = csv.Split('\n');
            for (var i = 1; i < lines.Length; i++) // 0 = hlavička
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                var c = line.Split('\t');
                string Col(int idx) => idx < c.Length ? c[idx].Trim() : "";

                var name = Col(4);
                if (name.Length == 0) continue; // bezejmenné stánky vynechat
                if (!double.TryParse(Col(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                    !double.TryParse(Col(3), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                    continue;

                var type = Col(0);
                var id = (type.Length > 0 ? type[0] : 'n') + Col(1);

                var street = Col(6).Length > 0 ? Col(6) : Col(7);
                var number = Col(8).Length > 0 ? Col(8) : Col(9);
                var city = Col(10);
                var parts = new List<string>();
                var streetPart = $"{street} {number}".Trim();
                if (streetPart.Length > 0) parts.Add(streetPart);
                if (city.Length > 0 && city != street) parts.Add(city);

                result.Add(new PubInfo(
                    id, name, Col(5),
                    Math.Round(lat, 6), Math.Round(lon, 6),
                    parts.Count > 0 ? string.Join(", ", parts) : null,
                    Col(11).Length > 0 ? Col(11) : null,
                    Col(12).Length > 0 ? Col(12) : null));
            }
            return result;
        }
    }
}
