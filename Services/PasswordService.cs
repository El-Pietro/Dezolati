using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Dezolati.Services
{
    /// <summary>
    /// Zapomenuté heslo a změna hesla přes Firebase Auth REST (appka i web stejně).
    /// E-mail s odkazem posílá Firebase sám (šablona v konzoli: Authentication → Templates).
    /// </summary>
    public static class PasswordService
    {
        // veřejný webový klíč projektu (stejný je v MauiProgram a webovém AuthSession) – není to tajemství
        private const string ApiKey = "AIzaSyBmfjw3PRo85_PDq8Z763rmh49pags1PjA";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public const string ResetSentText =
            "Jestli ten e-mail u nás má účet, přišel ti na něj odkaz na nové heslo. Mrkni i do spamu.";

        /// <summary>
        /// Pošle odkaz na nové heslo. Vrací null = hotovo (i když e-mail neznáme – ať se nedá zjišťovat,
        /// kdo tu má účet), jinak text chyby.
        /// </summary>
        public static async Task<string?> SendResetAsync(string email)
        {
            email = email.Trim();
            if (email.Length < 3 || !email.Contains('@')) return "Napiš svůj e-mail.";
            try
            {
                var (ok, code, _) = await CallAsync("accounts:sendOobCode", new JsonObject
                {
                    ["requestType"] = "PASSWORD_RESET",
                    ["email"] = email
                });
                if (ok || code is "EMAIL_NOT_FOUND" or "USER_NOT_FOUND") return null;
                return code switch
                {
                    "INVALID_EMAIL" => "Tohle nevypadá jako e-mail.",
                    "TOO_MANY_ATTEMPTS_TRY_LATER" or "RESET_PASSWORD_EXCEED_LIMIT" => "Moc pokusů. Zkus to za chvíli.",
                    _ => "Odkaz se nepodařilo poslat." + (code.Length > 0 ? $" ({code})" : "")
                };
            }
            catch (Exception ex) when (Net.IsNetworkError(ex))
            {
                return "Bez signálu to nepůjde.";
            }
        }

        /// <summary>
        /// Změna hesla přihlášeného: nejdřív ověří staré heslo (Firebase chce čerstvé přihlášení),
        /// pak nastaví nové. Vrací null = hotovo, jinak text chyby. Volající se pak má přihlásit novým heslem
        /// (Firebase při změně hesla zneplatní staré přihlášení).
        /// </summary>
        public static async Task<string?> ChangeAsync(string email, string oldPassword, string newPassword)
        {
            if (string.IsNullOrEmpty(oldPassword) || string.IsNullOrEmpty(newPassword)) return "Vyplň staré i nové heslo.";
            if (newPassword.Length < 6) return "Nové heslo musí mít aspoň 6 znaků.";
            if (newPassword == oldPassword) return "Nové heslo je stejné jako staré. Odvážné.";
            try
            {
                var (ok, code, node) = await CallAsync("accounts:signInWithPassword", new JsonObject
                {
                    ["email"] = email,
                    ["password"] = oldPassword,
                    ["returnSecureToken"] = true
                });
                if (!ok)
                    return code is "INVALID_LOGIN_CREDENTIALS" or "INVALID_PASSWORD" ? "Staré heslo nesedí."
                         : code == "TOO_MANY_ATTEMPTS_TRY_LATER" ? "Moc pokusů. Zkus to za chvíli."
                         : "Změna se nepovedla." + (code.Length > 0 ? $" ({code})" : "");

                (ok, code, _) = await CallAsync("accounts:update", new JsonObject
                {
                    ["idToken"] = node!["idToken"]!.GetValue<string>(),
                    ["password"] = newPassword,
                    ["returnSecureToken"] = true
                });
                if (ok) return null;
                return code == "WEAK_PASSWORD" ? "Moc slabé heslo."
                     : "Změna se nepovedla." + (code.Length > 0 ? $" ({code})" : "");
            }
            catch (Exception ex) when (Net.IsNetworkError(ex))
            {
                return "Bez signálu to nepůjde.";
            }
        }

        private static async Task<(bool Ok, string Code, JsonNode? Node)> CallAsync(string method, JsonObject body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://identitytoolkit.googleapis.com/v1/{method}?key={ApiKey}")
            {
                Content = JsonContent.Create(body)
            };
            req.Headers.TryAddWithoutValidation("X-Firebase-Locale", "cs");   // e-mail česky (výchozí překlad Firebase)
            using var resp = await Http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return (true, "", JsonNode.Parse(text));
            string code = "";
            try
            {
                var msg = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? "";
                var i = msg.IndexOf(' ');
                code = i > 0 ? msg[..i] : msg;
            }
            catch { }
            return (false, code, null);
        }
    }
}
