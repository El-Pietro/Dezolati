using Microsoft.JSInterop;

namespace Dezolati.Web
{
    /// <summary>
    /// Oznámení od kámošů v prohlížeči (web push přes Firebase Cloud Messaging).
    /// Token prohlížeče jde do PushTokens stejně jako token telefonu → server posílá oběma stejně, nic se nemění.
    /// Na iPhonu to jde jen z webu přidaného na plochu (iOS 16.4+) a povolení se musí vyžádat ťuknutím.
    /// </summary>
    public static class WebPush
    {
        public enum State { NotConfigured, Unsupported, NeedsHomeScreen, Off, Denied, On }

        public static async Task<State> GetStateAsync()
        {
            var js = WebPlatform.Js;
            if (js == null) return State.Unsupported;
            if (!await js.InvokeAsync<bool>("dz.pushConfigured")) return State.NotConfigured;
            if (!await js.InvokeAsync<bool>("dz.pushSupported"))
                return WebPlatform.IsIos && !WebPlatform.IsStandalone ? State.NeedsHomeScreen : State.Unsupported;
            var permission = await js.InvokeAsync<string>("dz.pushPermission");
            if (permission == "denied") return State.Denied;
            return permission == "granted" && Services.PushService.HasToken ? State.On : State.Off;
        }

        /// <summary>Zapnout (z ťuknutí – kvůli iPhonu). Vrací chybu k zobrazení, nebo null.</summary>
        public static async Task<string?> EnableAsync()
        {
            var js = WebPlatform.Js;
            if (js == null || Services.AuthSession.Uid is not string uid) return "Nejsi přihlášený.";
            var token = await js.InvokeAsync<string>("dz.pushToken", true);
            if (token.StartsWith("error:", StringComparison.Ordinal))
                return token switch
                {
                    "error:denied" => "Prohlížeč oznámení zakázal. Povol je v nastavení prohlížeče (u iPhonu v Nastavení → Oznámení → Dezoláti).",
                    "error:default" => "Oznámení jsi nepovolil.",
                    "error:config" => "Oznámení na webu ještě nejsou nastavená.",
                    "error:unsupported" => WebPlatform.IsIos
                        ? "Na iPhonu jdou oznámení jen z ikony na ploše (Sdílet → Přidat na plochu), iOS 16.4 a novější."
                        : "Tenhle prohlížeč oznámení neumí.",
                    _ => "Nepovedlo se: " + token[6..]
                };
            await Services.PushService.SaveTokenAsync(uid, token);
            return null;
        }

        public static async Task DisableAsync() => await Services.PushService.UnregisterAsync();

        /// <summary>Při startu: když jsou oznámení povolená, obnovit token (prohlížeč ho občas změní). Potichu.</summary>
        public static async Task RefreshAsync()
        {
            try
            {
                var js = WebPlatform.Js;
                if (js == null || Services.AuthSession.Uid is not string uid || !Services.PushService.HasToken) return;
                if (await js.InvokeAsync<string>("dz.pushPermission") != "granted") return;
                var token = await js.InvokeAsync<string>("dz.pushToken", false);
                if (!token.StartsWith("error:", StringComparison.Ordinal))
                    await Services.PushService.SaveTokenAsync(uid, token);
            }
            catch { /* příště */ }
        }
    }
}
