using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>
    /// Katalog hospod, barů, restaurací a pivních zahrádek z OpenStreetMap.
    /// Hlavně celá ČR v jednom souboru (název, typ, poloha, adresa, otvíračka, web…) – vyrábí ho jednou týdně GitHub Action
    /// (.github/workflows/hospody.yml) a appka si ho stáhne z vydání „katalog“ (obnova po 14 dnech).
    /// Podrobnosti (adresa, otvíračka, web…) se dotáhnou z Overpass API až při otevření hospody.
    /// Záloha, když GitHub nejde: čtverce mřížky (~14 × 14 km) přímo z Overpassu podle výřezu mapy.
    /// Mapa nestojí žádné čtení z Firestore. Data © přispěvatelé OpenStreetMap (ODbL).
    /// </summary>
    public static class PubCatalog
    {
        public const double LatStep = 0.125;
        public const double LonStep = 0.1875;

        private const string DirName = "hospody_ctverce_v2";
        private const string OldDirName = "hospody_ctverce_v1";      // 3.8: dvakrát větší čtverce
        private const string OldFileName = "hospody_cz64_v3.json";   // starý katalog celého JMK – už se nepoužívá
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

        /// <summary>Odkud se katalog stahuje (web si ho bere ze svého webu – z GitHubu by ho prohlížeč nepustil).</summary>
        public static string NationalUrl { get; set; } = "https://github.com/El-Pietro/Dezolati/releases/download/katalog/hospody-cz.csv.gz";
        private const string NationalFileName = "hospody_cz_v2.csv.gz";
        private const string OldNationalFileName = "hospody_cz.csv.gz";   // první verze jen s názvy
        private const string DetailsFileName = "hospody_detaily.json";
        private static readonly TimeSpan NationalMaxAge = TimeSpan.FromDays(14);
        private static List<PubInfo>? _national;
        private static readonly Dictionary<string, PubInfo> Details = new();   // dotažené podrobnosti (přebíjí lehké záznamy)
        private static Task? _nationalTask;
        private static DateTime _nationalFailedAt = DateTime.MinValue;

        private static readonly string[] Endpoints =
        {
            "https://overpass-api.de/api/interpreter",
            "https://overpass.private.coffee/api/interpreter",
            "https://overpass.kumi.systems/api/interpreter"
        };

        private static readonly HttpClient Http = CreateHttp();
        private static readonly SemaphoreSlim Lock = new(1, 1);          // stahování po jednom (slušnost k Overpassu)
        private static readonly object MemLock = new();
        private static readonly Dictionary<string, List<PubInfo>> Cells = new();
        private static bool _diskLoaded;
        private static List<PubInfo> _pubs = new();
        private static Dictionary<string, PubInfo> _byId = new();

        /// <summary>Přibyly hospody (stažený čtverec) – mapa se překreslí.</summary>
        public static event Action? Changed;

        private static string Dir => Path.Combine(FileSystem.AppDataDirectory, DirName);
        private static string CellPath(string key) => Path.Combine(Dir, key + ".json");
        private static string NationalPath => Path.Combine(FileSystem.AppDataDirectory, NationalFileName);
        private static string DetailsPath => Path.Combine(FileSystem.AppDataDirectory, DetailsFileName);

        /// <summary>Je v telefonu katalog celé ČR (pak se čtverce nestahují)?</summary>
        public static bool HasNational
        {
            get { lock (MemLock) return _national != null; }
        }

        /// <summary>Právě se stahuje katalog celé ČR.</summary>
        public static bool NationalLoading
        {
            get { lock (MemLock) return _nationalTask is { IsCompleted: false }; }
        }

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            // Overpass chce slušně představenou appku (vlastní User-Agent + Referer),
            // jinak vrací 406 Not Acceptable
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Dezolati/3.9 (+https://github.com/El-Pietro/Dezolati)");
            http.DefaultRequestHeaders.Referrer = new Uri("https://github.com/El-Pietro/Dezolati");
            return http;
        }

        // ---------------- čtverce ----------------

        public static string CellOf(double lat, double lon) =>
            $"{(int)Math.Floor(lat / LatStep)}_{(int)Math.Floor(lon / LonStep)}";

        /// <summary>Čtverce, které pokrývají obdélník (seřazené od středu).</summary>
        public static List<string> CellsIn(double south, double west, double north, double east)
        {
            var result = new List<(string Key, double Dist)>();
            var cy = (south + north) / 2; var cx = (west + east) / 2;
            for (var y = (int)Math.Floor(south / LatStep); y <= (int)Math.Floor(north / LatStep); y++)
                for (var x = (int)Math.Floor(west / LonStep); x <= (int)Math.Floor(east / LonStep); x++)
                {
                    var d = Math.Abs((y + 0.5) * LatStep - cy) + Math.Abs((x + 0.5) * LonStep - cx);
                    result.Add(($"{y}_{x}", d));
                }
            return result.OrderBy(r => r.Dist).Select(r => r.Key).ToList();
        }

        /// <summary>Čtverec, ve kterém bod leží, a k němu sousední, je-li bod blízko okraje (do ~1 km).</summary>
        public static List<string> CellsNear(double lat, double lon) =>
            CellsIn(lat - 0.01, lon - 0.015, lat + 0.01, lon + 0.015);

        public static bool HasCell(string key)
        {
            lock (MemLock) return _national != null || Cells.ContainsKey(key);
        }

        /// <summary>Je v telefonu aspoň něco?</summary>
        public static bool HasAny
        {
            get { lock (MemLock) return _national != null || Cells.Count > 0; }
        }

        /// <summary>
        /// Dotáhne chybějící čtverce (a zastaralé obnoví). Po jednom, bez signálu tiše nic.
        /// Vrací, kolik se jich stáhlo. Chyba stahování se vyhodí jen když nic nemáme.
        /// </summary>
        public static async Task<int> EnsureCellsAsync(IEnumerable<string> keys, CancellationToken ct = default)
        {
            await LoadDiskAsync();
            // katalog celé ČR se zrovna stahuje → počkat na něj, čtverce pak nejsou potřeba
            Task? national;
            lock (MemLock) national = _nationalTask;
            if (national is { IsCompleted: false })
                await national.WaitAsync(ct);
            if (HasNational) return 0;

            var downloaded = 0;
            Exception? last = null;
            foreach (var key in keys.Distinct())
            {
                ct.ThrowIfCancellationRequested();
                var path = CellPath(key);
                var fresh = File.Exists(path) && DateTime.Now - File.GetLastWriteTime(path) < MaxAge;
                if (fresh && HasCell(key)) continue;
                if (!Net.IsOnline) break;

                await Lock.WaitAsync(ct);
                try
                {
                    // mezitím ho mohl stáhnout někdo jiný
                    if (File.Exists(path) && DateTime.Now - File.GetLastWriteTime(path) < MaxAge && HasCell(key)) continue;
                    var pubs = await DownloadCellAsync(key, ct);
                    await SaveCellAsync(key, pubs);
                    lock (MemLock) { Cells[key] = pubs; Rebuild(); }
                    downloaded++;
                    Changed?.Invoke();   // ukázat hned, nečekat na ostatní čtverce
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    System.Diagnostics.Debug.WriteLine($"Čtverec {key}: {ex.Message}");
                }
                finally
                {
                    Lock.Release();
                }
            }
            if (last != null && downloaded == 0 && !HasAny) throw last;
            return downloaded;
        }

        /// <summary>Všechno, co je v telefonu (nic nestahuje). force se už nepoužívá.</summary>
        public static async Task<IReadOnlyList<PubInfo>> GetAsync(bool force = false)
        {
            await LoadDiskAsync();
            lock (MemLock) return _pubs;
        }

        /// <summary>Katalog v telefonu; null = zatím nic staženého.</summary>
        public static async Task<IReadOnlyList<PubInfo>?> GetCachedAsync()
        {
            await LoadDiskAsync();
            lock (MemLock) return Cells.Count == 0 && _national == null ? null : _pubs;
        }

        public static PubInfo? Find(string id)
        {
            lock (MemLock) return _byId.TryGetValue(id, out var p) ? p : null;
        }

        /// <summary>Nejbližší hospoda do maxMeters (null = nic v dosahu).</summary>
        public static PubInfo? Nearest(double lat, double lon, double maxMeters)
        {
            List<PubInfo> pubs;
            lock (MemLock) pubs = _pubs;
            PubInfo? best = null;
            var bestDist = maxMeters;
            foreach (var p in pubs)
            {
                if (Math.Abs(p.Lat - lat) > 0.01 || Math.Abs(p.Lon - lon) > 0.015) continue;   // rychlé síto
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

        private static void Rebuild()
        {
            var all = (_national ?? new List<PubInfo>()).Concat(Cells.Values.SelectMany(c => c));
            var byId = new Dictionary<string, PubInfo>();
            foreach (var p in all)
            {
                if (byId.TryGetValue(p.Id, out var have) && !(have.Light && !p.Light)) continue;   // jen jednou, plný má přednost
                byId[p.Id] = p;
            }
            foreach (var d in Details.Values)
                if (byId.ContainsKey(d.Id)) byId[d.Id] = d;
            _byId = byId;
            _pubs = byId.Values.ToList();
        }

        /// <summary>Jednou za spuštění: čtverce z telefonu do paměti (a smazat starý krajský katalog).</summary>
        private static async Task LoadDiskAsync()
        {
            if (_diskLoaded)
            {
                StartNationalRefresh();
                return;
            }
            await Lock.WaitAsync();
            try
            {
                if (_diskLoaded) return;
                try { File.Delete(Path.Combine(FileSystem.AppDataDirectory, OldFileName)); } catch { }
                try { Directory.Delete(Path.Combine(FileSystem.AppDataDirectory, OldDirName), recursive: true); } catch { }
                try { File.Delete(Path.Combine(FileSystem.AppDataDirectory, OldNationalFileName)); } catch { }
                Directory.CreateDirectory(Dir);
                var loaded = new Dictionary<string, List<PubInfo>>();
                foreach (var file in Directory.GetFiles(Dir, "*.json"))
                {
                    try
                    {
                        await using var stream = File.OpenRead(file);
                        var pubs = await JsonSerializer.DeserializeAsync<List<PubInfo>>(stream);
                        if (pubs != null) loaded[Path.GetFileNameWithoutExtension(file)] = pubs;
                    }
                    catch { /* poškozený čtverec → stáhne se znovu */ }
                }
                List<PubInfo>? national = null;
                try
                {
                    if (File.Exists(NationalPath))
                        national = ParseGz(await File.ReadAllBytesAsync(NationalPath));
                }
                catch { /* poškozený → stáhne se znovu */ }
                List<PubInfo>? details = null;
                try
                {
                    if (File.Exists(DetailsPath))
                    {
                        await using var stream = File.OpenRead(DetailsPath);
                        details = await JsonSerializer.DeserializeAsync<List<PubInfo>>(stream);
                    }
                }
                catch { }
                lock (MemLock)
                {
                    foreach (var kv in loaded) Cells[kv.Key] = kv.Value;
                    if (national is { Count: > 0 }) _national = national;
                    if (details != null) foreach (var d in details) Details[d.Id] = d;
                    Rebuild();
                }
                _diskLoaded = true;
            }
            finally
            {
                Lock.Release();
            }
            StartNationalRefresh();
        }

        // ---------------- katalog celé ČR (GitHub) ----------------

        /// <summary>Chybí / je starší než 14 dní → stáhnout na pozadí (starý se mezitím používá).</summary>
        private static void StartNationalRefresh()
        {
            try
            {
                var fresh = File.Exists(NationalPath) && DateTime.Now - File.GetLastWriteTime(NationalPath) < NationalMaxAge;
                if (fresh && HasNational) return;
            }
            catch { }
            if (!Net.IsOnline || DateTime.UtcNow - _nationalFailedAt < TimeSpan.FromMinutes(10)) return;
            lock (MemLock)
            {
                if (_nationalTask is { IsCompleted: false }) return;
                _nationalTask = Task.Run(DownloadNationalAsync);
            }
        }

        private static async Task DownloadNationalAsync()
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(NationalUrl);
                var pubs = ParseGz(bytes);
                if (pubs.Count < 1000) throw new InvalidOperationException($"Katalog má jen {pubs.Count} podniků.");

                var tmp = NationalPath + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, NationalPath, overwrite: true);

                lock (MemLock)
                {
                    _national = pubs;
                    Cells.Clear();          // čtverce už nejsou potřeba
                    Rebuild();
                }
                try { Directory.Delete(Dir, recursive: true); } catch { }
            }
            catch (Exception ex)
            {
                _nationalFailedAt = DateTime.UtcNow;
                System.Diagnostics.Debug.WriteLine($"Katalog ČR: {ex.Message}");
            }
            Changed?.Invoke();   // i při chybě – mapa přestane psát „stahuji“ a zkusí čtverce
        }

        private static List<PubInfo> ParseGz(byte[] gz)
        {
            using var ms = new MemoryStream(gz);
            using var zip = new GZipStream(ms, CompressionMode.Decompress);
            using var reader = new StreamReader(zip, Encoding.UTF8);
            var csv = reader.ReadToEnd();
            if (!csv.StartsWith("@type", StringComparison.Ordinal)) throw new InvalidDataException("Katalog není CSV z Overpassu.");
            // katalog s adresou, otvíračkou, webem… (stejné sloupce jako Columns) → podrobnosti se pak nestahují vůbec
            var header = csv.AsSpan(0, Math.Max(0, csv.IndexOf('\n')));
            return header.Count('\t') >= 16 ? Parse(csv) : ParseLight(csv);
        }

        private static async Task SaveCellAsync(string key, List<PubInfo> pubs)
        {
            Directory.CreateDirectory(Dir);
            var path = CellPath(key);
            var tmp = path + ".tmp";
            await using (var stream = File.Create(tmp))
                await JsonSerializer.SerializeAsync(stream, pubs);
            File.Move(tmp, path, overwrite: true);
        }

        // sloupce CSV výstupu (pořadí musí sedět s Parse)
        private const string Columns =
            "::type,::id,::lat,::lon,name,amenity,\"addr:street\",\"addr:place\",\"addr:housenumber\"," +
            "\"addr:conscriptionnumber\",\"addr:city\",brewery,opening_hours," +
            "website,\"contact:website\",phone,\"contact:phone\"";

        // Do mapy stačí poloha, typ a název; adresa, otvíračka, web… se dotáhnou až při otevření hospody.
        // Je to několikrát menší odpověď, takže projdou i husté oblasti (Praha).
        private const string LightColumns = "::type,::id,::lat,::lon,name,amenity";

        private static async Task<List<PubInfo>> DownloadCellAsync(string key, CancellationToken ct)
        {
            var parts = key.Split('_');
            var y = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var x = int.Parse(parts[1], CultureInfo.InvariantCulture);
            return await DownloadBoxAsync(y * LatStep, x * LonStep, (y + 1) * LatStep, (x + 1) * LonStep, 0, ct);
        }

        /// <summary>Obdélník; když na něj server nestačí (moc podniků), rozdělí se na 4 menší (max. 2×).</summary>
        private static async Task<List<PubInfo>> DownloadBoxAsync(double s, double w, double n, double e, int depth, CancellationToken ct)
        {
            try
            {
                return await QueryAsync(LightColumns, BoxFilter(s, w, n, e), light: true, ct);
            }
            catch (TooBigException) when (depth < 2)
            {
                var midLat = (s + n) / 2;
                var midLon = (w + e) / 2;
                var result = new List<PubInfo>();
                result.AddRange(await DownloadBoxAsync(s, w, midLat, midLon, depth + 1, ct));
                result.AddRange(await DownloadBoxAsync(s, midLon, midLat, e, depth + 1, ct));
                result.AddRange(await DownloadBoxAsync(midLat, w, n, midLon, depth + 1, ct));
                result.AddRange(await DownloadBoxAsync(midLat, midLon, n, e, depth + 1, ct));
                return result;
            }
        }

        private static string BoxFilter(double s, double w, double n, double e)
        {
            string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
            return $"nwr[\"amenity\"~\"^(pub|bar|biergarten|restaurant)$\"]({F(s)},{F(w)},{F(n)},{F(e)});";
        }

        /// <summary>Server odpověděl, že je toho na jeden dotaz moc (timeout / nedostatek paměti).</summary>
        private sealed class TooBigException : Exception
        {
            public TooBigException(string message) : base(message) { }
        }

        private static async Task<List<PubInfo>> QueryAsync(string columns, string filter, bool light, CancellationToken ct)
        {
            // CSV místo JSON = několikanásobně menší přenos; out center = u budov střed
            var query = $"[out:csv({columns})][timeout:80];" + filter + "out center;";

            var errors = new List<string>();
            var tooBig = 0;
            foreach (var url in Endpoints)
            {
                var host = new Uri(url).Host;
                try
                {
                    using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) });
                    using var resp = await Http.PostAsync(url, content, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        if ((int)resp.StatusCode is 504 or 503) tooBig++;
                        errors.Add($"{host}: HTTP {(int)resp.StatusCode} {Snippet(body)}");
                        continue;
                    }
                    // prázdný čtverec (les, pole) je v pořádku – jen musí přijít hlavička CSV
                    if (!body.StartsWith("@type", StringComparison.Ordinal))
                    {
                        if (body.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                            || body.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                            || body.Contains("runtime error", StringComparison.OrdinalIgnoreCase))
                            tooBig++;
                        errors.Add($"{host}: nečekaná odpověď – {Snippet(body)}");
                        continue;
                    }
                    return light ? ParseLight(body) : Parse(body);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (TaskCanceledException ex)
                {
                    // vypršel náš limit → odpověď je nejspíš moc velká / server nestíhá
                    tooBig++;
                    errors.Add($"{host}: vypršel čas ({ex.Message})");
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException?.Message;
                    errors.Add($"{host}: {ex.GetType().Name}: {ex.Message}" + (inner != null ? $" ({inner})" : ""));
                }
            }

            foreach (var e in errors) System.Diagnostics.Debug.WriteLine("Overpass: " + e);
            var message = "Seznam hospod se nepodařilo stáhnout.\n" + string.Join("\n", errors);
            if (tooBig > 0) throw new TooBigException(message);
            throw new InvalidOperationException(message);
        }

        // ---------------- podrobnosti jedné hospody (až při otevření) ----------------

        /// <summary>
        /// Hospoda stažená jen v základu → dotáhnout adresu, otvíračku, web, telefon, pivovar (1 malý dotaz)
        /// a uložit do čtverce, ať se to příště nestahuje. Bez signálu vrátí, co je.
        /// </summary>
        public static async Task<PubInfo> EnsureDetailsAsync(PubInfo pub, CancellationToken ct = default)
        {
            if (!pub.Light || pub.Id.Length < 2 || !Net.IsOnline) return pub;
            var type = pub.Id[0] switch { 'w' => "way", 'r' => "relation", _ => "node" };
            if (!long.TryParse(pub.Id[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var osmId)) return pub;

            List<PubInfo> found;
            try { found = await QueryAsync(Columns, $"{type}({osmId});", light: false, ct); }
            catch { return pub; }

            // v OSM už není / nemá jméno → aspoň se to příště nezkouší znovu
            var detailed = found.FirstOrDefault() ?? pub with { Light = false };
            List<PubInfo> copy;
            lock (MemLock)
            {
                Details[pub.Id] = detailed;
                Rebuild();
                copy = Details.Values.ToList();
            }
            try
            {
                var tmp = DetailsPath + ".tmp";
                await using (var stream = File.Create(tmp))
                    await JsonSerializer.SerializeAsync(stream, copy);
                File.Move(tmp, DetailsPath, overwrite: true);
            }
            catch { }
            return detailed;
        }

        /// <summary>Jen poloha, typ a název (podrobnosti později).</summary>
        internal static List<PubInfo> ParseLight(string csv)
        {
            var result = new List<PubInfo>();
            var lines = csv.Split('\n');
            for (var i = 1; i < lines.Length; i++) // 0 = hlavička
            {
                var c = lines[i].TrimEnd('\r').Split('\t');
                if (c.Length < 6) continue;
                var name = c[4].Trim();
                if (name.Length == 0) continue;
                if (!double.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                    !double.TryParse(c[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                    continue;
                var type = c[0].Trim();
                var id = (type.Length > 0 ? type[0] : 'n') + c[1].Trim();
                result.Add(new PubInfo(id, name, c[5].Trim(), Math.Round(lat, 6), Math.Round(lon, 6),
                    null, null, null, null, null, Light: true));
            }
            return result;
        }

        private static string Snippet(string body)
        {
            var oneLine = string.Join(' ', body.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            // z HTML chybové stránky vytáhnout jen text
            oneLine = System.Text.RegularExpressions.Regex.Replace(oneLine, "<[^>]+>", " ");
            oneLine = System.Text.RegularExpressions.Regex.Replace(oneLine, @"\s+", " ").Trim();
            return oneLine.Length > 160 ? oneLine[..160] + "…" : oneLine;
        }

        // OSM umí víc hodnot oddělených středníkem → stačí první
        private static string? FirstOf(params string[] values)
        {
            foreach (var v in values)
            {
                var first = v.Split(';')[0].Trim();
                if (first.Length > 0) return first;
            }
            return null;
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
                    Col(12).Length > 0 ? Col(12) : null,
                    FirstOf(Col(13), Col(14)),
                    FirstOf(Col(15), Col(16))));
            }
            return result;
        }
    }
}
