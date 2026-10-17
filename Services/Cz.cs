namespace Dezolati.Services
{
    /// <summary>Česká množná čísla, ať appka nepíše "1 piv".</summary>
    public static class Cz
    {
        public static string Plural(int n, string one, string few, string many) =>
            $"{n} " + (n == 1 ? one : n is >= 2 and <= 4 ? few : many);

        public static string Piva(int n) => Plural(n, "pivo", "piva", "piv");
        public static string Vina(int n) => Plural(n, "víno", "vína", "vín");
        public static string Panaky(int n) => Plural(n, "panák", "panáky", "panáků");

        public static string Souhrn(int pivo, int vino, int panaky) =>
            $"{Piva(pivo)}, {Vina(vino)}, {Panaky(panaky)}";
    }
}
