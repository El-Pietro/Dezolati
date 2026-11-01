using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Dezolati.Services
{
    /// <summary>
    /// Smazání účtu a všech dat (Cloud Function <c>deleteAccount</c>). Appka sama by to nesvedla –
    /// log pití smí jen přidávat a cizí dokumenty (přátelství, připíjení) mazat nesmí. Sdílené s webem.
    /// </summary>
    public static class AccountService
    {
        private const string Url = "https://europe-west10-dezolati.cloudfunctions.net/deleteAccount";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

        /// <summary>Smaže účet na serveru. Vyhodí výjimku s textem pro člověka, když to nevyjde.</summary>
        public static async Task DeleteAsync()
        {
            var token = await AuthSession.GetTokenAsync();
            using var req = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Content = new StringContent("{\"data\":{\"confirm\":\"SMAZAT\"}}", Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            string? error = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    error = err.TryGetProperty("message", out var m) ? m.GetString() : "Server vrátil chybu.";
                else if (resp.IsSuccessStatusCode && doc.RootElement.TryGetProperty("result", out _))
                    return;
            }
            catch (JsonException) { }
            throw new InvalidOperationException(error ?? $"Server neodpověděl ({(int)resp.StatusCode}).");
        }

        /// <summary>Po smazání: v tomhle telefonu / prohlížeči nic nenechat.</summary>
        public static void ForgetLocal()
        {
            FriendAliases.Clear();
            FriendBadge.Reset();
            try { Preferences.Clear(); } catch { }
            try
            {
                foreach (var f in Directory.GetFiles(FileSystem.AppDataDirectory)) File.Delete(f);   // lístek, adresář, fronta…
            }
            catch { }
            AuthSession.SignOut();
        }
    }
}
