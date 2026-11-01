namespace Dezolati.Services
{
    /// <summary>
    /// Odznak na tlačítku Přátelé = počet žádostí o přátelství a pozvánek do hospody, které čekají na mě.
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
        private static int _requests, _invites;

        /// <summary>Počet se změnil (může přijít z jiného vlákna).</summary>
        public static event Action? Changed;

        /// <summary>Přišlo oznámení o žádosti / návrat do appky → příště se zeptat.</summary>
        public static void Invalidate() => _dirty = true;

        /// <summary>Stránka Přátelé zná přesný počet (už ho má načtený) → bez dalšího čtení.</summary>
        public static void Set(int count)
        {
            _dirty = false;
            _lastCheck = DateTime.UtcNow;
            _requests = count;
            Update(_requests + _invites);
        }

        /// <summary>Stránka pozvánek zná, kolik jich čeká na odpověď.</summary>
        public static void SetInvites(int count)
        {
            _invites = count;
            Update(_requests + _invites);
        }

        public static async Task RefreshAsync()
        {
            if (_running || AuthSession.Uid is not string uid || !Net.IsOnline) return;
            if (!_dirty && DateTime.UtcNow - _lastCheck < MinInterval) return;

            _running = true;
            try
            {
                _requests = await new FirestoreService().CountIncomingRequests(uid);
                try
                {
                    await Pozvani.MineAsync();
                    _invites = Pozvani.WaitingCount;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Odznak pozvánek: {ex.Message}"); }
                _dirty = false;
                _lastCheck = DateTime.UtcNow;
                Update(_requests + _invites);
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
            _requests = _invites = 0;
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
