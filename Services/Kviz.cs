using System.Text.Json;

namespace Dezolati.Services
{
    /// <summary>Jedna otázka kvízu: správná odpověď + tři špatné, po odpovědi se ukáže poznámka.</summary>
    public sealed record QuizQuestion(int Id, string Category, string Text, string Correct, string[] Wrong, string Note);

    /// <summary>
    /// Hospodský kvíz – kolem stolu na jednom telefonu (logika sdílená appkou a webem).
    /// Hráči se střídají, každý dostane svou otázku, 20 s na odpověď. Otázky se neopakují,
    /// dokud se neprojdou všechny (pamatuje si to telefon / prohlížeč).
    /// </summary>
    public sealed class QuizGame
    {
        public const string PlayersKey = "hry_hraci";      // stejný seznam jako v Hrách v appce
        private const string SeenKey = "hry_kviz_videne";
        public const double TimeLimit = 20;

        public enum Phase { Setup, Question, Answered, Done }

        public Phase State { get; private set; } = Phase.Setup;
        public List<string> Players { get; } = new();
        public int[] Scores { get; private set; } = Array.Empty<int>();
        public int RoundsPerPlayer { get; set; } = 5;
        /// <summary>Okruhy otázek (Pivo / Víno / Tvrdej, libovolně kombinované).</summary>
        public List<string> Okruhy { get; set; } = new() { "Pivo" };
        public List<QuizQuestion> Deck { get; } = new();
        public int Index { get; private set; }
        public QuizQuestion? Current => Index < Deck.Count ? Deck[Index] : null;
        public string[] Options { get; private set; } = Array.Empty<string>();
        public int? Picked { get; private set; }
        public bool LastCorrect { get; private set; }
        public double TimeLeft { get; private set; }
        public int CurrentPlayer => Players.Count == 0 ? 0 : Index % Players.Count;
        public string CurrentPlayerName => Players.Count == 0 ? "" : Players[CurrentPlayer];
        private readonly Random rnd = new();

        public static List<string> LoadPlayers()
        {
            try { return JsonSerializer.Deserialize<List<string>>(Preferences.Get(PlayersKey, "[]")) ?? new(); }
            catch { return new(); }
        }

        public static void SavePlayers(List<string> players) =>
            Preferences.Set(PlayersKey, JsonSerializer.Serialize(players));

        private const string OkruhyKey = "hry_kviz_okruhy";

        /// <summary>Naposledy zvolené okruhy (aspoň jeden; poprvé pivo).</summary>
        public static List<string> LoadOkruhy()
        {
            try
            {
                var list = (JsonSerializer.Deserialize<List<string>>(Preferences.Get(OkruhyKey, "[]")) ?? new())
                    .Where(o => QuizBank.Okruhy.Contains(o)).Distinct().ToList();
                return list.Count > 0 ? list : new() { "Pivo" };
            }
            catch { return new() { "Pivo" }; }
        }

        public static void SaveOkruhy(List<string> okruhy) =>
            Preferences.Set(OkruhyKey, JsonSerializer.Serialize(okruhy));

        public void Start(IEnumerable<string> players)
        {
            Players.Clear();
            Players.AddRange(players.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
            if (Players.Count == 0) Players.Add("Ty");
            Scores = new int[Players.Count];
            Deck.Clear();
            Deck.AddRange(Pick(Players.Count * RoundsPerPlayer));
            Index = 0;
            Ask();
        }

        /// <summary>
        /// Vybere otázky z daných okruhů, které hráči v tomhle zařízení ještě neviděli. Když v okruzích dojdou,
        /// zapomenou se jen ty z vybraných okruhů (ostatní okruhy si „viděné“ drží dál).
        /// </summary>
        private List<QuizQuestion> Pick(int count)
        {
            HashSet<int> seen;
            try { seen = JsonSerializer.Deserialize<HashSet<int>>(Preferences.Get(SeenKey, "[]")) ?? new(); }
            catch { seen = new(); }
            var pool = QuizBank.For(Okruhy);
            var fresh = pool.Where(q => !seen.Contains(q.Id)).OrderBy(_ => rnd.Next()).ToList();
            if (fresh.Count < count)
            {
                foreach (var q in pool) seen.Remove(q.Id);
                fresh.AddRange(pool.Where(q => fresh.All(f => f.Id != q.Id)).OrderBy(_ => rnd.Next()));
            }
            var picked = fresh.Take(Math.Min(count, pool.Count)).ToList();
            foreach (var q in picked) seen.Add(q.Id);
            Preferences.Set(SeenKey, JsonSerializer.Serialize(seen));
            return picked;
        }

        private void Ask()
        {
            if (Current is not { } q) { State = Phase.Done; return; }
            Options = q.Wrong.Append(q.Correct).OrderBy(_ => rnd.Next()).ToArray();
            Picked = null;
            TimeLeft = TimeLimit;
            State = Phase.Question;
        }

        public int CorrectIndex => Current is { } q ? Array.IndexOf(Options, q.Correct) : -1;

        public void Answer(int option)
        {
            if (State != Phase.Question || Current is not { } q) return;
            Picked = option;
            LastCorrect = option >= 0 && option < Options.Length && Options[option] == q.Correct;
            if (LastCorrect) Scores[CurrentPlayer]++;
            State = Phase.Answered;
        }

        public void Step(double dt)
        {
            if (State != Phase.Question) return;
            TimeLeft -= dt;
            if (TimeLeft <= 0) { TimeLeft = 0; Answer(-1); }
        }

        public void Next()
        {
            if (State != Phase.Answered) return;
            Index++;
            if (Index >= Deck.Count) State = Phase.Done;
            else Ask();
        }

        public string Ranking()
        {
            var order = Players.Select((p, i) => (p, s: Scores[i])).OrderByDescending(x => x.s).ToList();
            var lines = order.Select((x, i) => $"{i + 1}. {x.p} – {x.s} z {RoundsPerPlayer}").ToList();
            if (Players.Count > 1)
            {
                var worst = order.Last();
                lines.Add(order.Count(x => x.s == worst.s) > 1 && order.First().s == worst.s
                    ? "Remíza. Rundu platí ten, kdo navrhl kvíz."
                    : $"{worst.p} platí rundu.");
            }
            else
            {
                var s = order[0].s;
                lines.Add(s >= RoundsPerPlayer ? "Plný počet. Štamgast s titulem." : s >= RoundsPerPlayer * 0.6 ? "Ujde to, ale výčepní tě ještě nezná jménem." : "Doporučuju víc chodit do hospody. Studijně.");
            }
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Sto otázek. Každá ověřená proti zdroji (31. 10. 2026) – viz poznámka u otázky:
    /// Wikipedie (Beer in the Czech Republic, Pilsner Urquell Brewery, Staropramen Brewery, Royal Brewery of Krušovice,
    /// Velkopopovický Kozel, Holba, Radegast, Postřižinské, Cutting It Short, Gambrinus, Humulus lupulus, Reinheitsgebot,
    /// Oktoberfest, Maß, Guinness Brewery, Kölsch, Lambic, Saccharomyces pastorianus, Shandy, International Beer Day,
    /// S. P. L. Sørensen), finance.cz (kdo vlastní pivovary), kudyznudy.cz (Žatecký chmel), jiznicechy.cz (Regent),
    /// infoglobe.cz (Černá Hora), ceskenoviny.cz (Primátor), radio.cz (U Zlatého tygra, pivní slovník, U Kalicha),
    /// ub.vscht.cz a The Oxford Companion to Beer / beerandbrewing.com (Balling, Stella Artois, trapisté, světlem
    /// poškozené pivo), carlsberg.com, worldhistory.org (Ninkasi), quaritch.com (Pasteur), mentalfloss.com (pivní styly
    /// měst), craftbeer.com a beerspa.com (hladinka, šnyt, mlíko, Lukr), brewery.org (dekokce), epravo.cz (nealko pivo).
    /// </summary>
    public static class QuizBank
    {
        private static int _id;
        private static QuizQuestion Q(string cat, string text, string correct, string w1, string w2, string w3, string note) =>
            new(++_id, cat, text, correct, new[] { w1, w2, w3 }, note);

        private const string Cz = "České pivo", Br = "Pivovary a značky", Hist = "Historie", Svet = "Pivo ve světě",
            Vyroba = "Výroba piva", Hosp = "Hospoda";

        // Okruhy (od 3.12): Pivo = původních 120 otázek, Víno, Tvrdej. Pořadí seznamů se nesmí měnit –
        // Id otázek jde po sobě a telefon si podle něj pamatuje, co už padlo.
        public static readonly List<QuizQuestion> Pivo = new()
        {
            // ---------------- historie ----------------
            Q(Hist, "Kde je v Česku poprvé doloženo vaření piva?", "V Břevnovském klášteře", "Na Vyšehradě", "Ve Strahovském klášteře", "V Plzni",
                "Benediktini v Břevnově vařili pivo už v roce 993."),
            Q(Hist, "Ve kterém roce je doloženo vaření piva v Břevnovském klášteře?", "993", "1088", "1295", "1499",
                "Je to nejstarší záznam o vaření piva v českých zemích."),
            Q(Hist, "Z kterého roku pocházejí první údaje o pěstování chmele na českém území?", "859", "993", "1088", "1348",
                "Chmel se tu pěstoval dřív, než je doloženo vaření piva v Břevnově."),
            Q(Hist, "Který panovník daroval roku 1088 vyšehradské kapitule chmelový desátek na vaření piva?", "Vratislav II.", "Karel IV.", "Václav II.", "Přemysl Otakar II.",
                "Vratislav II. byl prvním českým králem."),
            Q(Hist, "Kdy byl založen pražský pivovar U Fleků?", "1499", "1620", "1795", "1842",
                "Pivovar a pivnice U Fleků funguje přes pět set let."),
            Q(Hist, "Ve kterém roce se v Plzni uvařil první světlý ležák?", "1842", "1795", "1869", "1895",
                "První várka proběhla 5. října 1842."),
            Q(Hist, "Kdo uvařil první plzeňský ležák?", "Josef Groll", "Karel Balling", "František Ondřej Poupě", "Anton Dreher",
                "Groll byl bavorský sládek najatý plzeňskými měšťany."),
            Q(Hist, "Odkud pocházel sládek Josef Groll?", "Z Bavorska", "Z Rakouska", "Ze Saska", "Z Moravy",
                "Bavorský sládek přinesl do Plzně spodní kvašení."),
            Q(Hist, "Kdy plzeňští měšťané rozhodli o založení Měšťanského pivovaru?", "1839", "1795", "1848", "1869",
                "Stavělo se od září 1839 do podzimu 1840, první várka byla v roce 1842."),
            Q(Hist, "Kdy byla zaregistrována ochranná známka Pilsner Urquell?", "1898", "1842", "1918", "1948",
                "Urquell znamená „prazdroj“ – původní zdroj plzeňského piva."),
            Q(Hist, "Kdy byl založen Budějovický měšťanský pivovar (dnes Samson)?", "1795", "1842", "1895", "1918",
                "Je o sto let starší než Budvar."),
            Q(Hist, "Kdy byl založen Budějovický Budvar?", "1895", "1795", "1842", "1948",
                "Budějovice měly pivovar už ve 13. století."),
            Q(Hist, "Od kterého roku vaří americký Anheuser-Busch pivo Budweiser?", "1876", "1795", "1895", "1933",
                "O jméno Budweiser se s Budvarem soudí už dlouhá desetiletí."),
            Q(Hist, "Kdy byl založen smíchovský pivovar Staropramen?", "1869", "1842", "1895", "1918",
                "Staropramen stojí na pražském Smíchově."),
            Q(Hist, "Který panovník koupil v roce 1583 pivovar v Krušovicích?", "Rudolf II.", "Karel IV.", "Josef II.", "Marie Terezie",
                "Proto má Krušovice v logu korunu."),
            Q(Hist, "Kdy byl založen pivovar Krušovice?", "1581", "1379", "1499", "1842",
                "Už o dva roky později ho koupil císař Rudolf II."),
            Q(Hist, "Ke kterému roku se hlásí počátek pivovaru v Třeboni (Regent)?", "1379", "1499", "1581", "1842",
                "Patří k nejstarším fungujícím pivovarům v Česku."),
            Q(Hist, "Odkdy je doloženo vaření piva v Černé Hoře?", "Od roku 1530", "Od roku 1842", "Od roku 1918", "Od roku 1970",
                "Za založení pivovaru se považuje dokonce rok 1298."),
            Q(Hist, "Kdy začal pivovar Gambrinus (První akciový pivovar) v Plzni vařit stejnojmenný ležák?", "1918", "1842", "1869", "1948",
                "V roce 1932 se První akciový pivovar spojil s Prazdrojem."),
            Q(Hist, "Kdy se poprvé uvařil Velkopopovický Kozel?", "1874", "1842", "1918", "1970",
                "Pivovar ve Velkých Popovicích postavili v roce 1875."),

            // ---------------- pivovary a značky ----------------
            Q(Br, "Kde se vaří Radegast?", "V Nošovicích", "Ve Frýdku-Místku", "V Ostravě", "V Třinci",
                "Pivovar v Nošovicích vaří od roku 1970."),
            Q(Br, "Od kterého roku se vaří pivo v Nošovicích (Radegast)?", "1970", "1874", "1918", "1992",
                "Na české poměry je to mladý pivovar."),
            Q(Br, "Po kom je pojmenované pivo Radegast?", "Po slovanském bohu", "Po zakladateli pivovaru", "Po keltském náčelníkovi", "Po hoře v Alpách",
                "Socha boha Radegasta stojí na Radhošti."),
            Q(Br, "Kde se vaří Velkopopovický Kozel?", "Ve Velkých Popovicích", "V Benešově", "V Plzni", "V Říčanech",
                "Velké Popovice leží asi 25 km jihovýchodně od Prahy."),
            Q(Br, "Jak se jmenují živí kozlové chovaní v pivovaru Velké Popovice?", "Olda", "Franta", "Pepa", "Karel",
                "Od 70. let dostává každý kozel jméno po svém prvním ošetřovateli Oldovi."),
            Q(Br, "Kde se vaří Zubr?", "V Přerově", "V Olomouci", "V Prostějově", "V Kroměříži",
                "Zubr patří do skupiny Pivovary CZ Group spolu s Holbou a Litovlí."),
            Q(Br, "Kde se vaří Holba?", "V Hanušovicích", "V Šumperku", "V Jeseníku", "V Zábřehu",
                "Pivovar v Hanušovicích byl založen v roce 1874."),
            Q(Br, "Kde se vaří Bernard?", "V Humpolci", "V Pelhřimově", "V Jihlavě", "V Havlíčkově Brodě",
                "Humpolec leží na Vysočině."),
            Q(Br, "Kde se vaří Platan?", "V Protivíně", "V Písku", "Ve Strakonicích", "Ve Vodňanech",
                "Protivínský pivovar patří do skupiny Lobkowicz."),
            Q(Br, "Kde se vaří Rychtář?", "V Hlinsku", "V Chrudimi", "V Poličce", "Ve Žďáru nad Sázavou",
                "Hlinsko leží na pomezí Vysočiny a Pardubického kraje."),
            Q(Br, "Kde se vaří Ježek?", "V Jihlavě", "V Třebíči", "V Telči", "V Pelhřimově",
                "Jihlavský ježek je i ve znaku města."),
            Q(Br, "Kde se vaří Zlatopramen a Březňák?", "Ve Velkém Březně", "V Ústí nad Labem", "V Děčíně", "V Teplicích",
                "Velké Březno leží na severu Čech u Labe."),
            Q(Br, "Ve které pražské čtvrti stojí pivovar Staropramen?", "Na Smíchově", "V Braníku", "V Holešovicích", "Na Žižkově",
                "Smíchovský pivovar vaří od roku 1869."),
            Q(Br, "Kde se vaří Primátor?", "V Náchodě", "V Hradci Králové", "V Trutnově", "V Jaroměři",
                "Do roku 2009 patřil náchodský pivovar městu."),
            Q(Br, "Ve kterém městě stojí pivovar Regent?", "V Třeboni", "V Jindřichově Hradci", "V Českém Krumlově", "V Táboře",
                "Už roku 1367 tu měl pivovar augustiniánský klášter."),
            Q(Br, "Kde se vaří Postřižinské pivo?", "V Nymburce", "V Kolíně", "V Poděbradech", "V Mladé Boleslavi",
                "Jméno je podle Hrabalovy knihy Postřižiny."),
            Q(Br, "Kde stojí pivovar Černá Hora?", "Mezi Tišnovem a Blanskem", "U Jihlavy", "U Opavy", "U Klatov",
                "Pivovar leží na staré obchodní cestě z Tišnova do Blanska."),
            Q(Br, "Kdo vlastní Budějovický Budvar?", "Český stát", "Japonská Asahi", "Nizozemský Heineken", "Molson Coors",
                "Budvar je národní podnik pod ministerstvem zemědělství."),
            Q(Br, "Kdo vlastní Plzeňský Prazdroj od roku 2017?", "Japonská Asahi", "Nizozemský Heineken", "Dánský Carlsberg", "Český stát",
                "Předtím (1999–2017) patřil do skupiny SABMiller."),
            Q(Br, "Do které skupiny patřil Plzeňský Prazdroj v letech 1999–2017?", "SABMiller", "Heineken", "Carlsberg", "Molson Coors",
                "Po převzetí SABMilleru firmou AB InBev se Prazdroj prodal Asahi."),
            Q(Br, "Které pivo NEPATŘÍ do skupiny Plzeňský Prazdroj?", "Starobrno", "Gambrinus", "Radegast", "Velkopopovický Kozel",
                "Starobrno patří Heinekenu."),
            Q(Br, "Které pivo patří do skupiny Staropramen?", "Braník", "Krušovice", "Platan", "Holba",
                "Ke Staropramenu patří i Velvet a ostravský Ostravar."),
            Q(Br, "Jak se jmenuje nealkoholické pivo Plzeňského Prazdroje?", "Birell", "Clausthaler", "Free", "Bavaria 0.0",
                "Birell se vaří v Plzni spolu s Pilsner Urquell a Gambrinusem."),
            Q(Br, "Kdo vlastní Krušovice?", "Heineken", "Asahi", "Molson Coors", "Český stát",
                "Heineken koupil Krušovice v roce 2007."),
            Q(Br, "Kde je Pivovarské muzeum se středověkou sladovnou a ledovým sklepem?", "V Plzni", "V Praze", "V Českých Budějovicích", "V Žatci",
                "Muzeum je ve středověkém sladovnickém domě kousek od Prazdroje."),
            Q(Br, "Co je Gambrinus podle legendy?", "Bájný král či vévoda piva", "Svatý patron sládků", "Římský bůh vína", "Plzeňský sládek",
                "Často se mylně označuje za svatého, ale svatý není."),
            Q(Br, "Pod jakým jménem se Budvar prodává v USA?", "Czechvar", "Budweiser", "Bohemian Bud", "Budejovice Lager",
                "Jméno Budweiser tam patří Anheuser-Buschi."),
            Q(Br, "Které pivo vaří Budvar kromě Budweiser Budvar?", "Pardál", "Braník", "Bernard", "Holba",
                "Pardál vaří Budvar v Českých Budějovicích."),

            // ---------------- české pivo ----------------
            Q(Cz, "Která země má nejvyšší spotřebu piva na obyvatele na světě?", "Česko", "Německo", "Irsko", "Belgie",
                "I když spotřeba v posledních letech klesá, Česko pořád vede."),
            Q(Cz, "Kolik alkoholu má zhruba klasická dvanáctka?", "Asi 5 %", "Asi 12 %", "Asi 3 %", "Asi 8 %",
                "Stupně nejsou procenta alkoholu, ale cukernatost mladiny."),
            Q(Cz, "Kolik alkoholu má zhruba desítka?", "Asi 4 %", "Asi 10 %", "Asi 2 %", "Asi 6,5 %",
                "Šestnáctka mívá zhruba 6,5 %."),
            Q(Cz, "Jakou stupňovitost má podle zákona výčepní pivo?", "8–10 °", "11–12,99 °", "Nad 13 °", "Pod 6 °",
                "Pod 8 ° je lehké pivo."),
            Q(Cz, "Jakou stupňovitost má podle zákona ležák?", "11–12,99 °", "8–10 °", "Nad 13 °", "Přesně 12 °",
                "Ležák tedy může být jedenáctka i dvanáctka."),
            Q(Cz, "Nad kolik stupňů je pivo speciál?", "Nad 13 °", "Nad 11 °", "Nad 16 °", "Nad 20 °",
                "Speciálem je třeba silná čtrnáctka."),
            Q(Cz, "Kolik alkoholu smí mít nealkoholické pivo podle české vyhlášky?", "Nejvýš 0,5 %", "Přesně 0,0 %", "Nejvýš 1,2 %", "Nejvýš 2 %",
                "Nealko proto není vždycky úplně bez alkoholu."),
            Q(Cz, "Co měří Ballingova stupnice (°Bg)?", "Kolik cukru je v mladině", "Hořkost piva", "Barvu piva", "Obsah oxidu uhličitého",
                "Z cukernatosti mladiny se dá odhadnout síla piva."),
            Q(Cz, "Kdo vymyslel stupnici, podle které se u nás pivu říká desítka či dvanáctka?", "Karel Balling", "Josef Groll", "Jan Evangelista Purkyně", "František Ondřej Poupě",
                "Balling sestavil své tabulky v roce 1843."),
            Q(Cz, "Kde působil chemik Karel Balling?", "Na pražské polytechnice", "V Plzeňském pivovaru", "Na univerzitě ve Vídni", "V Žatci",
                "Balling (1805–1868) převzal na pražské polytechnice výuku chemie."),
            Q(Cz, "Saaz je německý název kterého města?", "Žatce", "Slaného", "Úštěku", "Loun",
                "Proto se žateckému chmelu ve světě říká Saaz."),
            Q(Cz, "Kdy byla Žatecká chmelařská krajina zapsána na seznam UNESCO?", "2023", "1992", "2003", "2015",
                "Zapsán byl Žatec a krajina žateckého chmele."),
            Q(Cz, "Která odrůda je „králem“ českých chmelů?", "Žatecký poloraný červeňák", "Sládek", "Premiant", "Agnus",
                "Ostatní jsou také české odrůdy, ale červeňák je nejslavnější."),
            Q(Cz, "Odkdy má žatecký chmel chráněné označení původu?", "Od roku 2007", "Od roku 1993", "Od roku 2016", "Od roku 1950",
                "Žatecký chmel se smí pěstovat jen ve vymezené oblasti."),
            Q(Cz, "Kdy se sklízí český červeňák?", "Ve druhé polovině srpna", "V dubnu", "V červnu", "V listopadu",
                "Česací stroje jedou na Žatecku na konci léta."),
            Q(Cz, "Jaký podíl světového piva dnes tvoří světlé ležáky?", "Víc než dvě třetiny", "Asi desetina", "Asi čtvrtina", "Asi třetina",
                "První světlý ležák vznikl v Plzni v roce 1842."),

            // ---------------- výroba piva ----------------
            Q(Vyroba, "Do které čeledi rostlin patří chmel?", "Konopovité", "Růžovité", "Lilkovité", "Brukvovité",
                "Chmel je příbuzný konopí – sdílejí asi 73 % genomu."),
            Q(Vyroba, "Které rostliny chmele dávají hlávky pro pivovary?", "Jen samičí", "Jen samčí", "Obě stejně", "Jen mladé rostliny do 3 let",
                "Samčí a samičí květy rostou na různých rostlinách."),
            Q(Vyroba, "Do jaké výšky může chmel dorůst?", "Až 10 metrů", "Nejvýš 2 metry", "Asi 50 centimetrů", "Až 30 metrů",
                "Chmel je popínavá trvalka a dožívá se až 20 let."),
            Q(Vyroba, "Jak se jmenují kvasinky, které kvasí ležáky?", "Saccharomyces pastorianus", "Saccharomyces boulardii", "Candida albicans", "Lactobacillus brevis",
                "Je to spodní kvasinka – po kvašení klesá ke dnu."),
            Q(Vyroba, "Jak se kvasí plzeňský ležák?", "Spodně", "Svrchně", "Spontánně", "Vůbec ne",
                "Spodní kvašení přinesl do Plzně bavorský sládek Groll."),
            Q(Vyroba, "Proč se pivo plní do hnědých lahví?", "Chrání ho před světlem", "Je to levnější sklo", "Pivo v nich rychleji vychladne", "Kvůli zákonu",
                "Světlo s chmelovými látkami vytvoří látku, která páchne jako skunk."),
            Q(Vyroba, "Jak páchne pivo, které dlouho stálo na světle?", "Jako skunk", "Jako máslo", "Jako ocet", "Jako mokrý papír",
                "Vzniká látka MBT, chemicky podobná skunkímu sekretu. Nejvíc chrání hnědé sklo, plechovka nebo sud."),
            Q(Vyroba, "Kolik rmutu se při dekokci tradičně odebírá k povaření?", "Zhruba třetina", "Celý rmut", "Kapka na ochutnání", "Zhruba devět desetin",
                "Povařená část se vrací a zvedne teplotu celého rmutu."),
            Q(Vyroba, "Při jaké teplotě končí tradiční trojrmutový postup (odrmutování)?", "Asi 77 °C", "Asi 35 °C", "Asi 52 °C", "100 °C",
                "Prodlevy jsou zhruba 35, 52, 65 a 77 °C."),
            Q(Vyroba, "Při jaké teplotě se dělá cukrotvorná prodleva?", "Kolem 65 °C", "Kolem 35 °C", "Kolem 90 °C", "Kolem 10 °C",
                "Enzymy při ní mění škrob na cukry."),
            Q(Vyroba, "Které suroviny povoloval bavorský zákon o čistotě piva z roku 1516?", "Vodu, ječmen a chmel", "Vodu, pšenici a kvasnice", "Vodu, slad, chmel a cukr", "Jen vodu a ječmen",
                "Kvasnice v zákoně chyběly, i když je sládci používali."),
            Q(Vyroba, "Kdo vydal bavorský zákon o čistotě piva (Reinheitsgebot)?", "Vévoda Vilém IV. Bavorský", "Karel IV.", "Ludvík II. Bavorský", "Fridrich Barbarossa",
                "Zákon platil od 23. dubna 1516."),
            Q(Vyroba, "Ve kterém roce vyšel bavorský zákon o čistotě piva?", "1516", "1842", "1295", "1810",
                "Vydal ho vévoda Vilém IV. Bavorský."),
            Q(Vyroba, "Kdo napsal v roce 1876 knihu Études sur la bière (Studie o pivu)?", "Louis Pasteur", "Robert Koch", "Justus von Liebig", "Antoine Lavoisier",
                "Pasteur v ní popsal nemoci piva a jak mu zabránit kazit se."),
            Q(Vyroba, "Kterou vědeckou stupnici zavedl v roce 1909 chemik laboratoře pivovaru Carlsberg?", "pH", "Celsiovu", "Richterovu", "Beaufortovu",
                "S. P. L. Sørensen vedl Carlsbergovu laboratoř v Kodani."),

            // ---------------- pivo ve světě ----------------
            Q(Svet, "Jak se jmenovala sumerská bohyně piva?", "Ninkasi", "Ištar", "Isis", "Freya",
                "Její jméno se překládá jako „paní, která plní ústa“."),
            Q(Svet, "Co obsahuje starověký Hymnus na Ninkasi?", "Recept na pivo", "Seznam králů", "Zákony o prodeji piva", "Mapu Mezopotámie",
                "Hymnus byl sepsán kolem roku 1800 př. n. l."),
            Q(Svet, "Ve kterém městě se koná Oktoberfest?", "V Mnichově", "V Berlíně", "V Kolíně nad Rýnem", "Ve Vídni",
                "Říká se mu také Wiesn."),
            Q(Svet, "Jaká událost dala vzniknout Oktoberfestu v roce 1810?", "Svatba prince Ludvíka a Terezie", "Konec napoleonských válek", "Otevření mnichovského pivovaru", "Korunovace bavorského krále",
                "Louka, kde se slavilo, se dodnes jmenuje po nevěstě."),
            Q(Svet, "Jak se jmenuje louka, kde se koná Oktoberfest?", "Theresienwiese", "Marienplatz", "Englischer Garten", "Odeonsplatz",
                "Terezina louka – podle princezny Terezie."),
            Q(Svet, "Kdo na Oktoberfestu narazí první sud a zvolá „O'zapft is!“?", "Starosta Mnichova", "Bavorský premiér", "Nejstarší sládek", "Vítěz losování",
                "„O'zapft is!“ znamená „Je naraženo!“"),
            Q(Svet, "Ve kterém měsíci Oktoberfest začíná?", "V září", "V říjnu", "V srpnu", "V listopadu",
                "Začíná v polovině září a končí na začátku října."),
            Q(Svet, "Kolik piva je v jedné Maß?", "1 litr", "0,5 litru", "0,7 litru", "2 litry",
                "Maßkrug je symbolem Oktoberfestu."),
            Q(Svet, "Na kolik let si Arthur Guinness pronajal pivovar v Dublinu?", "Na 9 000 let", "Na 99 let", "Na 999 let", "Na 50 let",
                "Nájem stál 45 liber ročně."),
            Q(Svet, "Ve kterém roce podepsal Arthur Guinness slavnou nájemní smlouvu?", "1759", "1842", "1516", "1888",
                "Pivovar stojí u St. James's Gate v Dublinu."),
            Q(Svet, "Ze kterého města pochází pivo Kölsch?", "Z Kolína nad Rýnem", "Z Mnichova", "Z Düsseldorfu", "Z Bambergu",
                "Kölsch se smí vařit jen do 50 km od Kolína."),
            Q(Svet, "V jaké sklenici se podává Kölsch?", "Ve štíhlé Stange (0,2 l)", "V litrové Maß", "V půllitru s uchem", "V pokálu na stopce",
                "Stange je štíhlá válcová sklenice."),
            Q(Svet, "Ze kterého města pochází Altbier?", "Z Düsseldorfu", "Z Kolína nad Rýnem", "Z Mnichova", "Z Plzně",
                "Altbier je měděné svrchně kvašené pivo."),
            Q(Svet, "Které město je proslulé kouřovým pivem (Rauchbier)?", "Bamberg", "Brémy", "Lipsko", "Drážďany",
                "Slad se tam suší nad otevřeným ohněm."),
            Q(Svet, "Jak kvasí belgický lambik?", "Spontánně, divokými kvasinkami", "Spodními kvasinkami", "Svrchními kvasinkami", "Nekvasí",
                "Lambik se vaří jen v okolí Bruselu."),
            Q(Svet, "Co je kriek?", "Lambik s višněmi", "Lambik s malinami", "Pšeničné pivo", "Tmavý stout",
                "Tradiční kriek je suchý a kyselý."),
            Q(Svet, "Co je gueuze?", "Směs mladého a starého lambiku", "Silné tmavé pivo", "Pivo s medem", "Nealkoholické pivo",
                "V lahvi dokvašuje nejméně rok."),
            Q(Svet, "Ve které zemi je nejvíc trapistických pivovarů?", "V Belgii", "V Německu", "Ve Francii", "V Česku",
                "Trapistické pivo se musí vařit uvnitř kláštera."),
            Q(Svet, "Ve kterém městě se vaří Stella Artois?", "V Lovani (Leuven)", "V Bruselu", "V Antverpách", "V Amsterdamu",
                "Pivovar Den Horen je v Lovani doložen už roku 1366."),
            Q(Svet, "Co znamená jméno Stella v pivu Stella Artois?", "Hvězdu – bylo to vánoční pivo", "Jméno sládkovy dcery", "Ulici v Lovani", "Druh chmele",
                "Poprvé ho uvařili jako vánoční pivo v roce 1926."),
            Q(Svet, "Kdo a kdy založil pivovar Carlsberg?", "J. C. Jacobsen v roce 1847", "Arthur Guinness v roce 1759", "Gerard Heineken v roce 1864", "Josef Groll v roce 1842",
                "Pivovar pojmenoval po synovi Carlovi a kopci (bjerg)."),
            Q(Svet, "Co je Radler?", "Pivo s citronovou limonádou", "Pivo s rumem", "Tmavé pšeničné pivo", "Pivo s medovinou",
                "Radler znamená německy cyklista."),
            Q(Svet, "Kdy se slaví Mezinárodní den piva?", "První pátek v srpnu", "1. května", "Poslední sobotu v září", "11. listopadu",
                "Vznikl v roce 2007 v kalifornské Santa Cruz."),
            Q(Svet, "Které anglické město se hlásí jako rodiště pale ale?", "Burton upon Trent", "Londýn", "Manchester", "Liverpool",
                "Burtonský pale ale je jemnější než IPA."),
            Q(Svet, "Jak se vyrábí Eisbock?", "Zmrazí se a led se odebere", "Podává se s ledem", "Kvasí v ledovém sklepě", "Chmelí se zmrazeným chmelem",
                "Síla piva se tím zhruba zdvojnásobí."),
            Q(Svet, "S čím se tradičně podává Berliner Weisse?", "Se sirupem (mařinka, malina)", "S plátkem citronu", "Se solí", "S rumem",
                "Je to kyselé pšeničné pivo s asi 2,5 % alkoholu."),
            Q(Svet, "Ze kterého města pochází kyselé pivo Gose?", "Z Lipska", "Z Mnichova", "Z Hamburku", "Z Berlína",
                "Jméno má podle řeky Gose."),

            // ---------------- hospoda ----------------
            Q(Hosp, "Kolik pěny má mít správně načepovaná hladinka?", "Zhruba tři prsty", "Žádnou", "Půlku sklenice", "Jen tenký proužek",
                "Hladinka se čepuje až po okraj."),
            Q(Hosp, "Co je šnyt?", "Víc pěny než piva, kus prázdný", "Pivo bez pěny", "Míchané světlé a tmavé", "Jen pěna",
                "Šnyt je na ochutnání, ne na žízeň."),
            Q(Hosp, "Co je mlíko?", "Sklenice skoro plná pěny", "Pivo s mlékem", "Nealkoholické pivo", "Světlé pivo bez pěny",
                "Pěna je sladší a jemnější než pivo pod ní."),
            Q(Hosp, "Co je řezané pivo?", "Světlé a tmavé dohromady", "Pivo s limonádou", "Pivo nalité napůl", "Pivo bez pěny",
                "Jméno je od slova řezat."),
            Q(Hosp, "Kolik piva se vejde do tupláku?", "1 litr", "0,5 litru", "0,3 litru", "2 litry",
                "Tuplák = dvojitý, tedy dva půllitry."),
            Q(Hosp, "Kolik je „malé pivo“?", "0,3 litru", "0,2 litru", "0,4 litru", "0,5 litru",
                "Velké je půllitr."),
            Q(Hosp, "Pod jakou značkou se vyrábějí české výčepní kohouty s bočním ovládáním?", "Lukr", "Grohe", "Zátka", "Pípař",
                "Firma Lukr je vyvinula v roce 1991."),
            Q(Hosp, "Do které pražské hospody chodil Bohumil Hrabal?", "U Zlatého tygra", "U Kalicha", "U Fleků", "U Medvídků",
                "Hrabal tam byl pravidelným štamgastem."),
            Q(Hosp, "Kdo v lednu 1994 seděl U Zlatého tygra s Václavem Havlem a Bohumilem Hrabalem?", "Bill Clinton", "Michail Gorbačov", "Margaret Thatcherová", "Papež Jan Pavel II.",
                "Psal se 11. leden 1994."),
            Q(Hosp, "Kde se v Praze jako v první hospodě čepoval plzeňský Prazdroj?", "U Pinkasů", "U Zlatého tygra", "U Kalicha", "U Fleků",
                "U Zlatého tygra byli až druzí."),
            Q(Hosp, "Do které hospody chodil dobrý voják Švejk?", "U Kalicha", "U Zlatého tygra", "U Fleků", "U Pinkasů",
                "Chodil tam každý večer v šest."),
            Q(Hosp, "Kdo natočil film Postřižiny?", "Jiří Menzel", "Miloš Forman", "Věra Chytilová", "Jan Svěrák",
                "Film podle Hrabala vznikl v roce 1980."),
            Q(Hosp, "Kde vyrůstal Bohumil Hrabal, když byl jeho otčím správcem pivovaru?", "V Nymburce", "V Plzni", "V Kolíně", "V Brně",
                "Z toho vznikly Postřižiny i Postřižinské pivo."),
            Q(Hosp, "Koho předobrazem je Maryška z Postřižin?", "Hrabalovy matky", "Hrabalovy ženy", "Hrabalovy sestry", "Herečky Magdy Vášáryové",
                "Ve filmu ji hrála Magda Vášáryová."),
        };

        // ======================= VÍNO =======================
        // Ověřeno 18. 11. 2026: Wikipedie (Czech wine, Tokaji, Port wine, Sherry, Prosecco, Retsina, Ice wine, Beaujolais nouveau,
        // Phylloxera, Malbec, Grüner Veltliner, Riesling, Mulled wine), vinarskecentrum.cz (vinařské oblasti), epravo.cz (burčák),
        // e15.cz (svatomartinské víno), decanter.com (velikosti lahví), thewhiskyexchange.com (šampaňské),
        // chianticlassico.com (legenda o černém kohoutovi), winefolly.com (Rioja).
        private const string CzV = "Česká vína", SvetV = "Víno ve světě", Bubl = "Bubliny a sladká vína", PojmyV = "Vinařské pojmy";

        public static readonly List<QuizQuestion> Vino = new()
        {
            // ---------------- česká vína ----------------
            Q(CzV, "Kolik českých vinic leží na Moravě?", "Asi 96 %", "Asi 50 %", "Asi 70 %", "Asi 85 %",
                "Čechy mají jen kolem 700 hektarů, Morava přes 17 tisíc."),
            Q(CzV, "Kolik vinařských podoblastí má Morava?", "Čtyři", "Dvě", "Tři", "Šest",
                "Mikulovská, Znojemská, Velkopavlovická a Slovácká."),
            Q(CzV, "Která z těchto podoblastí NENÍ moravská?", "Mělnická", "Mikulovská", "Znojemská", "Slovácká",
                "Mělnická je v Čechách, spolu s Litoměřickou."),
            Q(CzV, "Které dvě vinařské podoblasti mají Čechy?", "Mělnickou a Litoměřickou", "Mělnickou a Pražskou", "Litoměřickou a Mosteckou", "Roudnickou a Kutnohorskou",
                "Vinice jsou i v Praze nebo u Mostu, ale patří pod tyhle dvě."),
            Q(CzV, "Ve kterém zámku sídlí Salon vín České republiky?", "Ve Valticích", "V Lednici", "V Mikulově", "Ve Znojmě",
                "Do Salonu se každý rok vybírá stovka nejlepších českých vín."),
            Q(CzV, "Která obec má nejvíc vinic v Česku?", "Velké Bílovice", "Valtice", "Mikulov", "Čejkovice",
                "Podle Národního vinařského centra kolem 777 hektarů (2023)."),
            Q(CzV, "Kdy se smí prodávat burčák?", "Od 1. srpna do 30. listopadu", "Od 15. srpna do 15. listopadu", "Od 1. září do 31. prosince", "Celý rok",
                "A jen z hroznů sklizených v tom roce, dokud ještě kvasí."),
            Q(CzV, "Odkud musí být hrozny, aby se tomu smělo říkat burčák?", "Sklizené i zpracované v Česku", "Z celé EU", "Jen z Moravy", "Odkudkoli, když se kvasí tady",
                "Z dovezených hroznů je to jen „částečně zkvašený hroznový mošt“."),
            Q(CzV, "Kdy se otevírá svatomartinské víno?", "11. 11. v 11:11", "1. 11. v poledne", "11. 11. o půlnoci", "Třetí čtvrtek v listopadu",
                "Třetí čtvrtek v listopadu je Beaujolais nouveau, to je jiná pohádka."),
            Q(CzV, "Která bílá odrůda může být svatomartinským vínem?", "Müller Thurgau", "Ryzlink rýnský", "Chardonnay", "Sauvignon",
                "Dál Veltlínské červené rané a Muškát moravský."),
            Q(CzV, "Která modrá odrůda může být červeným svatomartinským?", "Modrý Portugal", "Frankovka", "Cabernet Sauvignon", "Merlot",
                "Druhou je Svatovavřinecké."),
            Q(CzV, "Který z těchto přívlastků je nejnižší (nejméně cukru v moštu)?", "Kabinetní víno", "Pozdní sběr", "Výběr z hroznů", "Výběr z bobulí",
                "Kabinet 19 °NM, pozdní sběr 21, výběr z hroznů 24, z bobulí 27."),
            Q(CzV, "Který přívlastek potřebuje nejsladší mošt?", "Výběr z cibéb", "Výběr z bobulí", "Ledové víno", "Pozdní sběr",
                "Aspoň 32 °NM – cibéby jsou bobule napadené ušlechtilou plísní."),
            Q(CzV, "Při jaké teplotě se nejvýš smí lisovat hrozny na české ledové víno?", "−7 °C", "0 °C", "−3 °C", "−12 °C",
                "Voda zůstane v ledu, ven teče jen sladký koncentrát."),
            Q(CzV, "Jak dlouho se nejméně suší hrozny na slámové víno?", "Tři měsíce", "Týden", "Měsíc", "Rok",
                "Suší se na slámě nebo rákosu, odtud jméno."),
            Q(CzV, "Kdo dostal roku 1249 Mikulovsko od Přemysla Otakara II.?", "Lichtenštejnové", "Rožmberkové", "Pernštejnové", "Habsburkové",
                "Mikulov pak patřil k největším vinařským centrům Moravy."),
            Q(CzV, "Jak se česky jmenuje Grüner Veltliner?", "Veltlínské zelené", "Ryzlink vlašský", "Rulandské bílé", "Sylvánské zelené",
                "V Rakousku je to nejvysazovanější odrůda."),
            Q(CzV, "Co mají společného Ryzlink rýnský a Ryzlink vlašský?", "Nic, nejsou příbuzné", "Je to totéž", "Vlašský je potomek rýnského", "Rýnský je mutace vlašského",
                "Vlašský (Welschriesling) má s Ryzlinkem rýnským společné jen jméno."),

            // ---------------- víno ve světě ----------------
            Q(SvetV, "Odkud se do Evropy zavlekla mšička révokaz, která v 19. století zničila vinice?", "Ze Severní Ameriky", "Z Asie", "Z Afriky", "Z Austrálie",
                "Americké révy jsou vůči ní částečně odolné."),
            Q(SvetV, "Jak se Evropa nakonec ubránila révokazu?", "Roubováním na americké podnože", "Postřiky", "Vypalováním vinic", "Přesunem vinic na sever",
                "Evropská réva roste dodnes většinou na amerických kořenech."),
            Q(SvetV, "Která vinařská země se révokazu skoro vyhnula díky poušti, oceánu a Andám?", "Chile", "Argentina", "Francie", "Itálie",
                "Chile chrání poušť Atacama, Tichý oceán a Andy."),
            Q(SvetV, "Z jakého údolí pochází portské víno?", "Z údolí řeky Douro", "Z údolí Loiry", "Z údolí Rioja", "Z údolí Tejo",
                "Zraje ve sklepech ve Vila Nova de Gaia naproti Portu."),
            Q(SvetV, "Čím se zastaví kvašení portského?", "Přidáním vínovice", "Zmrazením", "Sírou", "Medem",
                "Neutrální vínovice (aguardente) zabije kvasinky – zůstane cukr a víc alkoholu."),
            Q(SvetV, "Kolik alkoholu má obvykle portské?", "Kolem 19–20 %", "Kolem 12 %", "Kolem 30 %", "Kolem 40 %",
                "Je to dezertní víno, ne pálenka."),
            Q(SvetV, "Kdy byla vymezena oblast portského – jedna z prvních chráněných vinařských oblastí?", "1756", "1703", "1855", "1935",
                "V roce 1703 byla Methuenská smlouva s Anglií, která portskému otevřela trh."),
            Q(SvetV, "Ze kterého města pochází sherry?", "Jerez de la Frontera", "Sevilla", "Málaga", "Porto",
                "„Sherry“ je zangličtělé Xeres, tedy Jerez."),
            Q(SvetV, "Z jaké odrůdy je většina sherry?", "Palomino", "Pedro Ximénez", "Tempranillo", "Albariño",
                "Palomino tvoří asi 90 %, Pedro Ximénez hlavně sladká sherry."),
            Q(SvetV, "Co je u sherry „flor“?", "Vrstva kvasinek na víně v sudu", "Druh sudu", "Sklenička na sherry", "Sladký přídavek",
                "Chrání víno před oxidací – pod ní zraje fino a manzanilla."),
            Q(SvetV, "Jak se jmenuje systém sudů, kde se mladší sherry postupně mísí se starším?", "Solera", "Barrique", "Remuage", "Assemblage",
                "V každé lahvi je tak kus hodně starého vína."),
            Q(SvetV, "Čím je ochucená řecká retsina?", "Pryskyřicí borovice", "Anýzem", "Medem", "Mořskou vodou",
                "Pryskyřicí se ve starověku těsnily amfory a víno ji načichlo."),
            Q(SvetV, "Ze které odrůdy je Beaujolais nouveau?", "Gamay", "Pinot noir", "Merlot", "Syrah",
                "Lahvuje se jen 6–8 týdnů po sklizni."),
            Q(SvetV, "Kdy se pouští na trh Beaujolais nouveau?", "Třetí čtvrtek v listopadu", "11. listopadu", "1. prosince", "Na první adventní neděli",
                "Jednotné datum platí od roku 1985."),
            Q(SvetV, "Ve které zemi je Malbec nejvysazovanější modrou odrůdou?", "V Argentině", "V Chile", "Ve Francii", "V Austrálii",
                "Nejvíc ho roste v Mendoze, původem je ale z Francie."),
            Q(SvetV, "Ve které zemi je Veltlínské zelené nejvysazovanější odrůdou?", "V Rakousku", "V Německu", "V Maďarsku", "Ve Slovinsku",
                "Zabírá asi třetinu rakouských vinic."),
            Q(SvetV, "Odkud pochází Ryzlink rýnský?", "Z Porýní v Německu", "Z Alsaska", "Z Moravy", "Z rakouské Wachau",
                "První zmínka je z roku 1435 z Rüsselsheimu."),
            Q(SvetV, "Které víno občas po letech zrání voní po petroleji?", "Ryzlink rýnský", "Chardonnay", "Muškát", "Merlot",
                "Může za to látka TDN – a fajnšmekři to milují."),
            Q(SvetV, "Která odrůda je hlavní v Rioji?", "Tempranillo", "Garnacha", "Sangiovese", "Malbec",
                "Rioja leží na severu Španělska v údolí řeky Ebro."),
            Q(SvetV, "Jak dlouho nejméně zraje červená Rioja Gran Reserva?", "5 let", "2 roky", "3 roky", "10 let",
                "Z toho aspoň 2 roky v sudu. Crianza stačí 2 roky celkem."),
            Q(SvetV, "Která dvě města se podle legendy přela o Chianti závodem rytířů, které vyslali kohouti?", "Florencie a Siena", "Řím a Florencie", "Pisa a Siena", "Benátky a Verona",
                "Rytíři vyrazili, jakmile kohout zakokrhal."),
            Q(SvetV, "Jakou barvu měl vítězný kohout, dnes symbol Chianti Classico?", "Černou", "Bílou", "Červenou", "Zlatou",
                "Florenťané ho nechali vyhladovět v tmavém kurníku, tak kokrhal dávno před svítáním."),

            // ---------------- bubliny a sladká vína ----------------
            Q(Bubl, "Které tři odrůdy jsou hlavní v šampaňském?", "Chardonnay, Pinot noir, Pinot meunier", "Chardonnay, Ryzlink, Muškát", "Pinot noir, Merlot, Gamay", "Sauvignon, Sémillon, Chardonnay",
                "Pár dalších je povoleno, ale skoro se nepoužívají."),
            Q(Bubl, "Co znamená na šampaňském „Blanc de blancs“?", "Jen z bílých hroznů", "Jen z modrých hroznů", "Bez cukru", "Z jednoho ročníku",
                "Blanc de noirs je naopak bílé víno z modrých hroznů."),
            Q(Bubl, "Co je „Brut nature“?", "Šampaňské skoro bez cukru (do 3 g/l)", "Nejsladší šampaňské", "Růžové šampaňské", "Šampaňské bez bublin",
                "Běžný brut mívá kolem 15 g cukru na litr."),
            Q(Bubl, "Kolik litrů má láhev Magnum?", "1,5 l", "1 l", "2 l", "3 l",
                "Dvě normální lahve v jedné."),
            Q(Bubl, "Jak se jmenuje 3litrová láhev šampaňského?", "Jeroboam", "Magnum", "Methuselah", "Balthazar",
                "Pozor, v Bordeaux má Jeroboam 5 litrů."),
            Q(Bubl, "Kolik normálních lahví se vejde do Nebukadnesaru?", "20", "8", "12", "16",
                "Má 15 litrů. Unese ho jen silný číšník."),
            Q(Bubl, "Z jaké odrůdy se dělá Prosecco?", "Glera", "Trebbiano", "Moscato", "Pinot grigio",
                "Do roku 2009 se odrůda jmenovala taky Prosecco."),
            Q(Bubl, "Kde druhotně kvasí většina Prosecca?", "Ve velkých nerezových tancích", "V lahvi", "V dubových sudech", "V amforách",
                "Říká se tomu metoda Charmat – na rozdíl od šampaňského, které kvasí v lahvi."),
            Q(Bubl, "Který slavný drink je původně z Prosecca?", "Bellini", "Margarita", "Cuba libre", "Negroni",
                "Prosecco je i základ Spritzu Veneziano."),
            Q(Bubl, "Ze které země je tokajské víno (většina oblasti)?", "Z Maďarska", "Z Rakouska", "Z Rumunska", "Z Chorvatska",
                "Malý kousek oblasti je i na Slovensku."),
            Q(Bubl, "Která odrůda je hlavní v tokajských výběrech?", "Furmint", "Tramín", "Muškát", "Ryzlink",
                "Furmint zabírá asi 60 % oblasti, další je Hárslevelű."),
            Q(Bubl, "Co udávají u tokaje „puttony“?", "Sladkost výběru", "Stáří vína", "Obsah alkoholu", "Velikost lahve",
                "Původně počet nůší cibéb (asi 25 kg) přidaných do sudu."),
            Q(Bubl, "Jak se odedávna říká tokajskému?", "Víno králů, král vín", "Krev Kristova", "Zlato Dunaje", "Slzy Panny Marie",
                "Latinsky Vinum regum, rex vinorum – připisuje se Ludvíku XIV."),
            Q(Bubl, "Kolik alkoholu má tokajská Eszencia?", "Jen asi 5–6 %", "Kolem 12 %", "Kolem 18 %", "Přes 40 %",
                "Má 500–700 gramů cukru na litr, kvasinky se v tom skoro utopí."),
            Q(Bubl, "Při jaké teplotě se v Kanadě musí sbírat hrozny na ledové víno?", "−8 °C a méně", "−2 °C", "−4 °C", "−15 °C",
                "V Německu a Rakousku stačí −7 °C."),
            Q(Bubl, "Která země vyrábí nejvíc ledového vína?", "Kanada", "Německo", "Rakousko", "Česko",
                "Víc než všechny ostatní země dohromady."),

            // ---------------- vinařské pojmy ----------------
            Q(PojmyV, "Jak se říká svařenému vínu v Německu?", "Glühwein", "Glögg", "Vin chaud", "Grzaniec",
                "Glögg je severský, vin chaud francouzský, grzaniec polský."),
            Q(PojmyV, "Odkud je svařené víno glögg?", "Ze Skandinávie", "Z Německa", "Z Polska", "Z Francie",
                "Podává se s rozinkami a mandlemi, někdy s panákem akvavitu."),
            Q(PojmyV, "Které koření patří do svařáku nejčastěji?", "Skořice, hřebíček a badyán", "Pepř a kmín", "Paprika a česnek", "Majoránka a tymián",
                "Často přijde i pomeranč, muškátový oříšek nebo zázvor."),
        };

        // ======================= TVRDEJ =======================
        // Ověřeno 18. 11. 2026: Wikipedie (Slivovitz, Becherovka, Jan Becher, Tuzemák, Fernet Stock, Borovička, Absinthe,
        // Scotch whisky, Tequila, Gin, Jägermeister, Rum, Caipirinha, Negroni, Bloody Mary, Piña colada, Margarita, Calvados,
        // Grappa, Ouzo, Pálinka), diffordsguide.com (koňak VS/VSOP/XO).
        private const string CzT = "Česká kořalka", SvetT = "Kořalky světa", Whisky = "Whisky, gin a rum", Koktejly = "Koktejly";

        public static readonly List<QuizQuestion> Tvrdej = new()
        {
            // ---------------- česká kořalka ----------------
            Q(CzT, "Národním nápojem kterého státu je podle Wikipedie slivovice (šljivovica)?", "Srbska", "Polska", "Rakouska", "Řecka",
                "UNESCO zapsalo srbskou šljivovicu na seznam nehmotného dědictví."),
            Q(CzT, "Ve kterém městě se vyrábí Becherovka?", "V Karlových Varech", "V Mariánských Lázních", "V Plzni", "Ve Vizovicích",
                "Vymyslel ji karlovarský lékárník Josef Becher."),
            Q(CzT, "Ve kterém roce se Becherovka začala prodávat?", "1807", "1842", "1889", "1918",
                "Lékárník Josef Becher předtím dva roky ladil recept."),
            Q(CzT, "Pod jakým jménem Josef Becher svůj likér zpočátku prodával?", "English Bitter", "Karlsbader Kräuter", "Aqua vitae", "Becherův elixír",
                "Recept prý probíral s anglickým lékařem Frobrigem."),
            Q(CzT, "Jak se jmenuje Becherovka s tonikem?", "Beton", "Cihla", "Malta", "Štěrk",
                "Vznikl pro Expo 67 v Montrealu."),
            Q(CzT, "Kolik alkoholu má Becherovka?", "38 %", "30 %", "40 %", "45 %",
                "Bylin a koření je v ní přes dvacet, přesný recept je tajný."),
            Q(CzT, "Z čeho se vyrábí tuzemák?", "Z brambor nebo cukrové řepy", "Z cukrové třtiny", "Z obilí", "Ze švestek",
                "Chuť mu dávají rumové esence."),
            Q(CzT, "Proč se tuzemský rum od roku 2003 nesmí jmenovat rum?", "Rum smí být jen z cukrové třtiny", "Je moc slabý", "Má špatnou barvu", "Kvůli spotřební dani",
                "Proto se dnes prodává jako Tuzemák nebo Tuzemský."),
            Q(CzT, "Kde se vyrábí Fernet Stock?", "V Plzni-Božkově", "V Karlových Varech", "V Praze", "Ve Vizovicích",
                "V Božkově se vyrábí od roku 1927."),
            Q(CzT, "Ve kterém městě založil Lionello Stock v roce 1884 svou firmu?", "V Terstu", "V Benátkách", "Ve Vídni", "V Plzni",
                "Terst tehdy patřil Rakousku-Uhersku."),
            Q(CzT, "Kolik alkoholu má Fernet Stock Citrus?", "30 %", "20 %", "38 %", "40 %",
                "Původní Fernet má kolem 40 %. Citrus přišel v roce 1997."),
            Q(CzT, "Čím je ochucená borovička?", "Jalovcem", "Borovým jehličím", "Anýzem", "Pelyňkem",
                "Žádná borovice. Proto chutná trochu jako gin."),
            Q(CzT, "Odkud borovička pochází?", "Ze Slovenska", "Z Polska", "Z Maďarska", "Z Rakouska",
                "Pálí se od 16. století, tradičně na Liptově."),
            Q(CzT, "Čím se liší český „absinth“ od klasického absintu?", "Obvykle v něm chybí anýz a fenykl", "Je z brambor", "Je bez pelyňku", "Je modrý",
                "Je blíž pelyňkové hořké. A zapalování je moderní vynález."),

            // ---------------- kořalky světa ----------------
            Q(SvetT, "Která bylina dala absintu jméno?", "Pelyněk", "Jalovec", "Máta", "Heřmánek",
                "Latinsky Artemisia absinthium. Dál je v něm anýz a fenykl."),
            Q(SvetT, "Jak se přezdívá absintu?", "Zelená víla", "Zelený ďábel", "Ledová královna", "Bílá paní",
                "Francouzsky la fée verte."),
            Q(SvetT, "Ve které zemi vznikl absint?", "Ve Švýcarsku", "Ve Francii", "V Čechách", "V Itálii",
                "V kantonu Neuchâtel na konci 18. století."),
            Q(SvetT, "Jak se absint tradičně ředí?", "Ledovou vodou přes kostku cukru na děrované lžičce", "Zapálením cukru", "Colou", "Mlékem",
                "Zakalí se – tomu se říká louche."),
            Q(SvetT, "Z čeho se vyrábí tequila?", "Z modré agáve", "Z kukuřice", "Z cukrové třtiny", "Z kaktusu opuncie",
                "Pečou se jádra agáve, kterým se říká piña."),
            Q(SvetT, "Ve kterém mexickém státě se vyrábí většina tequily?", "Jalisco", "Oaxaca", "Sonora", "Yucatán",
                "Městečko Tequila je kousek od Guadalajary."),
            Q(SvetT, "Jak dlouho zraje tequila reposado?", "Aspoň 2 měsíce, ale méně než rok", "Vůbec nezraje", "1 až 3 roky", "Přes 3 roky",
                "Añejo zraje 1–3 roky, extra añejo přes 3."),
            Q(SvetT, "Patří do lahve tequily červ?", "Ne, regulační rada to zakazuje", "Ano, vždycky", "Jen do añejo", "Jen pro export",
                "Červ bývá v některých mezcalech – a je to marketing."),
            Q(SvetT, "Kolik agáve musí mít nejlevnější tequila (mixto)?", "Aspoň 51 %", "Aspoň 10 %", "Aspoň 75 %", "100 %",
                "Zbytek můžou být jiné cukry."),
            Q(SvetT, "Odkud je ouzo?", "Z Řecka", "Z Turecka", "Z Itálie", "Z Bulharska",
                "Nejslavnější je z ostrova Lesbos, z Plomari."),
            Q(SvetT, "Co se stane s ouzem, když do něj nalijete vodu?", "Zbělá", "Zezelená", "Zpění", "Nic",
                "Anetol z anýzu se ve vodě nerozpustí a udělá mléčnou emulzi."),
            Q(SvetT, "Ze které země je pálinka?", "Z Maďarska", "Z Rumunska", "Ze Slovenska", "Ze Srbska",
                "Od roku 2004 je v EU chráněná – ovoce musí být maďarské."),
            Q(SvetT, "Co znamená u pálinky „kisüsti“?", "Pálená dvakrát v malém kotli", "Z divokého ovoce", "Bez cukru", "Zrála v sudu",
                "Kotel smí mít nejvýš 1000 litrů."),
            Q(SvetT, "Z čeho se pálí grappa?", "Z matolin po lisování hroznů", "Z rozinek", "Z fíků", "Z citronů",
                "Slupky, semínka a třapiny, co zbydou po výrobě vína."),
            Q(SvetT, "Co je caffè corretto?", "Espreso s grappou", "Espreso s mlékem", "Káva s citronem", "Studená káva",
                "„Opravená“ káva. Italové vědí."),
            Q(SvetT, "Z čeho se vyrábí calvados?", "Z jablek (a hrušek)", "Z hroznů", "Ze švestek", "Z třešní",
                "Pálí se z normandského cideru."),
            Q(SvetT, "Co je „trou normand“?", "Calvados mezi chody jídla", "Normandská hospoda", "Sud na calvados", "Kocovina po calvadosu",
                "„Normandská díra“ má udělat místo pro další chod."),
            Q(SvetT, "Co znamená VS na lahvi koňaku?", "Very Special", "Very Strong", "Vintage Selection", "Very Smooth",
                "VSOP je Very Superior Old Pale, XO Extra Old."),
            Q(SvetT, "Kolik bylin a koření je v Jägermeisteru?", "56", "13", "27", "99",
                "Má 35 % a jméno znamená „vrchní lovčí“."),
            Q(SvetT, "Ze kterého města pochází Jägermeister?", "Z Wolfenbüttelu", "Z Mnichova", "Z Hamburku", "Z Kolína nad Rýnem",
                "Recept vymyslel Curt Mast, prodává se od roku 1935."),
            Q(SvetT, "Co je v logu Jägermeistera?", "Jelen se zářícím křížem mezi parohy", "Orel", "Kanec", "Liška",
                "Odkaz na patrony lovců, svatého Huberta a Eustacha."),

            // ---------------- whisky, gin a rum ----------------
            Q(Whisky, "Kolik let nejméně musí zrát skotská whisky?", "3 roky", "1 rok", "5 let", "10 let",
                "V dubových sudech do 700 litrů, ve Skotsku."),
            Q(Whisky, "Kolik alkoholu musí mít nejméně skotská whisky v lahvi?", "40 %", "35 %", "37,5 %", "43 %",
                "Silnější smí být, slabší ne."),
            Q(Whisky, "Co znamená „single malt“?", "Z jedné palírny, jen ze sladového ječmene", "Z jednoho sudu", "Jednou pálená", "Z jednoho roku",
                "Pálí se po várkách v destilačním kotli."),
            Q(Whisky, "Co udává číslo let na lahvi whisky?", "Věk nejmladší whisky v ní", "Průměrný věk", "Věk nejstarší whisky", "Rok lahvování",
                "Dvanáctiletá tedy může obsahovat i starší."),
            Q(Whisky, "Kdo je v roce 1494 zapsán, že dostal slad na výrobu aqua vitae – nejstarší zmínka o skotské whisky?", "Mnich John Cor", "Johnnie Walker", "Jack Daniel", "William Grant",
                "Zápis je ve skotských daňových rolích, opatství Lindores."),
            Q(Whisky, "Která whisky se píše s „e“ (whiskey)?", "Irská", "Skotská", "Kanadská", "Japonská",
                "Skotská, kanadská i japonská je whisky."),
            Q(Whisky, "Která z těchto je oficiální skotská whisky oblast?", "Speyside", "Kentucky", "Cognac", "Jalisco",
                "Dál Highlands, Lowlands, Islay a Campbeltown."),
            Q(Whisky, "Čím musí v ginu převládat chuť?", "Jalovcem", "Anýzem", "Pelyňkem", "Koriandrem",
                "Jalovec je v ginu to samé co chmel v pivu."),
            Q(Whisky, "Kolik alkoholu musí mít v EU nejméně gin?", "37,5 %", "30 %", "40 %", "45 %",
                "Jalovcové kořalky smí mít i 30 %."),
            Q(Whisky, "Proč se v britských koloniích pil gin s tonikem?", "Tonik s chininem chránil před malárií", "Proti kurdějím", "Na trávení", "Na lepší spaní",
                "Gin přebil hořkou chuť chininu."),
            Q(Whisky, "Kdy zachvátil Anglii „Gin Craze“, kdy byla v Londýně ginovna skoro na každém rohu?", "Kolem roku 1700", "Kolem roku 1800", "Ve 20. letech 20. století", "Ve středověku",
                "Zhruba 1695–1735. Přes polovinu londýnských výčepů tvořily ginovny."),
            Q(Whisky, "Odkud je nejstarší známý záznam o rumu?", "Z Barbadosu", "Z Kuby", "Z Jamajky", "Z Portorika",
                "Dokument z roku 1651."),
            Q(Whisky, "Co skončilo 31. července 1970, na „Black Tot Day“?", "Denní příděl rumu v britském námořnictvu", "Prohibice v Americe", "Výroba tuzemáku", "Rumová stezka do Indie",
                "Dodnes se rum vydá jen při rozkazu „splice the mainbrace“."),
            Q(Whisky, "Co byl původně grog?", "Rum zředěný vodou", "Rum s čajem", "Svařené víno", "Pivo s rumem",
                "Zavedl ho admirál Vernon kolem roku 1740."),
            Q(Whisky, "Čemu se říká „andělský podíl“?", "Tomu, co se ze sudu odpaří", "První kapce z kotle", "Tomu, co si nechá palírník", "Zbytku v lahvi",
                "U rumu v tropech až 10 % ročně, ve Skotsku kolem 2 %."),
            Q(Whisky, "Z čeho se dělá rhum agricole?", "Z čerstvé šťávy z cukrové třtiny", "Z melasy", "Z cukrové řepy", "Z kukuřice",
                "Typický je z Martiniku. Většina ostatních rumů je z melasy."),

            // ---------------- koktejly ----------------
            Q(Koktejly, "Která kořalka patří do caipirinhy?", "Cachaça", "Bílý rum", "Vodka", "Tequila",
                "Brazilský národní koktejl: cachaça, limetka, cukr."),
            Q(Koktejly, "Co je caipiroska?", "Caipirinha s vodkou", "Caipirinha bez alkoholu", "Caipirinha s pivem", "Caipirinha s ginem",
                "Smirnoff si to jméno dokonce zaregistroval."),
            Q(Koktejly, "Co patří do Negroni?", "Gin, Campari a červený vermut", "Vodka, Campari a tonik", "Rum, cola a limetka", "Gin, tonik a okurka",
                "Všeho stejně. Podle legendy vznikl ve Florencii v roce 1919."),
            Q(Koktejly, "Čím se v Negroni Sbagliato nahrazuje gin?", "Proseccem", "Vodkou", "Sodou", "Tequilou",
                "Sbagliato znamená italsky „chybný“."),
            Q(Koktejly, "Ze kterého drinku vzniklo Negroni, když si hrabě řekl o gin místo sody?", "Z Americana", "Z Martini", "Z Manhattanu", "Ze Spritzu",
                "Hrabě Camillo Negroni – jestli to vůbec byl hrabě."),
            Q(Koktejly, "Co je základem Bloody Mary?", "Vodka a rajčatová šťáva", "Gin a rajčatová šťáva", "Rum a grenadina", "Vodka a brusinky",
                "Bez alkoholu je to Virgin Mary, s tequilou Bloody Maria."),
            Q(Koktejly, "Ve kterém baru prý vznikla Bloody Mary?", "V Harry's New York Bar v Paříži", "V hotelu Ritz v Londýně", "V Bodeguita del Medio v Havaně", "V Raffles v Singapuru",
                "Barman Fernand Petiot, kolem roku 1921."),
            Q(Koktejly, "Co znamená piña colada?", "Přecezený ananas", "Kokosový ráj", "Sladká pláž", "Ananas s rumem",
                "Rum, kokosový krém a ananasová šťáva."),
            Q(Koktejly, "Kde je piña colada od roku 1978 oficiálním národním drinkem?", "V Portoriku", "Na Kubě", "Na Jamajce", "V Mexiku",
                "Hotel Caribe Hilton v San Juanu tvrdí, že ji vymyslel v roce 1954."),
            Q(Koktejly, "Co znamená ve španělštině „margarita“?", "Sedmikráska", "Perla", "Slzička", "Princezna",
                "Je to i španělská podoba jména Markéta."),
            Q(Koktejly, "Co patří do klasické margarity?", "Tequila, triple sec a limetka", "Tequila, cola a citron", "Rum, máta a limetka", "Vodka, pomeranč a sůl",
                "Sůl na okraji skleničky je jen volitelná."),
            Q(Koktejly, "Kde vymysleli stroj na mraženou margaritu?", "V Dallasu", "V Mexico City", "V Las Vegas", "V Miami",
                "Mariano Martinez, rok 1971."),
        };

        /// <summary>Okruhy, jak je hráč vidí (klíč = název).</summary>
        public static readonly string[] Okruhy = { "Pivo", "Víno", "Tvrdej" };

        public static List<QuizQuestion> Of(string okruh) => okruh switch
        {
            "Víno" => Vino,
            "Tvrdej" => Tvrdej,
            _ => Pivo
        };

        /// <summary>Všechny otázky vybraných okruhů (nic nevybráno = pivo).</summary>
        public static List<QuizQuestion> For(IEnumerable<string> okruhy)
        {
            var list = okruhy.Distinct().SelectMany(Of).ToList();
            return list.Count > 0 ? list : Pivo;
        }

        public static int Count => Pivo.Count + Vino.Count + Tvrdej.Count;
    }
}
