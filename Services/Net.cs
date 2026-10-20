using System.Net;

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

        /// <summary>Chyba, po které má smysl to zkusit znovu, až bude signál.</summary>
        public static bool IsNetworkError(Exception ex) => ex switch
        {
            HttpRequestException => true,
            TaskCanceledException => true,      // timeout
            OperationCanceledException => true,
            IOException => true,
            FirestoreException fe => fe.StatusCode is HttpStatusCode.ServiceUnavailable
                                      or HttpStatusCode.GatewayTimeout
                                      or HttpStatusCode.RequestTimeout
                                      or HttpStatusCode.TooManyRequests,
            AggregateException ae => ae.InnerExceptions.Any(IsNetworkError),
            _ => ex.InnerException != null && IsNetworkError(ex.InnerException)
        };
    }
}
