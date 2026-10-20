namespace Dezolati.Services
{
    /// <summary>
    /// Odznak na tlačítku Přátelé = počet žádostí o přátelství, které čekají na mě.
    /// Ptá se serveru jen při spuštění, návratu do appky a po oznámení o žádosti
    /// (ne při každém návratu na lístek), stránka Přátelé ho opraví zadarmo.
    /// </summary>
    public static class FriendBadge
    {
        private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(2);
        private static DateTime _lastCheck = DateTime.MinValue;
        private static bool _dirty = true;     // příště se zeptat bez ohledu na interval
        private static bool _running;

        public static int Count { get; private set; }

        /// <summary>Počet se změnil (může přijít z jiného vlákna).</summary>
        public static event Action? Changed;

        /// <summary>Přišlo oznámení o žádosti / návrat do appky → příště se zeptat.</summary>
        public static void Invalidate() => _dirty = true;

        /// <summary>Stránka Přátelé zná přesný počet (už ho má načtený) → bez dalšího čtení.</summary>
        public static void Set(int count)
        {
            _dirty = false;
            _lastCheck = DateTime.UtcNow;
            Update(count);
        }

        public static async Task RefreshAsync()
        {
            if (_running || AuthSession.Uid is not string uid || !Net.IsOnline) return;
            if (!_dirty && DateTime.UtcNow - _lastCheck < MinInterval) return;

            _running = true;
            try
            {
                var count = await new FirestoreService().CountIncomingRequests(uid);
                _dirty = false;
                _lastCheck = DateTime.UtcNow;
                Update(count);
            }
            catch (Exception ex)
            {
                // odznak není kritický – zkusí se příště
                System.Diagnostics.Debug.WriteLine($"Odznak přátel: {ex.Message}");
            }
            finally
            {
                _running = false;
            }
        }

        public static void Reset()
        {
            _dirty = true;
            Update(0);
        }

        private static void Update(int count)
        {
            if (count == Count) return;
            Count = count;
            Changed?.Invoke();
        }
    }
}
