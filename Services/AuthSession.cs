using Firebase.Auth;

namespace Dezolati.Services
{
    /// <summary>
    /// Jediné místo, kde žije přihlášený uživatel. Nahrazuje statické SignInModel.ID/Username/UserEmail.
    /// FirebaseAuthClient s FileUserRepository si přihlášení pamatuje i po restartu appky.
    /// </summary>
    public static class AuthSession
    {
        public static FirebaseAuthClient Client { get; set; } = null!;

        public static Firebase.Auth.User? CurrentUser => Client?.User;
        public static bool IsSignedIn => CurrentUser != null;
        public static string? Uid => CurrentUser?.Uid;
        public static string? DisplayName => CurrentUser?.Info?.DisplayName;
        public static string? Email => CurrentUser?.Info?.Email;

        /// <summary>ID token pro Firestore REST. Knihovna ho sama obnoví, když vyprší.</summary>
        public static async Task<string> GetTokenAsync()
        {
            var user = CurrentUser ?? throw new InvalidOperationException("Uživatel není přihlášen.");
            return await user.GetIdTokenAsync();
        }

        public static void SignOut()
        {
            Client?.SignOut();
            UserSession.Reset();
        }
    }
}
