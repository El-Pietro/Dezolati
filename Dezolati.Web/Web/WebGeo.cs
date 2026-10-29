using System.Text.Json;
using Microsoft.JSInterop;

namespace Dezolati.Web
{
    /// <summary>Poloha z prohlížeče (navigator.geolocation). Jen když je web otevřený – na pozadí nic.</summary>
    public static class WebGeo
    {
        public readonly record struct Location(double Lat, double Lon, double Accuracy);

        private sealed class Result
        {
            public bool Ok { get; set; }
            public double Lat { get; set; }
            public double Lon { get; set; }
            public double Acc { get; set; }
            public string? Error { get; set; }
        }

        /// <summary>Poslední chyba („denied“ / „unavailable“ / „timeout“ / „unsupported“).</summary>
        public static string? LastError { get; private set; }

        /// <summary>Poloha, nebo null (zakázaná / nejde zjistit / vypršel čas). maxAge = jak stará smí být.</summary>
        public static async Task<Location?> GetAsync(TimeSpan timeout, TimeSpan maxAge)
        {
            var js = WebPlatform.Js;
            if (js == null) return null;
            try
            {
                var json = await js.InvokeAsync<string>("dz.geo", (int)timeout.TotalMilliseconds, (int)maxAge.TotalMilliseconds);
                var r = JsonSerializer.Deserialize<Result>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (r is { Ok: true })
                {
                    LastError = null;
                    return new Location(r.Lat, r.Lon, r.Acc);
                }
                LastError = r?.Error ?? "unavailable";
                return null;
            }
            catch
            {
                LastError = "unavailable";
                return null;
            }
        }

        public static string Explain(string? error) => error switch
        {
            "denied" => WebPlatform.IsIos
                ? "Poloha je zakázaná. Na iPhonu: Nastavení → Soukromí → Polohové služby → Safari (nebo Dezoláti) → Při používání."
                : "Poloha je zakázaná. Povol ji v nastavení prohlížeče u tohohle webu.",
            "timeout" => "Polohu se nepodařilo zjistit včas. Zkus to venku nebo za chvíli.",
            "unsupported" => "Tenhle prohlížeč polohu neumí.",
            _ => "Polohu se nepodařilo zjistit."
        };
    }
}
