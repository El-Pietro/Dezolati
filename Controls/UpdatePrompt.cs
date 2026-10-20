using Dezolati.Services;

namespace Dezolati.Controls
{
    /// <summary>Nabídka nové verze na lístku + stažení a spuštění instalace.</summary>
    public static class UpdatePrompt
    {
        private static AppRelease? _pending;   // nalezená, ale ještě nenainstalovaná verze
        private static bool _busy;

        /// <summary>Při otevření lístku: zkontrolovat GitHub (max. jednou za 30 min) a případně nabídnout.</summary>
        public static async Task CheckAndOfferAsync(Page page)
        {
            if (_busy || !AppUpdater.IsSupported) return;
            var release = _pending ?? await AppUpdater.CheckAsync();
            if (release == null) return;
            _pending = release;
            await OfferAsync(page, release);
        }

        /// <summary>Po návratu do appky (třeba z nastavení povolení instalace) nabídnout znovu.</summary>
        public static async Task ResumeIfPendingAsync()
        {
            if (_pending == null || _busy) return;
            if (Shell.Current?.CurrentPage is Page page)
                await OfferAsync(page, _pending);
        }

        public static async Task OfferAsync(Page page, AppRelease release)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                _pending = release;
                var notes = release.Notes.Length > 400 ? release.Notes[..400] + "…" : release.Notes;
                var message = $"Teď máš {AppUpdater.CurrentVersionText}.";
                if (notes.Length > 0) message += "\n\n" + notes;
                var size = release.ApkSize > 0 ? $" ({Math.Round(release.ApkSize / 1048576.0)} MB)" : "";

                const string later = "Později";
                var choice = await Listek.ShowAsync(page, $"Vyšla verze {release.Version}", message,
                    ($"Aktualizovat{size}", PencilVariant.Primary),
                    (later, PencilVariant.Secondary));

                if (choice == null || choice == later)
                {
                    AppUpdater.Snooze(release);
                    _pending = null;
                    return;
                }

                await DownloadAndInstallAsync(page, release);
            }
            finally
            {
                _busy = false;
            }
        }

        private static async Task DownloadAndInstallAsync(Page page, AppRelease release)
        {
            // Android se jednou zeptá, jestli Dezoláti smí instalovat aplikace
            if (!AppUpdater.EnsureInstallPermission())
            {
                await Listek.AlertAsync(page, "Ještě jedno povolení",
                    "Zapni pro Dezoláti „Povolit z tohoto zdroje“ a vrať se zpátky – aktualizace se nabídne znovu.");
                return; // _pending zůstává → ResumeIfPendingAsync
            }

            string path;
            var busy = Listek.ShowBusy(page, $"Stahuju verzi {release.Version}", "0 %");
            try
            {
                path = await AppUpdater.DownloadAsync(release,
                    new Progress<double>(p => busy?.SetText($"{Math.Round(p * 100)} %")));
            }
            catch (Exception ex)
            {
                busy?.Dispose();
                await Listek.AlertAsync(page, "Nepovedlo se", $"Stažení selhalo: {ex.Message}");
                return;
            }
            busy?.Dispose();

            _pending = null;
            try
            {
                await AppUpdater.InstallAsync(path);   // dál už instalátor Androidu
            }
            catch (Exception ex)
            {
                await Listek.AlertAsync(page, "Nepovedlo se", $"Instalaci se nepodařilo spustit: {ex.Message}");
            }
        }
    }
}
