using System.Globalization;
using Microsoft.JSInterop;

namespace Dezolati.Web
{
    /// <summary>
    /// Spojení s prohlížečem: načte úložiště (Preferences), hlídá signál a návrat do appky
    /// (jako App.OnResume v appce) a hlásí novou verzi webu.
    /// </summary>
    public static class WebPlatform
    {
        private static bool _ready;
        private static Bridge? _bridge;

        public static bool IsReady => _ready;
        public static bool IsStandalone { get; private set; }
        public static bool IsIos { get; private set; }
        public static bool UpdateReady { get; private set; }

        /// <summary>Něco se změnilo (signál, nová verze) → překreslit pruhy nahoře.</summary>
        public static event Action? Changed;

        public static readonly CultureInfo Cs = new("cs-CZ");

        public static async Task InitAsync(IJSRuntime js)
        {
            if (_ready) return;

            CultureInfo.DefaultThreadCurrentCulture = Cs;
            CultureInfo.DefaultThreadCurrentUICulture = Cs;
            CultureInfo.CurrentCulture = Cs;
            CultureInfo.CurrentUICulture = Cs;

            var all = await js.InvokeAsync<Dictionary<string, string>>("dz.storageAll");
            Preferences.Load(all);
            if (js is IJSInProcessRuntime sync)
                Preferences.Writer = (k, v) => sync.InvokeVoid("dz.storageSet", k, v);
            else
                Preferences.Writer = (k, v) => _ = js.InvokeVoidAsync("dz.storageSet", k, v).AsTask();

            Connectivity.Current.Set(await js.InvokeAsync<bool>("dz.isOnline"));
            IsStandalone = await js.InvokeAsync<bool>("dz.isStandalone");
            IsIos = await js.InvokeAsync<bool>("dz.isIos");

            _bridge = new Bridge();
            await js.InvokeVoidAsync("dz.hook", DotNetObjectReference.Create(_bridge));
            await js.InvokeVoidAsync("dz.persist");
            _ready = true;
        }

        private sealed class Bridge
        {
            [JSInvokable]
            public void OnOnline(bool online)
            {
                Connectivity.Current.Set(online);
                Changed?.Invoke();
            }

            /// <summary>Návrat do appky: lístek znovu ze serveru (mezitím mohl zapisovat telefon).</summary>
            [JSInvokable]
            public void OnResume()
            {
                Services.LocalCache.MarkStale();
                Services.UserSession.RaiseDrinksChanged();
            }

            [JSInvokable]
            public void OnUpdateReady()
            {
                UpdateReady = true;
                Changed?.Invoke();
            }
        }
    }

    public enum PencilVariant { Primary, Secondary, Danger, Outline }

    /// <summary>
    /// Dialogy ve stylu lístku (jako Controls/Listek.cs v appce). Vykresluje je DialogHost v rozložení stránky.
    /// </summary>
    public static class Note
    {
        public sealed record Request(string Title, string? Message, (string Text, PencilVariant Variant)[] Buttons,
            bool Busy, TaskCompletionSource<string?> Result);

        public static Request? Current { get; private set; }
        public static event Action? Changed;

        public static Task AlertAsync(string title, string? message, string ok = "OK") =>
            ShowAsync(title, message, (ok, PencilVariant.Primary));

        public static async Task<bool> ConfirmAsync(string title, string? message, string yes, string no = "Nic", bool danger = false) =>
            await ShowAsync(title, message,
                (yes, danger ? PencilVariant.Danger : PencilVariant.Primary),
                (no, PencilVariant.Secondary)) == yes;

        /// <summary>Vrací text zmáčknutého tlačítka, null = ťuknutí mimo lístek.</summary>
        public static Task<string?> ShowAsync(string title, string? message, params (string Text, PencilVariant Variant)[] buttons)
        {
            Current?.Result.TrySetResult(null);
            var req = new Request(title, message, buttons, false, new TaskCompletionSource<string?>());
            Current = req;
            Changed?.Invoke();
            return req.Result.Task;
        }

        /// <summary>Lístek „pracuju…“ bez tlačítek. Zavře se přes Dispose.</summary>
        public static IDisposable Busy(string title, string? message)
        {
            Current?.Result.TrySetResult(null);
            var req = new Request(title, message, Array.Empty<(string, PencilVariant)>(), true, new TaskCompletionSource<string?>());
            Current = req;
            Changed?.Invoke();
            return new Closer(req);
        }

        internal static void Close(Request req, string? result)
        {
            req.Result.TrySetResult(result);
            if (Current == req)
            {
                Current = null;
                Changed?.Invoke();
            }
        }

        private sealed class Closer : IDisposable
        {
            private readonly Request _req;
            public Closer(Request req) => _req = req;
            public void Dispose() => Close(_req, null);
        }
    }
}
