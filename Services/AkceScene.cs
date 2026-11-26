using static Dezolati.Services.SceneKit;

namespace Dezolati.Services
{
    /// <summary>
    /// Výzdoba akce (sdílená kresba appky a webu): pás nahoře, roh lístku, padající věci, tácek.
    /// Každý slot = hotový motiv (podle presetu), vlastní obrázek (SImage, klíč „{akce}/{slot}“) nebo nic.
    /// </summary>
    public static class AkceScene
    {
        public const double HeaderH = 84, CornerSize = 84, TopH = 40;

        /// <summary>Jak vypadá výzdoba: preset a režim slotů („preset“ | „img“ | „none“), klíč obrázků.</summary>
        public sealed record Look(string Preset, string Top, string Corner, string Fall, string Coaster, string Key)
        {
            public string ImgKey(string slot) => $"{Key}/{slot}";
            public bool Any => Top != "none" || Corner != "none" || Fall != "none" || Coaster != "none";
            public bool Animated => Fall != "none" || (Top == "preset" && Preset == "vanoce");
        }

        public static Look Of(AkceInfo a) => Of(a.Theme, a.Id);

        /// <summary>Theme → Look; „img“ bez nahraného obrázku = motiv.</summary>
        public static Look Of(Dictionary<string, string> theme, string key, ISet<string>? localImages = null)
        {
            var preset = theme.TryGetValue("preset", out var p) ? p : "party";
            string Mode(string slot)
            {
                var m = theme.TryGetValue(slot, out var v) ? v : preset == "none" ? "none" : "preset";
                if (m == "img" && !theme.ContainsKey(slot + "Img") && localImages?.Contains(slot) != true) m = "preset";
                if (m == "preset" && preset == "none") m = "none";
                return m;
            }
            return new Look(preset, Mode("top"), Mode("corner"), Mode("fall"), Mode("coaster"), key);
        }

        // ---------------- hlavička: pás + roh ----------------

        public static List<SceneNode> Header(double w, Look l, double t)
        {
            var n = new List<SceneNode>();
            var topW = l.Corner == "none" ? w : w - CornerSize + 10;
            if (l.Top == "img") n.AddRange(HeadsBunting(topW, l));
            else if (l.Top == "preset")
                n.AddRange(l.Preset switch
                {
                    "mikulas" => Chain(topW),
                    "vanoce" => EventScene.Lights(topW, "vanoce", t),
                    _ => Bunting(topW)
                });
            if (l.Corner != "none") n.Add(new SGroup(w - CornerSize, 0, 0, null, Corner(l)));
            return n;
        }

        private static string Sag(double w, double y0, double sag, double span)
        {
            var sb = new System.Text.StringBuilder($"M -4 {F(y0)}");
            for (var x = -4.0; x < w + 4; x += span) sb.Append($" Q {F(x + span / 2)} {F(y0 + sag * 2)} {F(x + span)} {F(y0)}");
            return sb.ToString();
        }

        private static double SagY(double x, double y0, double sag, double span)
        {
            var q = (((x + 4) % span) + span) % span / span;   // 0..1 v oblouku
            return y0 + sag * 4 * q * (1 - q);
        }

        /// <summary>Mikuláš: řetěz (čert) a na něm uhlíky a hvězdičky.</summary>
        private static List<SceneNode> Chain(double w)
        {
            var n = new List<SceneNode>();
            var i = 0;
            for (var x = 2.0; x < w - 4; x += 9, i++)
            {
                var y = SagY(x, 8, 7, 120);
                n.Add(i % 2 == 0
                    ? new SPath(Ellipse(x, y, 6, 3.4), Stroke: "#4A4A4A", StrokeWidth: 2.2)
                    : new SPath(Ellipse(x, y, 2.2, 3.8), Stroke: "#6A6A6A", StrokeWidth: 2));
            }
            var k = 0;
            for (var x = 40.0; x < w - 20; x += 64, k++)
            {
                var y = SagY(x, 8, 7, 120) + 3;
                n.Add(new SPath($"M {F(x)} {F(y)} L {F(x)} {F(y + 10)}", Stroke: "#4A4A4A", StrokeWidth: 1));
                n.Add(k % 2 == 0
                    ? new SPath($"M {F(x - 6)} {F(y + 14)} L {F(x - 2)} {F(y + 9)} L {F(x + 5)} {F(y + 10)} L {F(x + 7)} {F(y + 16)} L {F(x + 1)} {F(y + 21)} L {F(x - 5)} {F(y + 19)} Z", Fill: "#1E1E22")
                    : new SPath(Star(x, y + 16, 7, 3), Fill: "#E2B23A", Stroke: "#A97E1E", StrokeWidth: 1));
            }
            return n;
        }

        /// <summary>Party: vlaječky na šňůře.</summary>
        private static List<SceneNode> Bunting(double w)
        {
            var colors = new[] { "#E8433A", "#F2C94C", "#3C8DDE", "#7C9A3C", "#D46AA8" };
            var n = new List<SceneNode> { new SPath(Sag(w, 6, 6, 110), Stroke: "#2F2A22", StrokeWidth: 1.4) };
            var i = 0;
            for (var x = 8.0; x < w - 14; x += 22, i++)
            {
                double y1 = SagY(x, 6, 6, 110), y2 = SagY(x + 16, 6, 6, 110);
                n.Add(new SPath($"M {F(x)} {F(y1)} L {F(x + 16)} {F(y2)} L {F(x + 8)} {F((y1 + y2) / 2 + 20)} Z", Fill: colors[i % colors.Length], FillOpacity: 0.92));
            }
            return n;
        }

        /// <summary>Vlastní obrázek: hlavy v kolečkách na šňůře.</summary>
        private static List<SceneNode> HeadsBunting(double w, Look l)
        {
            var n = new List<SceneNode> { new SPath(Sag(w, 6, 6, 110), Stroke: "#2F2A22", StrokeWidth: 1.4) };
            var i = 0;
            for (var x = 24.0; x < w - 14; x += 52, i++)
            {
                var y = SagY(x, 6, 6, 110);
                n.Add(new SPath($"M {F(x)} {F(y)} L {F(x)} {F(y + 6)}", Stroke: "#2F2A22", StrokeWidth: 1));
                n.Add(new SGroup(x, y + 19, i % 2 == 0 ? -8 : 8, null, new()
                {
                    new SImage(l.ImgKey("top"), -13, -13, 26, 26, true),
                    new SPath(Ellipse(0, 0, 13, 13), Stroke: "#2F2A22", StrokeWidth: 1.4)
                }));
            }
            return n;
        }

        // ---------------- roh (84 × 84) ----------------

        private static List<SceneNode> Corner(Look l)
        {
            if (l.Corner == "img")
            {
                var n = new List<SceneNode>
                {
                    new SPath(Ellipse(44, 48, 31, 31), Fill: "#FFFFFF"),
                    new SImage(l.ImgKey("corner"), 15, 19, 58, 58, true),
                    new SPath(Ellipse(44, 48, 29, 29), Stroke: "#2F2A22", StrokeWidth: 2.2)
                };
                return n;
            }
            return l.Preset switch
            {
                "mikulas" => new() { new SGroup(18, 4, 10, null, Mitre(), 0.95) },
                "vanoce" => new() { new SGroup(0, 0, 0, null, EventScene.Mistletoe(), 0.9) },
                _ => Balloons()
            };
        }

        /// <summary>Biskupská mitra (≈ 50 × 70).</summary>
        private static List<SceneNode> Mitre() => new()
        {
            new SPath("M 4 68 L 2 30 C 2 18 14 6 25 0 C 36 6 48 18 48 30 L 46 68 Z", Fill: "#C7202B", Stroke: "#7E1219", StrokeWidth: 1.4),
            new SPath("M 4 60 L 46 60 L 46 68 L 4 68 Z", Fill: "#E2B23A"),
            new SPath("M 25 0 L 25 60", Stroke: "#E2B23A", StrokeWidth: 4),
            new SPath("M 18 28 L 32 28 M 25 20 L 25 40", Stroke: "#F4E3A1", StrokeWidth: 3.4, Round: true),
            new SPath("M 10 68 L 8 78 M 40 68 L 42 78", Stroke: "#E2B23A", StrokeWidth: 3, Round: true)
        };

        private static List<SceneNode> SantaCap() => new()
        {
            new SPath("M 4 26 C 10 8 30 0 46 10 C 54 16 58 26 60 34 L 54 30 C 48 22 36 16 26 22 C 20 26 18 26 4 26 Z", Fill: "#C7202B"),
            new SPath(RoundRect(0, 22, 34, 10, 5), Fill: "#FFFFFF", Stroke: "#C9D8E6", StrokeWidth: 1),
            new SPath(Ellipse(60, 36, 6, 6), Fill: "#FFFFFF", Stroke: "#C9D8E6", StrokeWidth: 1)
        };

        private static List<SceneNode> PartyHat() => new()
        {
            new SPath("M 0 34 L 14 0 L 28 34 Z", Fill: "#3C8DDE", Stroke: "#2A5F99", StrokeWidth: 1.2),
            new SPath("M 5 22 L 23 22 M 9 12 L 19 12", Stroke: "#F2C94C", StrokeWidth: 3),
            new SPath(Ellipse(14, 0, 4, 4), Fill: "#E8433A")
        };

        private static List<SceneNode> Balloons() => new()
        {
            new SPath("M 30 44 C 28 60 36 70 32 84 M 52 40 C 54 58 46 70 50 84 M 66 50 C 64 64 70 74 66 84", Stroke: "#6B6B6B", StrokeWidth: 1),
            new SPath(Ellipse(30, 28, 15, 18), Fill: "#E8433A"),
            new SPath(Ellipse(52, 22, 15, 18), Fill: "#F2C94C"),
            new SPath(Ellipse(66, 36, 12, 15), Fill: "#3C8DDE"),
            new SPath(Ellipse(25, 22, 3, 5), Fill: "#FFFFFF", FillOpacity: 0.5),
            new SPath(Ellipse(47, 16, 3, 5), Fill: "#FFFFFF", FillOpacity: 0.5)
        };

        private static string Star(double cx, double cy, double r1, double r2)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < 10; i++)
            {
                var r = i % 2 == 0 ? r1 : r2;
                var a = -Math.PI / 2 + i * Math.PI / 5;
                sb.Append(i == 0 ? "M " : " L ").Append($"{F(cx + Math.Cos(a) * r)} {F(cy + Math.Sin(a) * r)}");
            }
            return sb.Append(" Z").ToString();
        }

        // ---------------- tácek (64 × 64) ----------------

        public static List<SceneNode> Coaster(Look l)
        {
            var n = new List<SceneNode>
            {
                new SPath(Ellipse(32, 32, 30, 30), Fill: "#F1E6CC", Stroke: "#B9A67E", StrokeWidth: 1.6),
                new SPath(Ellipse(32, 32, 25, 25), Stroke: "#C9B68C", StrokeWidth: 1)
            };
            if (l.Coaster == "img")
            {
                n.Add(new SImage(l.ImgKey("coaster"), 9, 9, 46, 46, true));
                n.Add(new SPath(Ellipse(32, 32, 23, 23), Stroke: "#8A7A55", StrokeWidth: 1.2));
                return n;
            }
            switch (l.Preset)
            {
                case "mikulas": n.Add(new SGroup(19, 9, 0, null, Mitre(), 0.52)); break;
                case "vanoce": n.Add(new SPath(Star(32, 32, 16, 7), Fill: "#E2B23A", Stroke: "#A97E1E", StrokeWidth: 1)); break;
                default:
                    var colors = new[] { "#E8433A", "#F2C94C", "#3C8DDE", "#7C9A3C" };
                    for (var i = 0; i < 8; i++)
                    {
                        var a = i * Math.PI / 4;
                        n.Add(new SPath(Ellipse(32 + Math.Cos(a) * 14, 32 + Math.Sin(a) * 14, 3, 3), Fill: colors[i % colors.Length]));
                    }
                    n.Add(new SPath(Ellipse(32, 32, 6, 6), Fill: "#D4A62A"));
                    break;
            }
            return n;
        }

        // ---------------- padající věci ----------------

        /// <summary>Jedna padající věc: x (podíl šířky), velikost, doba pádu, zpoždění, posun do strany, barva/druh.</summary>
        public sealed record Drop(double X, double Size, double Duration, double Delay, double Drift, int Kind, double Spin);

        public static readonly Drop[] Drops = MakeDrops();

        private static Drop[] MakeDrops()
        {
            var rnd = new Random(11);
            return Enumerable.Range(0, 30).Select(i => new Drop(rnd.NextDouble(), 3 + rnd.NextDouble() * 5,
                8 + rnd.NextDouble() * 9, rnd.NextDouble() * 16, -20 + rnd.NextDouble() * 50, i, 200 + rnd.NextDouble() * 500)).ToArray();
        }

        /// <summary>Kolik věcí padá: obrázky jen každá třetí (ať to není přeplácané).</summary>
        public static IEnumerable<Drop> DropsFor(Look l) => l.Fall == "img" ? Drops.Where(d => d.Kind % 3 == 0) : Drops;

        public static readonly string[] Confetti = { "#F2C94C", "#E8433A", "#3C8DDE", "#7C9A3C", "#D46AA8", "#FFFFFF" };

        /// <summary>Jedna věc (kolem 0,0) podle motivu.</summary>
        public static List<SceneNode> DropShape(Look l, Drop d) => l.Fall == "img"
            ? new() { new SImage(l.ImgKey("fall"), -14, -14, 28, 28, true) }
            : l.Preset switch
            {
                "mikulas" when d.Kind % 4 == 0 => new() { new SPath(Star(0, 0, d.Size + 2, (d.Size + 2) / 2.3), Fill: "#E2B23A", FillOpacity: 0.9) },
                "mikulas" or "vanoce" => new() { new SPath(Ellipse(0, 0, d.Size / 2, d.Size / 2), Fill: "#FFFFFF", FillOpacity: 0.85) },
                _ => new() { new SPath(d.Kind % 3 == 0 ? Ellipse(0, 0, d.Size / 2, d.Size / 2) : Rect(-d.Size / 2, -1.5, d.Size, 3 + d.Size / 2), Fill: Confetti[d.Kind % Confetti.Length]) }
            };

        /// <summary>Padající věci v čase t (appka; web má CSS animaci).</summary>
        public static List<SceneNode> Fall(double w, double h, Look l, double t)
        {
            var n = new List<SceneNode>();
            if (l.Fall == "none") return n;
            foreach (var d in DropsFor(l))
            {
                var prog = ((t + d.Delay) % d.Duration) / d.Duration;
                n.Add(new SGroup(d.X * w + d.Drift * prog, -30 + (h + 60) * prog, d.Spin * prog * (l.Fall == "img" ? 0.3 : 1), null, DropShape(l, d)));
            }
            return n;
        }
    }
}
