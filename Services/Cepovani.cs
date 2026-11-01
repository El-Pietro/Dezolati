namespace Dezolati.Services
{
    /// <summary>
    /// Čepování hladinky – hra pro jednoho (appka i web sdílí tuhle logiku, kreslí si každý sám).
    /// Drž pípu, pivo teče po stěně nakloněného půllitru, sklenice se postupně narovnává a v závěru
    /// se tvoří pěna. Na jeden zátah: pustíš = konec, žádné dočepovávání. Pak se pěna usadí
    /// a pivo „dojde“ (část pěny se změní zpátky v pivo) – teprve potom se hodnotí.
    /// Hladinka: až po okraj, nahoře zhruba tři prsty husté pěny, po usazení pivo u cejchu.
    /// </summary>
    public sealed class PourGame
    {
        // půllitr: po okraj 580 ml, cejch 0,5 l zhruba 2–3 cm pod okrajem (místo na pěnu)
        public const double Rim = 580;
        public const double Mark = 500;
        /// <summary>Kolik ml pěny je „jeden prst“: tři prsty = přesně od cejchu po okraj.</summary>
        public const double FingerMl = (Rim - Mark) / 3;
        /// <summary>Pěna nad okrajem, než začne přetékat (koruna).</summary>
        public const double Crown = 14;
        /// <summary>1 ml piva udělá 4 ml pěny (mokrá česká pěna).</summary>
        private const double Expansion = 4;
        /// <summary>Jak dlouho se po zavření pípy usazuje, než se hodnotí (s).</summary>
        public const double SettleSeconds = 3.2;
        private const double MaxTilt = 45;

        public enum Phase { Ready, Pouring, Settling, Done }

        public Phase State { get; private set; } = Phase.Ready;
        /// <summary>Tekuté pivo v ml.</summary>
        public double Beer { get; private set; }
        /// <summary>Objem pěny v ml.</summary>
        public double Foam { get; private set; }
        /// <summary>Kolik přeteklo (ml piva – přetečená pěna se počítá jako pivo, ze kterého vznikla).</summary>
        public double Spilled { get; private set; }
        /// <summary>Náklon sklenice ve stupních (45 = začátek, 0 = rovně).</summary>
        public double Tilt { get; private set; } = MaxTilt;
        /// <summary>Průtok pípy (ml/s) – každé kolo trochu jiný (tlak, sud).</summary>
        public double Flow { get; }
        /// <summary>Jak moc pivo pění (0,8–1,25) – taky každé kolo jiné.</summary>
        public double Foaminess { get; }
        public double PourSeconds { get; private set; }
        public bool Dribbled { get; private set; }    // zkoušel to ještě jednou zmáčknout
        public PourResult? Result { get; private set; }

        private double settleT;

        public PourGame(int? seed = null)
        {
            var r = seed is int s ? new Random(s) : Random.Shared;
            Flow = 110 + r.NextDouble() * 35;
            Foaminess = 0.8 + r.NextDouble() * 0.45;
        }

        /// <summary>Hladina pěny (vršek) jako podíl výšky sklenice (1 = okraj, víc = koruna).</summary>
        public double TopFraction => (Beer + Foam) / Rim;
        public double BeerFraction => Beer / Rim;
        public double FoamFingers => Foam / FingerMl;

        /// <summary>Prst na pípu. false = už se čepovalo (na jeden zátah).</summary>
        public bool Press()
        {
            if (State == Phase.Ready)
            {
                State = Phase.Pouring;
                return true;
            }
            if (State == Phase.Settling) Dribbled = true;   // dočepovávání se nepočítá, jen se pamatuje
            return false;
        }

        public void Release()
        {
            if (State != Phase.Pouring) return;
            State = Phase.Settling;
            settleT = 0;
        }

        public void Step(double dt)
        {
            if (dt <= 0 || State == Phase.Done) return;
            dt = Math.Min(dt, 0.05);

            if (State == Phase.Pouring)
            {
                PourSeconds += dt;
                // sklenice se narovnává, jak přibývá piva (rovně je zhruba ve čtyřech pětinách)
                var fill = Math.Min(1, BeerFraction);
                Tilt = MaxTilt * Math.Clamp(1 - fill / 0.8, 0, 1);
                // po stěně skoro bez pěny, rovně do sklenice čím dál víc pěny
                var upright = 1 - Tilt / MaxTilt;
                var foamShare = Math.Clamp((0.01 + 0.08 * upright * upright * upright) * Foaminess, 0, 0.9);
                // když už je plno a pořád se točí, proud jen bičuje pěnu
                if (TopFraction > 0.97) foamShare = Math.Max(foamShare, 0.85);
                var inflow = Flow * dt;
                Beer += inflow * (1 - foamShare);
                Foam += inflow * foamShare * Expansion;
                if (PourSeconds > 14) Release();   // nikdo nedrží pípu čtvrt minuty
            }
            else if (State == Phase.Settling)
            {
                settleT += dt;
                Tilt = Math.Max(0, Tilt - 60 * dt);
            }

            // pěna se usazuje → zpátky pivo (při čepování proud pěnu i dorovnává, proto pomaleji)
            var k = State == Phase.Pouring ? 0.04 : 0.06;
            var collapsed = Foam * k * dt;
            Foam -= collapsed;
            Beer += collapsed / Expansion;

            // přetečení: nejdřív teče pěna, pak pivo
            var over = Beer + Foam - (Rim + Crown);
            if (over > 0)
            {
                var fromFoam = Math.Min(over, Foam);
                Foam -= fromFoam;
                Beer -= over - fromFoam;
                Spilled += fromFoam / Expansion + (over - fromFoam);   // v ml piva (pěna je hlavně vzduch)
            }

            if (State == Phase.Settling && settleT >= SettleSeconds)
            {
                State = Phase.Done;
                Result = Evaluate();
            }
        }

        private PourResult Evaluate()
        {
            var fingers = FoamFingers;
            var total = Beer + Foam;
            string title, comment;
            int score;

            if (Spilled > 120)
            {
                title = "Potopa";
                comment = $"Na tácku je {Spilled:0} ml. Pípa se pouští, ne že se u ní usne.";
                score = 0;
            }
            else if (Beer < 170)
            {
                title = "Mlíko";
                comment = "Samá pěna. Někdo to tak pije, ale objednával hladinku.";
                score = 15;
            }
            else if (fingers < 0.5)
            {
                title = "Čochtan";
                comment = "Pivo bez čepice. Vypadá jak z petky a do deseti minut je mrtvé.";
                score = 20;
            }
            else if (Beer < 400 && total >= 470 && fingers >= 4)
            {
                title = "Šnyt";
                comment = "Půl piva, půl pěny. Na ochutnání dobrý, na žízeň urážka.";
                score = 35;
            }
            else if (total < 480)
            {
                title = "Podmírák";
                comment = Beer < Mark
                    ? $"Po usazení je piva {Beer:0} ml, do čárky 0,5 l chybí {Mark - Beer:0} ml. Za tohle se v hospodách bilo."
                    : $"Pivo je u čárky, ale pěna nedosahuje k okraji. Za tohle se v hospodách bilo.";
                score = Math.Max(5, 40 - (int)((480 - total) / 4));
            }
            else
            {
                // hladinka: pivo po usazení u čárky 0,5 l, nad ním tři prsty pěny až k okraji, nic nepřeteklo
                var s = 100.0
                        - 22 * Math.Abs(fingers - 3)
                        - 0.5 * Math.Max(0, Mark - 10 - Beer)
                        - 0.6 * Math.Max(0, Rim - 20 - total)
                        - 0.6 * Spilled;
                score = (int)Math.Round(Math.Clamp(s, 0, 100));
                (title, comment) = score switch
                {
                    >= 92 => ("Hladinka jak z učebnice", "Výčepní by ti podal ruku. Teda kdyby ji neměl mokrou."),
                    >= 78 => ("Hladinka", "Tohle by se dalo donést na stůl, aniž by štamgasti mlčky zírali."),
                    >= 60 => ("Ujde to", Spilled > 20 ? "Kdyby to nepřeteklo, mohla to být hladinka."
                                 : fingers < 2.5 ? "Pěny je málo, pivo rychle vyčpí."
                                 : fingers > 3.5 ? "Pěny je moc, hladinka to ještě není."
                                 : Beer < Mark - 10 ? $"Piva je {Beer:0} ml, pod čárkou. Štamgast si toho všimne dřív než ty."
                                 : "Málo plné. Štamgast si toho všimne dřív než ty."),
                    _ => ("Výčepní by plakal", fingers < 3 ? "Skoro bez pěny. Na to stačí plechovka." : "Víc pěny než piva v hlavě.")
                };
            }

            if (Spilled > 20 && Spilled <= 120)
            {
                comment = $"Přeteklo {Spilled:0} ml na tácek. " + comment;
                score = Math.Max(0, score - (int)(Spilled / 4));
                if (title.StartsWith("Hladinka")) title = "Hladinka s potopou";
            }
            if (Dribbled)
            {
                comment += " A dočepovávat se nesmí – na jeden zátah, ne na splátky.";
            }

            return new PourResult(title, comment, score, Beer, fingers, Spilled);
        }
    }

    public sealed record PourResult(string Title, string Comment, int Score, double BeerMl, double FoamFingers, double SpilledMl);
}
