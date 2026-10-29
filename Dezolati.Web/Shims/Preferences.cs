using System.Globalization;

namespace Microsoft.Maui.Storage
{
    /// <summary>
    /// Náhražka MAUI Preferences pro web: hodnoty žijí v localStorage prohlížeče (klíče „dz:…“).
    /// Při startu se všechno načte do paměti (Storage.InitAsync), čte se pak synchronně jako v telefonu,
    /// zápisy jdou hned zpátky do localStorage.
    /// </summary>
    public static class Preferences
    {
        internal const string Prefix = "dz:";
        private static readonly Dictionary<string, string> Values = new();
        private static readonly object Gate = new();

        /// <summary>Zápis do localStorage (nastaví Program.cs). null hodnota = smazat.</summary>
        internal static Action<string, string?>? Writer;

        internal static void Load(IDictionary<string, string> all)
        {
            lock (Gate)
            {
                Values.Clear();
                foreach (var kv in all)
                    if (kv.Key.StartsWith(Prefix, StringComparison.Ordinal))
                        Values[kv.Key[Prefix.Length..]] = kv.Value;
            }
        }

        public static bool ContainsKey(string key)
        {
            lock (Gate) return Values.ContainsKey(key);
        }

        public static void Remove(string key)
        {
            lock (Gate) Values.Remove(key);
            Writer?.Invoke(Prefix + key, null);
        }

        public static void Clear()
        {
            string[] keys;
            lock (Gate)
            {
                keys = Values.Keys.ToArray();
                Values.Clear();
            }
            foreach (var k in keys) Writer?.Invoke(Prefix + k, null);
        }

        public static string? Get(string key, string? defaultValue)
        {
            lock (Gate) return Values.TryGetValue(key, out var v) ? v : defaultValue;
        }

        public static bool Get(string key, bool defaultValue) =>
            Raw(key) is string s && bool.TryParse(s, out var v) ? v : defaultValue;

        public static int Get(string key, int defaultValue) =>
            Raw(key) is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;

        public static long Get(string key, long defaultValue) =>
            Raw(key) is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;

        public static double Get(string key, double defaultValue) =>
            Raw(key) is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;

        public static float Get(string key, float defaultValue) =>
            Raw(key) is string s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;

        public static DateTime Get(string key, DateTime defaultValue) =>
            Raw(key) is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? DateTime.FromBinary(v) : defaultValue;

        public static void Set(string key, string? value)
        {
            if (value == null) { Remove(key); return; }
            Put(key, value);
        }

        public static void Set(string key, bool value) => Put(key, value ? "True" : "False");
        public static void Set(string key, int value) => Put(key, value.ToString(CultureInfo.InvariantCulture));
        public static void Set(string key, long value) => Put(key, value.ToString(CultureInfo.InvariantCulture));
        public static void Set(string key, double value) => Put(key, value.ToString("R", CultureInfo.InvariantCulture));
        public static void Set(string key, float value) => Put(key, value.ToString("R", CultureInfo.InvariantCulture));
        public static void Set(string key, DateTime value) => Put(key, value.ToBinary().ToString(CultureInfo.InvariantCulture));

        private static string? Raw(string key)
        {
            lock (Gate) return Values.TryGetValue(key, out var v) ? v : null;
        }

        private static void Put(string key, string value)
        {
            lock (Gate) Values[key] = value;
            Writer?.Invoke(Prefix + key, value);
        }
    }

    /// <summary>Složky „v telefonu“ – na webu jen paměťový disk prohlížeče (po zavření zmizí, nevadí: je to cache).</summary>
    public static class FileSystem
    {
        public static string AppDataDirectory { get; } = Ensure("/dezolati/data");
        public static string CacheDirectory { get; } = Ensure("/dezolati/cache");

        private static string Ensure(string path)
        {
            try { Directory.CreateDirectory(path); }
            catch { /* server-side náhled pro testy: složka v /tmp */ path = Path.Combine(Path.GetTempPath(), path.TrimStart('/')); Directory.CreateDirectory(path); }
            return path;
        }
    }
}
