using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dezolati.Services
{
    /// <summary>
    /// Přihlášení na webu – stejné účty jako v appce (Firebase Auth, e-mail + heslo), jen přímo přes REST
    /// (appka používá knihovnu FirebaseAuthentication.net, ta se do prohlížeče nehodí).
    /// Přihlášení si web pamatuje v localStorage (refresh token), heslo se nikde neukládá.
    /// Veřejné členy jsou stejné jako v Services/AuthSession.cs, takže sdílené soubory nic nepoznají.
    /// </summary>
    public static class AuthSession
    {
        // stejný veřejný klíč projektu jako v MauiProgram.cs (identifikátor, ne tajemství)
        internal const string ApiKey = "AIzaSyBmfjw3PRo85_PDq8Z763rmh49pags1PjA";
        private const string KeyUser = "auth_user";
        private const string KeyGuest = "akce_guest";

        internal static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromSeconds(20) };

        private sealed record StoredUser(string Uid, string? Email, string? DisplayName,
            string RefreshToken, string IdToken, DateTime ExpiresAtUtc);

        private static StoredUser? _user;
        private static bool _loaded;
        private static readonly SemaphoreSlim RefreshLock = new(1, 1);

        private static StoredUser? User
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    try
                    {
                        var json = Preferences.Get(KeyUser, null as string);
                        _user = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<StoredUser>(json);
                    }
                    catch { _user = null; }
                }
                return _user;
            }
        }

        // host z odkazu na akci (anonymní účet Firebase) – NENÍ přihlášení: Uid zůstává null, jen GetTokenAsync ho použije
        private static StoredUser? _guest;
        private static bool _guestLoaded;

        private static StoredUser? Guest
        {
            get
            {
                if (!_guestLoaded)
                {
                    _guestLoaded = true;
                    try
                    {
                        var json = Preferences.Get(KeyGuest, null as string);
                        _guest = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<StoredUser>(json);
                    }
                    catch { _guest = null; }
                }
                return _guest;
            }
        }

        public static string? GuestUid => Guest?.Uid;

        /// <summary>Anonymní účet pro hosta z odkazu (vytvoří jen jednou, pak ho pamatuje).</summary>
        public static async Task<string> SignInGuestAsync()
        {
            if (Guest is { } g) return g.Uid;
            var node = await CallAsync("accounts:signUp", new JsonObject { ["returnSecureToken"] = true });
            var guest = new StoredUser(node["localId"]!.GetValue<string>(), null, null,
                node["refreshToken"]!.GetValue<string>(), node["idToken"]!.GetValue<string>(),
                DateTime.UtcNow.AddSeconds(ParseSeconds(node["expiresIn"])));
            SaveGuest(guest);
            return guest.Uid;
        }

        private static void SaveGuest(StoredUser? guest)
        {
            _guest = guest;
            _guestLoaded = true;
            if (guest == null) Preferences.Remove(KeyGuest);
            else Preferences.Set(KeyGuest, JsonSerializer.Serialize(guest));
        }

        public static bool IsSignedIn => User != null;
        public static string? Uid => User?.Uid;
        public static string? DisplayName => User?.DisplayName;
        public static string? Email => User?.Email;

        /// <summary>Přihlášení se změnilo (přihlásil / odhlásil se).</summary>
        public static event Action? Changed;

        /// <summary>ID token pro Firestore REST, před vypršením se sám obnoví.</summary>
        public static async Task<string> GetTokenAsync()
        {
            var user = User ?? Guest ?? throw new InvalidOperationException("Uživatel není přihlášen.");
            if (DateTime.UtcNow < user.ExpiresAtUtc.AddMinutes(-5)) return user.IdToken;

            await RefreshLock.WaitAsync();
            try
            {
                user = User ?? Guest ?? throw new InvalidOperationException("Uživatel není přihlášen.");
                if (DateTime.UtcNow < user.ExpiresAtUtc.AddMinutes(-5)) return user.IdToken;
                var isGuest = User == null;

                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = user.RefreshToken
                });
                using var resp = await Http.PostAsync($"https://securetoken.googleapis.com/v1/token?key={ApiKey}", content);
                var text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    var code = ErrorCode(text);
                    // účet smazaný / zablokovaný / token zneplatněný → odhlásit, ať to nezkouší pořád dokola
                    if (code is "TOKEN_EXPIRED" or "USER_DISABLED" or "USER_NOT_FOUND" or "INVALID_REFRESH_TOKEN")
                    {
                        if (isGuest) { SaveGuest(null); throw new AuthException(code, "Odkaz na akci otevři znovu."); }
                        SignOut();
                        throw new AuthException(code, "Přihlášení vypršelo, přihlas se znovu.");
                    }
                    throw new HttpRequestException($"Obnovení přihlášení se nepovedlo ({(int)resp.StatusCode}).");
                }
                var node = JsonNode.Parse(text)!;
                var updated = user with
                {
                    IdToken = node["id_token"]!.GetValue<string>(),
                    RefreshToken = node["refresh_token"]?.GetValue<string>() ?? user.RefreshToken,
                    ExpiresAtUtc = DateTime.UtcNow.AddSeconds(ParseSeconds(node["expires_in"]))
                };
                if (isGuest) SaveGuest(updated); else Save(updated);
                return updated.IdToken;
            }
            finally
            {
                RefreshLock.Release();
            }
        }

        public static async Task SignInAsync(string email, string password)
        {
            var node = await CallAsync("accounts:signInWithPassword", new JsonObject
            {
                ["email"] = email,
                ["password"] = password,
                ["returnSecureToken"] = true
            });
            SaveFrom(node, email);
        }

        /// <summary>Nový účet + přezdívka (displayName), rovnou přihlásí.</summary>
        public static async Task SignUpAsync(string email, string password, string displayName)
        {
            JsonNode node;
            if (User == null && Guest != null)
            {
                // host z odkazu si zakládá účet: z anonymního účtu se stane normální (stejné uid → zůstane na akci)
                node = await CallAsync("accounts:update", new JsonObject
                {
                    ["idToken"] = await GetTokenAsync(),
                    ["email"] = email,
                    ["password"] = password,
                    ["returnSecureToken"] = true
                });
                SaveFrom(node, email);
                SaveGuest(null);
            }
            else
            {
                node = await CallAsync("accounts:signUp", new JsonObject
                {
                    ["email"] = email,
                    ["password"] = password,
                    ["returnSecureToken"] = true
                });
                SaveFrom(node, email);
            }

            try
            {
                var upd = await CallAsync("accounts:update", new JsonObject
                {
                    ["idToken"] = node["idToken"]!.GetValue<string>(),
                    ["displayName"] = displayName,
                    ["returnSecureToken"] = true
                });
                SaveFrom(upd, email, displayName);
            }
            catch
            {
                // přezdívka je stejně v profilu (Users), displayName je jen záloha
                Save(User! with { DisplayName = displayName });
            }
        }

        public static void SignOut()
        {
            _user = null;
            _loaded = true;
            Preferences.Remove(KeyUser);
            UserSession.Reset();
            Changed?.Invoke();
        }

        // ---------------- interní ----------------

        private static async Task<JsonNode> CallAsync(string method, JsonObject body)
        {
            using var resp = await Http.PostAsJsonAsync(
                $"https://identitytoolkit.googleapis.com/v1/{method}?key={ApiKey}", body);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                var code = ErrorCode(text);
                throw new AuthException(code, Translate(code));
            }
            return JsonNode.Parse(text)!;
        }

        private static void SaveFrom(JsonNode node, string email, string? displayName = null)
        {
            var current = User;
            Save(new StoredUser(
                node["localId"]?.GetValue<string>() ?? current?.Uid ?? throw new InvalidOperationException("Chybí uid."),
                node["email"]?.GetValue<string>() ?? email,
                displayName ?? Str(node["displayName"]) ?? current?.DisplayName,
                node["refreshToken"]?.GetValue<string>() ?? current!.RefreshToken,
                node["idToken"]?.GetValue<string>() ?? current!.IdToken,
                DateTime.UtcNow.AddSeconds(ParseSeconds(node["expiresIn"]))));
        }

        private static string? Str(JsonNode? n)
        {
            var s = n?.GetValue<string>();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static void Save(StoredUser user)
        {
            var changed = _user?.Uid != user.Uid;
            _user = user;
            _loaded = true;
            Preferences.Set(KeyUser, JsonSerializer.Serialize(user));
            if (changed) Changed?.Invoke();
        }

        private static double ParseSeconds(JsonNode? n) =>
            n != null && double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 3600;

        private static string ErrorCode(string body)
        {
            try
            {
                var msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? "";
                // „WEAK_PASSWORD : Password should be at least 6 characters“
                var i = msg.IndexOf(' ');
                return i > 0 ? msg[..i] : msg;
            }
            catch { return ""; }
        }

        private static string Translate(string code) => code switch
        {
            "INVALID_LOGIN_CREDENTIALS" or "EMAIL_NOT_FOUND" or "INVALID_PASSWORD" or "INVALID_EMAIL"
                => "Špatný e-mail nebo heslo.",
            "TOO_MANY_ATTEMPTS_TRY_LATER" => "Moc pokusů. Dej si pauzu (a vodu).",
            "USER_DISABLED" => "Účet je zablokovaný.",
            "EMAIL_EXISTS" => "Tenhle e-mail už má účet. Přihlas se.",
            "WEAK_PASSWORD" => "Moc slabé heslo.",
            "MISSING_PASSWORD" => "Vyplň heslo.",
            _ => "Přihlášení se nepovedlo." + (code.Length > 0 ? $" ({code})" : "")
        };
    }

    public sealed class AuthException : Exception
    {
        public string Code { get; }
        public AuthException(string code, string message) : base(message) => Code = code;
    }
}
