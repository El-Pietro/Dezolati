using global::Android.Media;

namespace Dezolati.Services
{
    /// <summary>
    /// Hlasitost z mikrofonu pro foukání skla. Nic se nenahrává ani neposílá – počítá se jen síla zvuku (0–1).
    /// </summary>
    public static class MicMeter
    {
        private static AudioRecord? _rec;
        private static Thread? _thread;
        private static volatile bool _run;
        private static double _level;

        public static double Level => _level;
        public static bool Running => _run;

        public static bool Start()
        {
            if (_run) return true;
            try
            {
                const int rate = 44100;
                var min = AudioRecord.GetMinBufferSize(rate, ChannelIn.Mono, Encoding.Pcm16bit);
                if (min <= 0) return false;
                _rec = new AudioRecord(AudioSource.Mic, rate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(min, 4096));
                if (_rec.State != State.Initialized)
                {
                    _rec.Release();
                    _rec = null;
                    return false;
                }
                _rec.StartRecording();
                _run = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "dz-mic" };
                _thread.Start();
                return true;
            }
            catch
            {
                Stop();
                return false;
            }
        }

        private static void Loop()
        {
            var buf = new short[2048];
            while (_run && _rec != null)
            {
                int n;
                try { n = _rec.Read(buf, 0, buf.Length); } catch { break; }
                if (n <= 0) continue;
                double sum = 0;
                for (var i = 0; i < n; i++) sum += (double)buf[i] * buf[i];
                var rms = Math.Sqrt(sum / n) / 32768.0;
                _level = _level * 0.6 + Math.Min(1, rms * 6) * 0.4;
            }
        }

        public static void Stop()
        {
            _run = false;
            try { _rec?.Stop(); } catch { }
            try { _rec?.Release(); } catch { }
            _rec = null;
            _level = 0;
        }
    }
}
