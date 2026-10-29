namespace Microsoft.Maui.Networking
{
    public enum NetworkAccess { Unknown = 0, None = 1, Local = 2, ConstrainedInternet = 3, Internet = 4 }

    public sealed class ConnectivityChangedEventArgs : EventArgs
    {
        public ConnectivityChangedEventArgs(NetworkAccess access) => NetworkAccess = access;
        public NetworkAccess NetworkAccess { get; }
    }

    /// <summary>Signál podle prohlížeče (navigator.onLine + události online/offline, viz app.js).</summary>
    public sealed class Connectivity
    {
        public static Connectivity Current { get; } = new();

        private bool _online = true;

        public NetworkAccess NetworkAccess => _online ? NetworkAccess.Internet : NetworkAccess.None;

        public event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;

        internal void Set(bool online)
        {
            if (_online == online) return;
            _online = online;
            ConnectivityChanged?.Invoke(this, new ConnectivityChangedEventArgs(NetworkAccess));
        }
    }
}

namespace Microsoft.Maui.ApplicationModel
{
    /// <summary>Verze webu – do adresáře se zapíše „web 1.0“, ať je ve „Verze u lidí“ vidět, kdo jede přes web.</summary>
    public sealed class AppInfo
    {
        public const string WebVersion = "1.0";
        public static AppInfo Current { get; } = new();
        public string VersionString => "web " + WebVersion;
        public string BuildString => WebVersion;
        public string Name => "Dezoláti";
    }

    public static class MainThread
    {
        // Blazor WebAssembly má jedno vlákno – všechno už „na hlavním vlákně“ je.
        public static void BeginInvokeOnMainThread(Action action) => action();
        public static bool IsMainThread => true;
    }
}
