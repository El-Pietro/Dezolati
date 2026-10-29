# Dezoláti – web (pro iPhone)

Webová verze appky jako PWA na GitHub Pages: **https://el-pietro.github.io/Dezolati/**

- Android appka se kvůli webu nemění. Web si logiku (Firestore, fronta zápisů, statistiky, promile)
  bere ze stejných souborů v `Services/` a `Models/` – jen je nalinkuje (`Shared.props`).
- Co je jen pro telefon, nahrazují náhražky: `Shims/` (Preferences → localStorage, signál, verze)
  a `Web/` (přihlášení přes REST, PushService/Reminders bez oznámení a polohy).
- Android projekt web ignoruje (`DefaultItemExcludes` v `Dezolati.csproj`).

## Fáze
1. ✅ přihlášení, registrace, lístek, zápis, škrtání, moje statistiky, nastavení
2. kámoši + oznámení (web push)
3. mapa hospod
4. hry a zbytek

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
