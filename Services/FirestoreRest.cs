using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Dezolati.Services
{
    /// <summary>Podmínka do dotazu.</summary>
    public record Filter(string Field, string Op, object Value)
    {
        public static Filter Eq(string field, object value) => new(field, "EQUAL", value);
        public static Filter Gte(string field, object value) => new(field, "GREATER_THAN_OR_EQUAL", value);
        public static Filter Lt(string field, object value) => new(field, "LESS_THAN", value);
        public static Filter ArrayContains(string field, object value) => new(field, "ARRAY_CONTAINS", value);
        /// <summary>Pole je jedna z hodnot (max. 30 hodnot v jednom dotazu).</summary>
        public static Filter In(string field, IEnumerable<string> values) => new(field, "IN", values.ToList());
    }

    /// <summary>
    /// Tenký klient nad Firestore REST API. Autorizuje se ID tokenem přihlášeného uživatele,
    /// takže platí security rules a v appce není žádný admin klíč.
    /// </summary>
    public static class FirestoreRest
    {
        public const string ProjectId = "dezolati";
        private static readonly string DbPath = $"projects/{ProjectId}/databases/(default)/documents";
        private static readonly string BaseUrl = $"https://firestore.googleapis.com/v1/{DbPath}";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public static string FullName(string path) => $"{DbPath}/{path}";

        // =============== čtení ===============

        public static async Task<FsDoc?> GetAsync(string path)
        {
            var node = await SendAsync(HttpMethod.Get, $"{BaseUrl}/{path}", null, allowNotFound: true);
            return node == null ? null : FsDoc.From(node);
        }

        /// <summary>Dotaz s rovnostmi spojenými přes AND (zkratka).</summary>
        public static Task<List<FsDoc>> QueryAsync(string collection, int limit, params (string Field, object Value)[] equals) =>
            QueryAsync(collection, equals.Select(e => Filter.Eq(e.Field, e.Value)), limit: limit);

        /// <summary>
        /// Obecný dotaz. Pozor: rovnost + rozsah/řazení na jiném poli potřebuje composite index.
        /// Účtuje se 1 čtení za každý vrácený dokument (minimálně 1).
        /// </summary>
        public static async Task<List<FsDoc>> QueryAsync(string collection, IEnumerable<Filter> filters,
            string? orderBy = null, bool descending = false, int limit = 0)
        {
            var query = BuildQuery(collection, filters);
            if (orderBy != null)
            {
                query["orderBy"] = new JsonArray(new JsonObject
                {
                    ["field"] = new JsonObject { ["fieldPath"] = orderBy },
                    ["direction"] = descending ? "DESCENDING" : "ASCENDING"
                });
            }
            if (limit > 0) query["limit"] = limit;

            var node = await SendAsync(HttpMethod.Post, $"{BaseUrl}:runQuery", new JsonObject { ["structuredQuery"] = query });

            var result = new List<FsDoc>();
            if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    var doc = item?["document"];
                    if (doc != null) result.Add(FsDoc.From(doc));
                }
            }
            return result;
        }

        /// <summary>
        /// Agregace (sum/count) na serveru – účtuje se 1 čtení za každých až 1000 záznamů v indexu,
        /// takže součty přes celou historii stojí prakticky nic.
        /// </summary>
        /// <param name="sums">alias → pole, které se sčítá</param>
        /// <param name="countAlias">alias pro počet dokumentů (null = nepočítat)</param>
        public static async Task<Dictionary<string, long>> AggregateAsync(string collection, IEnumerable<Filter> filters,
            IDictionary<string, string> sums, string? countAlias = null)
        {
            var aggregations = new JsonArray();
            foreach (var (alias, field) in sums)
            {
                aggregations.Add(new JsonObject
                {
                    ["alias"] = alias,
                    ["sum"] = new JsonObject { ["field"] = new JsonObject { ["fieldPath"] = field } }
                });
            }
            if (countAlias != null)
                aggregations.Add(new JsonObject { ["alias"] = countAlias, ["count"] = new JsonObject() });

            var body = new JsonObject
            {
                ["structuredAggregationQuery"] = new JsonObject
                {
                    ["structuredQuery"] = BuildQuery(collection, filters),
                    ["aggregations"] = aggregations
                }
            };

            var node = await SendAsync(HttpMethod.Post, $"{BaseUrl}:runAggregationQuery", body);

            var result = new Dictionary<string, long>();
            var fields = (node as JsonArray)?.FirstOrDefault(i => i?["result"] != null)?["result"]?["aggregateFields"] as JsonObject;
            if (fields != null)
            {
                foreach (var (alias, value) in fields)
                    result[alias] = FsDoc.ValueToLong(value);
            }
            return result;
        }

        // =============== zápisy ===============

        /// <summary>Zapíše jen uvedená pole (ostatní nechá být). Když dokument neexistuje, vytvoří ho.</summary>
        public static Task PatchAsync(string path, Dictionary<string, object?> fields) =>
            CommitAsync(new[] { SetWrite(path, fields, mask: fields.Keys) });

        /// <summary>Vytvoří dokument s automatickým ID (nebo se zadaným).</summary>
        public static async Task<FsDoc> CreateAsync(string collection, Dictionary<string, object?> fields, string? documentId = null)
        {
            var url = $"{BaseUrl}/{collection}";
            if (documentId != null) url += $"?documentId={Uri.EscapeDataString(documentId)}";
            var node = await SendAsync(HttpMethod.Post, url, new JsonObject { ["fields"] = ToFields(fields) });
            return FsDoc.From(node!);
        }

        public static Task DeleteAsync(string path) =>
            SendAsync(HttpMethod.Delete, $"{BaseUrl}/{path}", null);

        /// <summary>
        /// Zápis do dávky.
        /// mask = null → celý dokument se nahradí; jinak se zapíšou jen pole v masce.
        /// increments = atomické přičtení (FieldValue.Increment).
        /// mustNotExist = jen vytvořit (selže, když už existuje).
        /// </summary>
        /// <param name="serverTime">pole, do kterých server zapíše svůj čas (REQUEST_TIME) – nezávisle na hodinách telefonu</param>
        public static JsonObject SetWrite(string path, Dictionary<string, object?> fields,
            IEnumerable<string>? mask = null, IDictionary<string, long>? increments = null, bool mustNotExist = false,
            IEnumerable<string>? serverTime = null)
        {
            var write = new JsonObject
            {
                ["update"] = new JsonObject
                {
                    ["name"] = FullName(path),
                    ["fields"] = ToFields(fields)
                }
            };
            if (mask != null)
                write["updateMask"] = new JsonObject
                {
                    ["fieldPaths"] = new JsonArray(mask.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray())
                };
            var transforms = new List<JsonNode?>();
            if (increments != null)
                transforms.AddRange(increments.Select(kv => (JsonNode?)new JsonObject
                {
                    ["fieldPath"] = kv.Key,
                    ["increment"] = ToValue(kv.Value)
                }));
            if (serverTime != null)
                transforms.AddRange(serverTime.Select(f => (JsonNode?)new JsonObject
                {
                    ["fieldPath"] = f,
                    ["setToServerValue"] = "REQUEST_TIME"
                }));
            if (transforms.Count > 0)
                write["updateTransforms"] = new JsonArray(transforms.ToArray());
            if (mustNotExist)
                write["currentDocument"] = new JsonObject { ["exists"] = false };
            return write;
        }

        public static JsonObject DeleteWrite(string path) => new() { ["delete"] = FullName(path) };

        /// <summary>Atomická dávka zápisů (po 500 kusech). Každý zápis = 1 write v kvótě.</summary>
        public static async Task CommitAsync(IEnumerable<JsonObject> writes)
        {
            foreach (var chunk in writes.Chunk(500))
            {
                var body = new JsonObject { ["writes"] = new JsonArray(chunk.Select(w => (JsonNode?)w).ToArray()) };
                await SendAsync(HttpMethod.Post, $"{BaseUrl}:commit", body);
            }
        }

        /// <summary>Upsert dokumentu s atomickým přičtením číselných polí.</summary>
        public static Task UpsertWithIncrementAsync(string path, Dictionary<string, object?> set, Dictionary<string, long> increment) =>
            CommitAsync(new[] { SetWrite(path, set, mask: set.Keys, increments: increment) });

        // =============== interní věci ===============

        private static JsonObject BuildQuery(string collection, IEnumerable<Filter> filters)
        {
            var parts = filters.Select(f => (JsonNode?)new JsonObject
            {
                ["fieldFilter"] = new JsonObject
                {
                    ["field"] = new JsonObject { ["fieldPath"] = f.Field },
                    ["op"] = f.Op,
                    ["value"] = ToValue(f.Value)
                }
            }).ToArray();

            var query = new JsonObject
            {
                ["from"] = new JsonArray(new JsonObject { ["collectionId"] = collection })
            };

            if (parts.Length == 1)
                query["where"] = parts[0];
            else if (parts.Length > 1)
                query["where"] = new JsonObject
                {
                    ["compositeFilter"] = new JsonObject { ["op"] = "AND", ["filters"] = new JsonArray(parts) }
                };

            return query;
        }

        private static async Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonNode? body, bool allowNotFound = false)
        {
            var token = await AuthSession.GetTokenAsync();

            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
                throw new FirestoreException(response.StatusCode, text);

            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }

        private static JsonObject ToFields(Dictionary<string, object?> fields)
        {
            var obj = new JsonObject();
            foreach (var kv in fields) obj[kv.Key] = ToValue(kv.Value);
            return obj;
        }

        internal static JsonObject ToValue(object? value) => value switch
        {
            null => new JsonObject { ["nullValue"] = null },
            string s => new JsonObject { ["stringValue"] = s },
            bool b => new JsonObject { ["booleanValue"] = b },
            int i => new JsonObject { ["integerValue"] = i.ToString(CultureInfo.InvariantCulture) },
            long l => new JsonObject { ["integerValue"] = l.ToString(CultureInfo.InvariantCulture) },
            double d => new JsonObject { ["doubleValue"] = d },
            DateTime dt => new JsonObject
            {
                ["timestampValue"] = dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)
            },
            IDictionary<string, object?> map => new JsonObject
            {
                ["mapValue"] = new JsonObject { ["fields"] = ToFields(new Dictionary<string, object?>(map)) }
            },
            IDictionary<string, string?> smap => ToValue(smap.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)),
            IEnumerable list => new JsonObject
            {
                ["arrayValue"] = new JsonObject
                {
                    ["values"] = new JsonArray(list.Cast<object?>().Select(v => (JsonNode?)ToValue(v)).ToArray())
                }
            },
            _ => throw new ArgumentException($"Nepodporovaný typ pro Firestore: {value.GetType().Name}")
        };
    }

    /// <summary>Načtený dokument z Firestore.</summary>
    public class FsDoc
    {
        public string Name { get; private set; } = "";
        public string Id => Name[(Name.LastIndexOf('/') + 1)..];
        public JsonObject Fields { get; private set; } = new();

        public static FsDoc From(JsonNode node) => new()
        {
            Name = node["name"]?.GetValue<string>() ?? "",
            Fields = node["fields"] as JsonObject ?? new JsonObject()
        };

        public bool Has(string field) => Fields.ContainsKey(field);

        public string? Str(string field) => Fields[field]?["stringValue"]?.GetValue<string>();

        public long Int(string field) => ValueToLong(Fields[field]);

        public DateTime? Time(string field)
        {
            var s = Fields[field]?["timestampValue"]?.GetValue<string>();
            if (s == null) return null;
            return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }

        /// <summary>Pole řetězců (arrayValue).</summary>
        public List<string> StrArray(string field) =>
            (Fields[field]?["arrayValue"]?["values"] as JsonArray)?
                .Select(v => v?["stringValue"]?.GetValue<string>())
                .Where(s => s != null)
                .Select(s => s!)
                .ToList() ?? new List<string>();

        /// <summary>Mapa řetězců (mapValue).</summary>
        public Dictionary<string, string?> StrMap(string field)
        {
            var result = new Dictionary<string, string?>();
            if (Fields[field]?["mapValue"]?["fields"] is JsonObject map)
                foreach (var (key, value) in map)
                    result[key] = value?["stringValue"]?.GetValue<string>();
            return result;
        }

        internal static long ValueToLong(JsonNode? v)
        {
            if (v?["integerValue"] is JsonNode i)
                return long.Parse(i.GetValue<string>(), CultureInfo.InvariantCulture);
            if (v?["doubleValue"] is JsonNode d)
                return (long)Math.Round(d.GetValue<double>());
            return 0;
        }
    }

    public class FirestoreException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        /// <summary>Dokument už existuje (precondition exists=false) – u fronty = už odesláno.</summary>
        public bool IsAlreadyExists { get; }

        public FirestoreException(HttpStatusCode statusCode, string body)
            : base(Describe(statusCode, body))
        {
            StatusCode = statusCode;
            IsAlreadyExists = statusCode == HttpStatusCode.Conflict
                              || body.Contains("ALREADY_EXISTS", StringComparison.Ordinal);
        }

        private static string Describe(HttpStatusCode code, string body)
        {
            string? msg = null;
            try { msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); } catch { }
            if (msg == null)
            {
                // runQuery vrací chyby jako pole
                try { msg = (JsonNode.Parse(body) as JsonArray)?.FirstOrDefault()?["error"]?["message"]?.GetValue<string>(); } catch { }
            }

            if (msg != null && msg.Contains("requires an index", StringComparison.OrdinalIgnoreCase))
                return "Chybí index ve Firestore (viz návod: Firestore → Indexes). Detail: " + msg;

            return code switch
            {
                HttpStatusCode.Forbidden => "Databáze odmítla přístup (security rules).",
                HttpStatusCode.Unauthorized => "Přihlášení vypršelo, přihlas se znovu.",
                _ => $"Chyba databáze ({(int)code}): {msg ?? body}"
            };
        }
    }
}
