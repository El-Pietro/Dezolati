using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dezolati.Services
{
    /// <summary>Hlášení „Tady jsou trhy!“ (TrhReports/{id}); Status píše server: pending / known / approved / rejected.</summary>
    public sealed record TrhReport(string Id, string By, string ByNick, double Lat, double Lon, string Day,
        string? From, string? To, string? Note, string Status, string? TrhId, DateTime? At);

    /// <summary>
    /// Vánoční trhy: nahlášení (kdokoli, poloha z GPS) a správa (jen správce – Cloud Function trhAction).
    /// Sdílené s webem.
    /// </summary>
    public static class TrhyService
    {
        private const string Url = "https://europe-west10-dezolati.cloudfunctions.net/trhAction";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>Uloží hlášení. Server ho pak buď pozná (už známý trh), schválí sám (3 lidé), nebo pošle správci.</summary>
        public static async Task SendAsync(double lat, double lon, double? accuracy, DateTime? from, DateTime? to, string? note)
        {
            if (AuthSession.Uid is not string uid) throw new InvalidOperationException("Nejdřív se přihlas.");
            await UserSession.EnsureReadyAsync();
            var fields = new Dictionary<string, object?>
            {
                ["By"] = uid,
                ["ByNick"] = UserSession.Profile?.Nickname ?? "?",
                ["Lat"] = Math.Round(lat, 6),
                ["Lon"] = Math.Round(lon, 6),
                ["Day"] = Events.Iso(Events.Today)
            };
            if (accuracy is double a) fields["Acc"] = Math.Round(a, 1);
            if (from is DateTime f) fields["From"] = Events.Iso(f);
            if (to is DateTime t) fields["To"] = Events.Iso(t);
            if (!string.IsNullOrWhiteSpace(note)) fields["Note"] = note.Trim()[..Math.Min(note.Trim().Length, 300)];
            await FirestoreRest.CommitAsync(new[]
            {
                FirestoreRest.SetWrite($"TrhReports/{Guid.NewGuid():N}", fields, mustNotExist: true, serverTime: new[] { "At" })
            });
        }

        // ---------------- správce ----------------

        public static async Task<List<TrhReport>> ListAsync()
        {
            var docs = await FirestoreRest.QueryAsync("TrhReports", Array.Empty<Filter>());
            double D(FsDoc d, string f) => d.Fields[f]?["doubleValue"]?.GetValue<double>() ?? FsDoc.ValueToLong(d.Fields[f]);
            return docs.Select(d => new TrhReport(d.Id, d.Str("By") ?? "", d.Str("ByNick") ?? "?", D(d, "Lat"), D(d, "Lon"),
                    d.Str("Day") ?? "", d.Str("From"), d.Str("To"), d.Str("Note"), d.Str("Status") ?? "pending", d.Str("TrhId"), d.Time("At")))
                .OrderBy(r => r.Status == "pending" ? 0 : 1).ThenByDescending(r => r.At).ToList();
        }

        public static Task ApproveAsync(string id, string? name, DateTime? from, DateTime? to) => CallAsync(new JsonObject
        {
            ["action"] = "approve", ["id"] = id, ["name"] = name,
            ["from"] = from is DateTime f ? Events.Iso(f) : null, ["to"] = to is DateTime t ? Events.Iso(t) : null
        });

        public static Task RejectAsync(string id) => CallAsync(new JsonObject { ["action"] = "reject", ["id"] = id });

        public static Task DeleteReportAsync(string id) => FirestoreRest.DeleteAsync($"TrhReports/{id}");

        /// <summary>Oprava trhu ze seznamu (termín, název, stav, smazání). Prodloužení termínu rozdá odznaky zpětně.</summary>
        public static Task EditAsync(string trhId, DateTime? from, DateTime? to, string? name, string? status, bool remove = false) =>
            CallAsync(new JsonObject
            {
                ["action"] = "edit", ["trhId"] = trhId,
                ["from"] = from is DateTime f ? Events.Iso(f) : null, ["to"] = to is DateTime t ? Events.Iso(t) : null,
                ["name"] = name, ["status"] = status, ["remove"] = remove
            });

        private static async Task<JsonNode?> CallAsync(JsonObject data)
        {
            var token = await AuthSession.GetTokenAsync();
            using var req = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Content = new StringContent(new JsonObject { ["data"] = data }.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            JsonNode? root;
            try { root = JsonNode.Parse(json); }
            catch (JsonException) { throw new InvalidOperationException($"Server neodpověděl ({(int)resp.StatusCode})."); }
            if (root?["error"] is JsonNode err)
                throw new InvalidOperationException(err["message"]?.GetValue<string>() ?? "Server vrátil chybu.");
            // po změně trhů si hned načíst nový seznam
            await Events.EnsureAsync(force: true);
            return root?["result"];
        }
    }
}
