using System.Globalization;

namespace Dezolati.Services
{
    /// <summary>
    /// Adventní kalendář (Vánoce 2026): 24 okýnek 1.–24. 12. Okýnko otevře zápis toho dne (hospodský den, do 6:00).
    /// Zmeškané okýnko jde zachránit hned další den dvěma trestnými panáky. Pod okýnky příběh „Čert propil peklo“.
    /// Stav počítá server (functions/vanoce.js → Achievements/{uid}.Advent: {"5": "d" | "p"}), telefon okýnko otevře
    /// hned po zápisu sám (ať se nečeká na server) a okno s dílem příběhu ukáže jen jednou.
    /// </summary>
    public static class Advent
    {
        public const int Year = 2026;
        public const string Title = "Čert propil peklo";

        public enum Win { Future, Today, Open, Rescued, Rescue, Missed }

        private const string KeySeen = "advent_seen", KeyLocal = "advent_local", KeyPk = "advent_pk";

        /// <summary>Číslo okýnka pro hospodský den „2026-12-05“ → 5; mimo 1.–24. 12. null.</summary>
        public static int? Number(string day) =>
            day.Length == 10 && day.StartsWith($"{Year}-12-") && int.TryParse(day[8..], out var n) && n is >= 1 and <= 24 ? n : null;

        public static string Day(int n) => $"{Year}-12-{n:00}";

        /// <summary>Kalendář se ukazuje, dokud běží event (od jeho začátku, i před 1. 12.), nebo v náhledu.</summary>
        public static bool Visible => Events.Current != null && (Events.Running || Events.Preview);

        /// <summary>Dnešní den prosince (hospodský den): 0 = před adventem, 25–31 po něm, 99 = v lednu.</summary>
        public static int TodayNumber
        {
            get
            {
                var t = Events.Today;
                if (t < new DateTime(Year, 12, 1)) return 0;
                return t.Year == Year && t.Month == 12 ? t.Day : 99;
            }
        }

        /// <summary>Otevřená okýnka: ze serveru + co telefon otevřel sám (jen u sebe).</summary>
        public static Dictionary<int, string> Opened(BadgeState state, bool mine)
        {
            var open = new Dictionary<int, string>(state.Advent);
            if (mine)
                foreach (var (n, how) in Local())
                    open.TryAdd(n, how);
            return open;
        }

        public static Win StateOf(int n, IReadOnlyDictionary<int, string> open)
        {
            if (open.TryGetValue(n, out var how)) return how == "p" ? Win.Rescued : Win.Open;
            var today = TodayNumber;
            if (n > today) return Win.Future;
            if (n == today) return Win.Today;
            return n == today - 1 ? Win.Rescue : Win.Missed;
        }

        /// <summary>Co říct po ťuknutí na zavřené okýnko.</summary>
        public static string ClosedText(Win w) => w switch
        {
            Win.Future => "Ještě ne. Trpělivost, i čert musel čekat.",
            Win.Today => "Dnes si zapiš aspoň jedno pití a okýnko se otevře.",
            Win.Rescue => "Včera jsi nic nezapsal. Dej si dnes dva trestné panáky a okýnko se otevře dodatečně.",
            _ => "Zmeškáno. Tenhle kousek příběhu si přečte jen ten, kdo ten den pil."
        };

        // ---------------- zápis: otevřít okýnko hned (server to potvrdí) ----------------

        /// <summary>Volá se po každém zápisu pití „teď“ (RecordDrink).</summary>
        public static void OnDrink(string day, int panaky, BadgeState? known)
        {
            if (!Events.Active || !DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date < new DateTime(Year, 12, 1) || date > new DateTime(Year, 12, 25)) return;
            var local = Local();
            if (Number(day) is int today && !local.ContainsKey(today) && known?.Advent.ContainsKey(today) != true)
                local[today] = "d";
            // trestné panáky: dnes aspoň dva → zachrání včerejší okýnko
            if (panaky > 0)
            {
                var pk = PkToday(day) + panaky;
                Preferences.Set(KeyPk, $"{day}:{pk}");
                var prevDay = date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (pk >= 2 && Number(prevDay) is int prev && !local.ContainsKey(prev) && known?.Advent.ContainsKey(prev) != true)
                    local[prev] = "p";
            }
            SaveLocal(local);
        }

        private static int PkToday(string day)
        {
            var raw = Preferences.Get(KeyPk, "");
            return raw.StartsWith(day + ":") && int.TryParse(raw[(day.Length + 1)..], out var c) ? c : 0;
        }

        private static Dictionary<int, string> Local()
        {
            var d = new Dictionary<int, string>();
            foreach (var part in Preferences.Get(KeyLocal, "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (part.Length >= 2 && int.TryParse(part[..^1], out var n)) d[n] = part[^1..];
            return d;
        }

        private static void SaveLocal(Dictionary<int, string> d) =>
            Preferences.Set(KeyLocal, string.Join(",", d.Select(kv => $"{kv.Key}{kv.Value}")));

        /// <summary>
        /// Nově otevřená okýnka, která jsem ještě neviděl (pro okno s dílem příběhu). Každé jen jednou.
        /// </summary>
        public static List<(int N, bool Rescued)> TakeNew(BadgeState? server)
        {
            if (!Events.Active) return new();
            var open = Opened(server ?? BadgeState.Empty, mine: true);
            var seen = Preferences.Get(KeySeen, "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var fresh = open.Where(kv => !seen.Contains(kv.Key.ToString())).OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value == "p")).ToList();
            foreach (var (n, _) in fresh) seen.Add(n.ToString());
            Preferences.Set(KeySeen, string.Join(",", seen));
            return fresh.Count > 3 ? fresh.TakeLast(1).ToList() : fresh;   // po instalaci jen poslední, ne celý román naráz
        }

        public static string WindowTitle(int n, bool rescued) =>
            rescued ? $"Trestné panáky zabraly! Okýnko {n}" : $"Okýnko {n}";

        // ---------------- kresba okýnka (50 × 58, číslo dává platforma) ----------------

        /// <summary>Pořadí okýnek v mřížce – zamíchané jako v pravém kalendáři (pevně, ať se nehýbou).</summary>
        public static readonly int[] Order = { 7, 18, 3, 22, 11, 1, 15, 24, 9, 5, 20, 13, 2, 17, 10, 23, 6, 14, 19, 4, 12, 21, 8, 16 };

        public static List<SceneNode> Door(Win w)
        {
            const double W = 50, H = 58;
            var n = new List<SceneNode>
            {
                new SPath(SceneKit.RoundRect(1, 1, W - 2, H - 2, 4), Fill: "#5A3418", Stroke: "#2F1C0C", StrokeWidth: 1.2)
            };
            switch (w)
            {
                case Win.Open or Win.Rescued:
                    // otevřené: krémový vnitřek s hvězdičkou (zachráněné: panák), dvířka odklopená doleva
                    n.Add(new SPath(SceneKit.RoundRect(5, 5, W - 10, H - 10, 3), Fill: "#FFF4D8"));
                    if (w == Win.Open) n.Add(new SPath(StarPath(W / 2 + 4, H / 2 - 2, 9, 4), Fill: "#E2B23A", Stroke: "#A97E1E", StrokeWidth: 1));
                    else
                    {
                        n.Add(new SPath("M 24 20 L 38 20 L 36 40 L 26 40 Z", Fill: "#F6E7B8", Stroke: "#6B3E1E", StrokeWidth: 1.4));
                        n.Add(new SPath("M 25.6 26 L 36.4 26 L 35.2 38.6 L 26.8 38.6 Z", Fill: "#C9892B", FillOpacity: 0.85));
                    }
                    n.Add(new SPath("M 5 5 L 13 9 L 13 49 L 5 53 Z", Fill: "#B3262E", Stroke: "#7E1219", StrokeWidth: 1));
                    break;
                default:
                    var fill = w switch { Win.Today => "#C9892B", Win.Missed => "#8A7D60", _ => "#B3262E" };
                    n.Add(new SPath(SceneKit.RoundRect(5, 5, W - 10, H - 10, 3), Fill: fill, Stroke: "#2F1C0C", StrokeWidth: 1));
                    n.Add(new SPath(SceneKit.Ellipse(W - 11, H - 13, 2.2, 2.2), Fill: "#E2B23A"));   // klika
                    if (w == Win.Today)
                        n.Add(new SPath(SceneKit.RoundRect(2.5, 2.5, W - 5, H - 5, 4), Stroke: "#F2C94C", StrokeWidth: 2.4));
                    if (w == Win.Rescue)
                        n.Add(new SPath(SceneKit.RoundRect(2.5, 2.5, W - 5, H - 5, 4), Stroke: "#F2C94C", StrokeWidth: 1.6, StrokeOpacity: 0.7));
                    if (w == Win.Missed)
                        n.Add(new SPath("M 12 14 L 38 44 M 38 14 L 12 44", Stroke: "#5A3418", StrokeWidth: 2, StrokeOpacity: 0.6, Round: true));
                    break;
            }
            return n;
        }

        private static string StarPath(double cx, double cy, double r1, double r2)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < 10; i++)
            {
                var r = i % 2 == 0 ? r1 : r2;
                var a = -Math.PI / 2 + i * Math.PI / 5;
                sb.Append(i == 0 ? "M " : " L ").Append($"{SceneKit.F(cx + Math.Cos(a) * r)} {SceneKit.F(cy + Math.Sin(a) * r)}");
            }
            return sb.Append(" Z").ToString();
        }

        /// <summary>Číslo na okýnku: u otevřených vlevo dole na dvířkách není místo → do rohu.</summary>
        public static bool NumberInCorner(Win w) => w is Win.Open or Win.Rescued;

        // ---------------- příběh ----------------

        public static string Story(int n) => n is >= 1 and <= 24 ? Parts[n - 1] : "";

        private static readonly string[] Parts =
        {
            // 1
            "V pekle bylo prvního prosince nezvykle chladno. Lucifer poslal čerta Vincka nahoru na svět pro uhlí, dal mu měšec a jedinou radu: „Hlavně si nikde nesedej.“ Vincek kývl. Pak uviděl hospodu U Zeleného sudu.",
            // 2
            "„Na jedno,“ řekl si Vincek, a protože čerti neumějí počítat do jedné, objednal si rovnou čtyři. Hostinský Pepa je natočil bez mrknutí oka – za třicet let za pípou viděl horší zákazníky než chlapa s kopyty. U vedlejšího stolu se zrovna rozdávaly karty.",
            // 3
            "Mariáš hráli tři: hostinský Pepa, šedovousý děda v kožichu a bledý mladík, který pil jen malinovku. „Chybí nám čtvrtý,“ řekl děda a podíval se na Vincka, jako by o něm věděl úplně všechno. Vincek si přisedl. Luciferova rada mu došla až o tři hodiny později.",
            // 4
            "Nejdřív prohrál měšec na uhlí. Pak rohy, které mu Pepa laskavě půjčil zpátky na dluh. A pak, když hlásil sedmu a myslel si, že má trumfy, vsadil to jediné, co mu zbylo: „Vsadím peklo.“ Pepa vynesl. Vincek trumfy neměl.",
            // 5
            "Ráno se ukázalo, kdo byl děda v kožichu: Mikuláš. „Peklo propité v hospodě, to jsem za šestnáct set let ještě neviděl,“ smál se a dopil svařák. Bledý mladík s malinovkou byl samozřejmě anděl a zapsal si to do knihy hříchů. Dvakrát podtržené.",
            // 6
            "Pepa měl teď v kapse listinu na peklo s Vinckovým podpisem z pivní pěny. „Vrátím ti ho,“ řekl, „ale ne zadarmo. Do Štědrého dne mi přineseš tři věci.“ Vincek polkl. Pepa vzal tácek a začal psát.",
            // 7
            "Na tácku stálo: 1. Svařák, po kterém nebolí hlava. 2. Hospodský, který nikdy nezavírá. 3. Štamgast, který zaplatí celou útratu a nebude u toho reptat. Vincek si poprvé v životě pomyslel, že peklo bylo vlastně docela příjemné místo.",
            // 8
            "Mezitím v pekle vyhasly poslední kotle, protože uhlí nikdo nepřivezl. Hříšníci se po staletích přestali potit a začali si stěžovat na zimu. Lucifer poslal nahoru telegram o jediném slově: „VINCKU.“",
            // 9
            "Svařák bez bolení hlavy hledal Vincek nejdřív na vánočních trzích. Ochutnal jich jedenáct a hlava ho bolela po každém víc. Prodavačka u posledního stánku mu poradila, ať to zkusí u babky kořenářky za lesem.",
            // 10
            "Babka kořenářka se nad čertem ve dveřích ani nepozastavila. „Svařák, po kterém nebolí hlava? Jednoduché, synku. Musí se vypít celý hned, ať nic nezbyde na ráno.“ Vincek té logice nerozuměl, ale recept si opsal.",
            // 11
            "Recept zněl: hřebíček, skořice, pomeranč, med a jedna kapka z pekelného kotle, „aby to hřálo“. Jenže kotle vyhasly. Vincek musel vymyslet, jak je zatopit, když nemá uhlí a peklo mu ani nepatří.",
            // 12
            "Napadlo ho to v hospodě, kde jinde. Štamgasti U Zeleného sudu se tak hádali o politice, že se nad stolem kouřilo. Vincek nastavil pytel, nachytal do něj trochu té horké řeči a odnesl ji dolů. Kotel chytl napoprvé.",
            // 13
            "Na Lucii stála v pekelné kuchyni Lucie a cedila svařák. „Lucie noci upije,“ zvedla hrnek, „ale tohle upije Lucie.“ Svařák voněl, hřál a ráno nikoho nebolela hlava. Bod jedna byl splněný.",
            // 14
            "Bod dva: hospodský, který nikdy nezavírá. Vincek obešel celý kraj. Všude se zavíralo o desáté, v neděli dřív a o svátcích se neotvíralo vůbec. Jediná hospoda bez zavírací doby byla ta jeho – v pekle. Jenže ta teď patřila Pepovi.",
            // 15
            "Zkusil podplatit ponocného, ať jedné hospodě schová hodiny. Ponocný to vzal za dvě piva a hodiny schoval tak dobře, že je dodnes nikdo nenašel. Hospodský přesto zavřel. „Mám to v nohách,“ řekl, „na to hodiny nepotřebuju.“",
            // 16
            "Anděl s malinovkou mezitím chodil všude za ním a zapisoval. „Jen pro pořádek,“ usmál se, „za každý podvod si čert odsedí sto let navíc.“ Vincek nenápadně vytáhl z rukávu cinknuté karty a začal to zkoušet poctivě.",
            // 17
            "Poctivě to znamenalo zeptat se. Ptal se všech hospodských v kraji, jestli by nechtěli čepovat napořád. Všichni odpověděli stejně: „A kdy bych spal?“ Jen jeden starý výčepní zamyšleně utřel sklenici a řekl: „Čert přece nikdy nespí, ne?“",
            // 18
            "Ta věta mu vrtala hlavou celý den. Čert nikdy nespí. Čert nikdy nezavírá. Hospodský, který nikdy nezavírá, musí být čert. „To jsem já,“ došlo mu konečně. Bod dva měl celou dobu v zrcadle.",
            // 19
            "Zbýval bod tři, nejtěžší ze všech: štamgast, který zaplatí celou útratu a nebude reptat. Vincek prošel všechny hospody a viděl tisíc štamgastů. Platit chtěli všichni. Až příště.",
            // 20
            "Mikuláš, už zase v civilu, mu u piva poradil: „Takových je málo. Znám jednoho, ale ten chodí jen jednou za rok a nosí dárky.“ Vincek nadšeně vyskočil. Mikuláš ho chytil za ocas: „Já to nejsem. Já mám po sezóně.“",
            // 21
            "Nejkratší den v roce strávil Vincek nejdelší dobou u jednoho stolu: sčítal útratu, kterou U Zeleného sudu za třicet let nikdo nezaplatil. Vyšlo mu číslo, které se nevešlo na tácek, a tak ho dopsal na stůl. A pak ještě kousek na zeď.",
            // 22
            "Večer si k němu přisedl anděl a poprvé si objednal pivo místo malinovky. „Zapsal jsem si každý tvůj krok,“ řekl, „a nejde mi to dohromady. Na čerta jsi byl celý advent podezřele poctivý.“ Pak zaplatil celou útratu ze stolu i ze zdi. A ani nehlesl.",
            // 23
            "Den před Štědrým dnem plavali U Zeleného sudu v kádi kapři a výčep praskal ve švech. Vincek položil na pult hrnek svařáku, vizitku „hospodský, nikdy nezavírá“ a účtenku zaplacenou andělem. Pepa dlouho mlčel. Pak vytáhl listinu na peklo a položil ji vedle pípy.",
            // 24
            "„Peklo si vezmi zpátky,“ řekl Pepa, „ale od dneška se v něm čepuje moje pivo.“ A tak je v pekle odjakživa teplo, nikdy se tam nezavírá a mariáš se hraje jen o drobné. Kdo celý advent poctivě zapisoval, má tam štamgastský stůl. Veselé Vánoce!"
        };
    }
}
