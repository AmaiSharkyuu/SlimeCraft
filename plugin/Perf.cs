using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SlimeCraft
{
    /// <summary>Debug timings: how long each system takes per frame, dumped to the log by the "perf" test command.</summary>
    public static class Perf
    {
        private static readonly Dictionary<string, double> total = new Dictionary<string, double>();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static double frameStart, frameSum;
        private static int frames;

        public static void Run(string name, Action a)
        {
            double t = clock.Elapsed.TotalMilliseconds;
            a();
            double d = clock.Elapsed.TotalMilliseconds - t;
            total.TryGetValue(name, out double v);
            total[name] = v + d;
        }

        public static void Frame()
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (frames > 0) frameSum += now - frameStart;
            frameStart = now;
            frames++;
        }

        public static void Dump()
        {
            int n = Math.Max(1, frames - 1);
            var parts = new List<string>();
            foreach (var kv in total) if (kv.Value / n > 0.05) parts.Add(kv.Key + "=" + (kv.Value / n).ToString("F2"));
            Plugin.Log.LogInfo("perf over " + n + " frames: frame=" + (frameSum / n).ToString("F1") + "ms " + string.Join(" ", parts.ToArray()));
            total.Clear(); frames = 0; frameSum = 0;
        }
    }
}
