using System;

namespace WomenOfWasteland
{
    // Loudness matching for replacement voice clips. No UnityEngine types (testable outside the game).
    // Loudness = RMS over the "active" part of a clip: 1024-sample blocks whose RMS is above 5 % of the clip's peak,
    // so leading/trailing silence and a longer or shorter take don't skew it.
    internal static class Level
    {
        public const float Knee = 0.7f;   // the limiter leaves everything below this untouched

        public static double ActiveRms(float[] s)
        {
            if (s == null || s.Length == 0) return 0;
            double peak = 0;
            foreach (var x in s) { double a = Math.Abs(x); if (a > peak) peak = a; }
            if (peak <= 0) return 0;
            const int W = 1024;
            double sum = 0; int blocks = 0;
            for (int i = 0; i < s.Length; i += W)
            {
                int n = Math.Min(W, s.Length - i);
                double e = 0;
                for (int k = 0; k < n; k++) e += (double)s[i + k] * s[i + k];
                e /= n;
                if (Math.Sqrt(e) > peak * 0.05) { sum += e; blocks++; }
            }
            return blocks > 0 ? Math.Sqrt(sum / blocks) : 0;
        }

        // gain, then a soft limiter above the knee (|y| stays below 1). Returns the number of limited samples.
        private static int GainLimit(float[] src, float[] dst, double gain)
        {
            int limited = 0;
            for (int i = 0; i < src.Length; i++)
            {
                double x = src[i] * gain, a = Math.Abs(x);
                if (a > Knee)
                {
                    a = Knee + (1.0 - Knee) * Math.Tanh((a - Knee) / (1.0 - Knee));
                    x = x < 0 ? -a : a;
                    limited++;
                }
                dst[i] = (float)x;
            }
            return limited;
        }

        // Brings `s` (in place) to targetRms × volume. The limiter eats some loudness on hot clips, so the gain is
        // corrected twice against the limited result. Gain is clamped to 1/16 .. 16 (±24 dB).
        // targetRms <= 0: no matching, only the volume factor. Returns the total gain applied (before limiting).
        public static double Process(float[] s, double targetRms, double volume, out int limited)
        {
            limited = 0;
            double mine = ActiveRms(s);
            if (mine <= 1e-6) return 1.0;
            double want = (targetRms > 0 ? targetRms : mine) * volume;
            double gain = Clamp(want / mine);
            if (Math.Abs(gain - 1.0) < 1e-4) return 1.0;
            var tmp = new float[s.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                limited = GainLimit(s, tmp, gain);
                double got = ActiveRms(tmp);
                if (got <= 1e-9 || limited == 0) break;
                double g2 = Clamp(gain * want / got);
                if (Math.Abs(g2 - gain) / gain < 0.01) break;
                gain = g2;
            }
            limited = GainLimit(s, s, gain);
            return gain;
        }

        private static double Clamp(double g) { return Math.Max(1.0 / 16.0, Math.Min(16.0, g)); }
    }
}
