using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dezolati.Services
{
    public sealed class UsageLimits
    {
        [JsonPropertyName("reads")] public long Reads { get; set; }
        [JsonPropertyName("writes")] public long Writes { get; set; }
        [JsonPropertyName("deletes")] public long Deletes { get; set; }
        [JsonPropertyName("invocations")] public long Invocations { get; set; }
    }

    public sealed class UsageToday
    {
        [JsonPropertyName("reads")] public long Reads { get; set; }
        [JsonPropertyName("writes")] public long Writes { get; set; }
        [JsonPropertyName("deletes")] public long Deletes { get; set; }
        [JsonPropertyName("since")] public DateTime Since { get; set; }
    }

    public sealed class UsageMonth
    {
        [JsonPropertyName("invocations")] public long Invocations { get; set; }
        [JsonPropertyName("projected")] public long Projected { get; set; }
        [JsonPropertyName("since")] public DateTime Since { get; set; }
    }

    public sealed class UsageDay
    {
        [JsonPropertyName("day")] public string Day { get; set; } = "";
        [JsonPropertyName("reads")] public long Reads { get; set; }
        [JsonPropertyName("writes")] public long Writes { get; set; }
        [JsonPropertyName("deletes")] public long Deletes { get; set; }
    }

    public sealed class UsageReport
    {
        [JsonPropertyName("limits")] public UsageLimits Limits { get; set; } = new();
        [JsonPropertyName("today")] public UsageToday Today { get; set; } = new();
        [JsonPropertyName("month")] public UsageMonth Month { get; set; } = new();
        [JsonPropertyName("days")] public List<UsageDay> Days { get; set; } = new();
        [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Přehled spotřeby Firebase proti bezplatným limitům (Cloud Function `usage`, čísla z Cloud Monitoringu).
    /// Pustí jen správce – skutečná kontrola je na serveru, tady jen kvůli tlačítku.
    /// </summary>
    public static class UsageService
    {
        /// <summary>UID správce (stejné jako ADMIN_UID ve functions/.env).</summary>
        public const string AdminUid = "rB2bUxNHHzbfyHx3mYW4JW61l3N2";

        private const string Url = "https://europe-west10-dezolati.cloudfunctions.net/usage";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

        public static bool IsAdmin => AuthSession.Uid == AdminUid;

        public static async Task<UsageReport> GetAsync()
        {
            var token = await AuthSession.GetTokenAsync();
            using var req = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Content = new StringContent("{\"data\":{}}", Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new InvalidOperationException(msg ?? $"Funkce vrátila chybu ({(int)resp.StatusCode}).");
            }
            if (!resp.IsSuccessStatusCode || !root.TryGetProperty("result", out var result))
                throw new InvalidOperationException($"Funkce neodpověděla ({(int)resp.StatusCode}).");

            return result.Deserialize<UsageReport>() ?? throw new InvalidOperationException("Prázdná odpověď.");
        }
    }
}
