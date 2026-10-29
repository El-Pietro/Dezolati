using System.Globalization;
using Dezolati.Models;

namespace Dezolati.Services
{
    /// <summary>Návrh poznámky do OpenStreetMap, čeká na schválení správcem.</summary>
    public sealed record OsmReport(string Id, string PubId, string Name, string Kind, double Lat, double Lon,
        string What, string? ByNick, DateTime? At)
    {
        public string WhatText => What switch
        {
            "closed" => "zavřeno",
            "reopened" => "zase otevřeno",
            "added" => "nový podnik",
            _ => What
        };
    }

    /// <summary>
    /// „Natrvalo zavřeno“ / „Zase otevřeno“ / „Přidat“ → návrh poznámky do OSM (Firestore OsmReports).
    /// Správce dostane oznámení a v Nastavení → Návrhy do OSM je odešle (anonymní OSM Note, bez účtu) nebo zahodí.
    /// Firestore: 1 zápis za návrh, čtení jen když správce otevře seznam.
    /// </summary>
    public static class OsmReports
    {
        private const string NotesUrl = "https://api.openstreetmap.org/api/0.6/notes";
        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Dezolati (+https://github.com/El-Pietro/Dezolati)");
            return http;
        }

        /// <summary>Uložit návrh (na pozadí, chyby tiše). Stejný podnik + stejná věc = jen jednou, dokud ho správce nevyřídí.</summary>
        public static async Task SubmitAsync(PubInfo pub, string what)
        {
            if (AuthSession.Uid is not string uid || !Net.IsOnline) return;
            try
            {
                await UserSession.EnsureReadyAsync();
                var id = $"{pub.Id}_{what}".Replace("/", "_");
                await FirestoreRest.CommitAsync(new[]
                {
                    FirestoreRest.SetWrite($"OsmReports/{id}", new()
                    {
                        ["PubId"] = pub.Id,
                        ["Name"] = PubService.NameOf(pub),
                        ["Kind"] = pub.Kind,
                        ["Lat"] = pub.Lat,
                        ["Lon"] = pub.Lon,
                        ["What"] = what,
                        ["By"] = uid,
                        ["ByNick"] = UserSession.Profile?.Nickname ?? "?"
                    }, mustNotExist: true, serverTime: new[] { "At" })
                });
            }
            catch (Exception ex)
            {
                // už nahlášené / bez signálu – nevadí
                System.Diagnostics.Debug.WriteLine($"Návrh do OSM: {ex.Message}");
            }
        }

        /// <summary>Čekající návrhy (jen správce), nejstarší první.</summary>
        public static async Task<List<OsmReport>> ListAsync()
        {
            var docs = await FirestoreRest.QueryAsync("OsmReports", Array.Empty<Filter>());
            double Dbl(FsDoc d, string f) =>
                d.Fields[f]?["doubleValue"]?.GetValue<double>() ?? d.Int(f);
            return docs.Select(d => new OsmReport(d.Id, d.Str("PubId") ?? "", d.Str("Name") ?? "?", d.Str("Kind") ?? "pub",
                    Dbl(d, "Lat"), Dbl(d, "Lon"), d.Str("What") ?? "", d.Str("ByNick"), d.Time("At")))
                .OrderBy(r => r.At ?? DateTime.MaxValue)
                .ToList();
        }

        /// <summary>Text poznámky pro mapovače (česky – mapují tu Češi).</summary>
        public static string NoteText(OsmReport r)
        {
            var kind = new PubInfo(r.PubId, r.Name, r.Kind, r.Lat, r.Lon, null, null, null).KindText;
            var osmLink = OsmLink(r.PubId);
            var text = r.What switch
            {
                "closed" => $"Podnik „{r.Name}“ ({kind}) je podle návštěvníků natrvalo zavřený.",
                "reopened" => $"Podnik „{r.Name}“ ({kind}) je podle návštěvníků zase otevřený.",
                "added" => $"Je tu podnik „{r.Name}“ ({kind}), v mapě chybí.",
                _ => $"Podnik „{r.Name}“ ({kind}): {r.What}"
            };
            if (osmLink != null) text += "\n" + osmLink;
            return text + "\n\nNahlášeno z appky Dezoláti (evidence piv pro kámoše), https://github.com/El-Pietro/Dezolati";
        }

        private static string? OsmLink(string pubId)
        {
            if (pubId.Length < 2 || !long.TryParse(pubId[1..], out _)) return null;   // vlastní podniky (c_…) v OSM nejsou
            var type = pubId[0] switch { 'n' => "node", 'w' => "way", 'r' => "relation", _ => null };
            return type == null ? null : $"https://www.openstreetmap.org/{type}/{pubId[1..]}";
        }

        /// <summary>Odeslat jako anonymní poznámku do OSM a návrh smazat.</summary>
        public static async Task SendAsync(OsmReport r)
        {
            string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("lat", F(r.Lat)),
                new KeyValuePair<string, string>("lon", F(r.Lon)),
                new KeyValuePair<string, string>("text", NoteText(r))
            });
            using var resp = await Http.PostAsync(NotesUrl, content);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"OpenStreetMap odpověděl {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
            }
            await FirestoreRest.DeleteAsync($"OsmReports/{r.Id}");
        }

        public static Task DiscardAsync(OsmReport r) => FirestoreRest.DeleteAsync($"OsmReports/{r.Id}");
    }
}
