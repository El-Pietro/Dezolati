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

        /// <summary>Vybere otázky, které hráči v tomhle zařízení ještě neviděli (když dojdou, začne se znovu).</summary>
        private List<QuizQuestion> Pick(int count)
        {
            HashSet<int> seen;
            try { seen = JsonSerializer.Deserialize<HashSet<int>>(Preferences.Get(SeenKey, "[]")) ?? new(); }
            catch { seen = new(); }
            var fresh = QuizBank.All.Where(q => !seen.Contains(q.Id)).OrderBy(_ => rnd.Next()).ToList();
            if (fresh.Count < count)
            {
                seen.Clear();
                fresh.AddRange(QuizBank.All.Where(q => fresh.All(f => f.Id != q.Id)).OrderBy(_ => rnd.Next()));
            }
            var picked = fresh.Take(Math.Min(count, QuizBank.All.Count)).ToList();
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

        public static readonly List<QuizQuestion> All = new()
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
    }
}
