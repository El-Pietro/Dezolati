using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Dezolati.Services
{
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

        // ---------- veřejné operace ----------

        public static async Task<FsDoc?> GetAsync(string path)
        {
            var node = await SendAsync(HttpMethod.Get, $"{BaseUrl}/{path}", null, allowNotFound: true);
            return node == null ? null : FsDoc.From(node);
        }

        /// <summary>Vytvoří dokument s automatickým ID (nebo se zadaným).</summary>
        public static async Task<FsDoc> CreateAsync(string collection, Dictionary<string, object?> fields, string? documentId = null)
        {
            var url = $"{BaseUrl}/{collection}";
            if (documentId != null) url += $"?documentId={Uri.EscapeDataString(documentId)}";
            var node = await SendAsync(HttpMethod.Post, url, new JsonObject { ["fields"] = ToFields(fields) });
            return FsDoc.From(node!);
        }

        /// <summary>Zapíše jen uvedená pole (ostatní nechá být). Když dokument neexistuje, vytvoří ho.</summary>
        public static async Task PatchAsync(string path, Dictionary<string, object?> fields)
        {
            var mask = string.Join("&", fields.Keys.Select(k => $"updateMask.fieldPaths={Uri.EscapeDataString(k)}"));
            await SendAsync(HttpMethod.Patch, $"{BaseUrl}/{path}?{mask}", new JsonObject { ["fields"] = ToFields(fields) });
        }

        /// <summary>Upsert dokumentu s atomickým přičtením číselných polí (FieldValue.Increment).</summary>
        public static async Task UpsertWithIncrementAsync(string path, Dictionary<string, object?> set, Dictionary<string, long> increment)
        {
            var write = new JsonObject
            {
                ["update"] = new JsonObject
                {
                    ["name"] = FullName(path),
                    ["fields"] = ToFields(set)
                },
                ["updateMask"] = new JsonObject
                {
                    ["fieldPaths"] = new JsonArray(set.Keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray())
                },
                ["updateTransforms"] = new JsonArray(increment.Select(kv => (JsonNode?)new JsonObject
                {
                    ["fieldPath"] = kv.Key,
                    ["increment"] = ToValue(kv.Value)
                }).ToArray())
            };

            await SendAsync(HttpMethod.Post, $"{BaseUrl}:commit", new JsonObject { ["writes"] = new JsonArray(write) });
        }

        /// <summary>Dotaz s rovnostmi spojenými přes AND. limit 0 = bez limitu.</summary>
        public static async Task<List<FsDoc>> QueryAsync(string collection, int limit, params (string Field, object Value)[] equals)
        {
            var filters = equals.Select(e => (JsonNode?)new JsonObject
            {
                ["fieldFilter"] = new JsonObject
                {
                    ["field"] = new JsonObject { ["fieldPath"] = e.Field },
                    ["op"] = "EQUAL",
                    ["value"] = ToValue(e.Value)
                }
            }).ToArray();

            var query = new JsonObject
            {
                ["from"] = new JsonArray(new JsonObject { ["collectionId"] = collection })
            };

            if (filters.Length == 1)
                query["where"] = filters[0];
            else if (filters.Length > 1)
                query["where"] = new JsonObject
                {
                    ["compositeFilter"] = new JsonObject { ["op"] = "AND", ["filters"] = new JsonArray(filters) }
                };

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

        // ---------- interní věci ----------

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

        public string? Str(string field) => Fields[field]?["stringValue"]?.GetValue<string>();

        public long Int(string field)
        {
            var v = Fields[field];
            if (v?["integerValue"] is JsonNode i)
                return long.Parse(i.GetValue<string>(), CultureInfo.InvariantCulture);
            if (v?["doubleValue"] is JsonNode d)
                return (long)d.GetValue<double>();
            return 0;
        }

        public DateTime? Time(string field)
        {
            var s = Fields[field]?["timestampValue"]?.GetValue<string>();
            if (s == null) return null;
            return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
    }

    public class FirestoreException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public FirestoreException(HttpStatusCode statusCode, string body)
            : base(Describe(statusCode, body))
        {
            StatusCode = statusCode;
        }

        private static string Describe(HttpStatusCode code, string body)
        {
            string? msg = null;
            try { msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); } catch { }
            return code switch
            {
                HttpStatusCode.Forbidden => "Databáze odmítla přístup (security rules).",
                HttpStatusCode.Unauthorized => "Přihlášení vypršelo, přihlas se znovu.",
                _ => $"Chyba databáze ({(int)code}): {msg ?? body}"
            };
        }
    }
}
