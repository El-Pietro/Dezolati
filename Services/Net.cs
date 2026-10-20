using System.Net;
using System.Net.Sockets;

namespace Dezolati.Services
{
    /// <summary>Rozpoznání "nejde síť" vs. skutečná chyba.</summary>
    public static class Net
    {
        public static bool IsOnline
        {
            get
            {
                try { return Connectivity.Current.NetworkAccess == NetworkAccess.Internet; }
                catch { return true; } // když to platforma neumí zjistit, prostě to zkusíme
            }
        }

        // Android hlásí výpadek po svém (Java výjimky, WebException s Java textem) – poznat i podle textu.
        private static readonly string[] NetworkMessages =
        {
            "Unable to resolve host", "No address associated", "Failed to connect", "failed to connect",
            "Network is unreachable", "Connection reset", "Connection refused", "Software caused connection abort",
            "timeout", "timed out", "SSL handshake", "Name or service not known"
        };

        /// <summary>Chyba, po které má smysl to zkusit znovu, až bude signál.</summary>
        public static bool IsNetworkError(Exception ex)
        {
            if (ex is FirestoreException fe)
                return fe.StatusCode is HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout
                    or HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests;

            if (ex is HttpRequestException or WebException or SocketException
                or TaskCanceledException or OperationCanceledException or IOException)
                return true;

            // Java.Net.UnknownHostException, Java.Net.ConnectException, Java.IO.IOException, Javax.Net.Ssl.*
            var type = ex.GetType().FullName ?? "";
            if (type.StartsWith("Java.Net.", StringComparison.Ordinal) ||
                type.StartsWith("Java.IO.", StringComparison.Ordinal) ||
                type.StartsWith("Javax.Net.", StringComparison.Ordinal))
                return true;

            if (NetworkMessages.Any(m => ex.Message.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (ex is AggregateException ae)
                return ae.InnerExceptions.Any(IsNetworkError);

            if (ex.InnerException != null && IsNetworkError(ex.InnerException))
                return true;

            // telefon sám hlásí, že je bez internetu → cokoli kromě odpovědi databáze bereme jako výpadek
            return !IsOnline;
        }
    }
}
