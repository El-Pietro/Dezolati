# Dezoláti – web (pro iPhone)

Webová verze appky jako PWA na GitHub Pages: **https://el-pietro.github.io/Dezolati/**

- Android appka se kvůli webu nemění. Web si logiku (Firestore, fronta zápisů, statistiky, promile)
  bere ze stejných souborů v `Services/` a `Models/` – jen je nalinkuje (`Shared.props`).
- Co je jen pro telefon, nahrazují náhražky: `Shims/` (Preferences → localStorage, signál, verze)
  a `Web/` (přihlášení přes REST, PushService/Reminders bez oznámení a polohy).
- Android projekt web ignoruje (`DefaultItemExcludes` v `Dezolati.csproj`).

## Fáze
1. ✅ přihlášení, registrace, lístek, zápis, škrtání, moje statistiky, nastavení
2. ✅ kámoši (žádosti, hledání, Možná znáš, kdo pije, připíjení, statistiky kámoše, tajný režim) + oznámení (web push)
3. ✅ mapa hospod (Leaflet + OSM dlaždice, katalog přibalený k webu, kartička hospody i kámošů, profil hospody, úpravy, přidání, oblíbené, filtr, pozvánky, domov) + poznání hospody při zápisu (poloha z prohlížeče)
4. hry a zbytek

## Katalog hospod
- Web nestahuje katalog z GitHub Releases (prohlížeč by ho nepustil) – akce `web.yml` ho při nasazení přibalí
  do `wwwroot/data/hospody-cz.csv.gz` (ve `.gitignore`) a každé pondělí ráno web nasadí znovu s čerstvým.
- Při `dotnet run` u sebe katalog chybí → mapa si hospody dotáhne z Overpassu (pomalejší, ale funguje).

## Oznámení (web push)
- Token prohlížeče jde do `PushTokens` stejně jako token telefonu (`Platform = "Web"`), server (functions) se nemění.
- Konfigurace: `wwwroot/js/push-config.js` (appId webové appky + VAPID klíč z Firebase konzole – obojí veřejné).
- Zprávy přijímá `wwwroot/push-sw.js` (načítá ho service worker), připíjení spojuje do jednoho oznámení.
- iPhone: jen z ikony na ploše, iOS 16.4+, zapíná se tlačítkem v Nastavení. Tlačítka reakcí v oznámení iPhone neumí → ťuk otevře Kámoše.

## Spuštění u sebe
```
cd Dezolati.Web
dotnet run
```
a otevřít adresu, kterou vypíše (http://localhost:…). Přihlašuje se proti ostré databázi.

## Nasazení
Samo: push do `master` se změnou v `Dezolati.Web/`, `Services/` nebo `Models/` → GitHub Action
`.github/workflows/web.yml` web sestaví a nahraje na GitHub Pages (pár minut).
Jednorázově: repo → Settings → Pages → Source: **GitHub Actions**.

## Pozor při úpravách sdílených souborů
Soubory v `Services/` a `Models/`, které web linkuje, nesmí používat nic jen z Androidu
(Geolocation, Permissions, Android.*…) bez `#if ANDROID`. Jinak web nepůjde sestavit
(GitHub Action spadne, Android to neovlivní).
