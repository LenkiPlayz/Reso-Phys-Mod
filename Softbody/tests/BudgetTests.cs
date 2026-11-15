using Softbody.Core;

// 0.29.1: the Budget pause waits for a sustained overrun (Core/BudgetWatch.cs). Run with:  dotnet run -c Release --project tests -- budget
// CPU only and instant: the frames' costs and gaps are given, nothing on this machine is timed.
namespace Softbody.Tests;

static partial class Program
{
    static void BudgetWatchTests()
    {
        Console.WriteLine("== Budget pause (0.29.1)");
        const float budget = 4.5f;          // the default Budget
        const double vr = 1.0 / 90;          // a 90 fps VR frame

        // A. a steady body under its budget never pauses
        {
            var w = new BudgetWatch();
            double at = FeedBudget(w, budget, 600, _ => 3.0, vr);
            Check(at < 0 && Math.Abs(w.MinuteMs - 3.0) < 1e-9 && w.Seconds == BudgetWatch.MinuteSeconds,
                  $"a steady 3 ms body at 90 fps runs 10 minutes without pausing (minute average {w.MinuteMs:0.000} ms)");
        }
        // B. lag spikes that have nothing to do with it: a 150 ms frame every 10 s, and a 1 s hitch with a 400 ms frame every minute
        {
            var w = new BudgetWatch();
            double ema = 0, oldPause = -1, t = 0, worstMinute = 0, gapBefore = 0;
            int frame = 0;
            string verdict = null;
            while (t < 600)
            {
                bool hitch = frame > 0 && frame % 5400 == 2700;     // every minute, offset half a minute
                bool spike = !hitch && frame > 0 && frame % 900 == 0; // every 10 s
                double cost = hitch ? 400 : spike ? 150 : 3.5;
                double gapAfter = hitch ? 1.0 : spike ? 0.15 + vr : vr;
                w.Add(cost, gapBefore);
                gapBefore = gapAfter;
                verdict ??= w.Verdict(budget);
                if (w.MinuteMs > worstMinute) worstMinute = w.MinuteMs;
                // the rule until 0.29.0: after the warmup, the 20-frame average over budget or one frame over 150 ms
                if (frame > 60)
                {
                    ema = ema <= 0 ? cost : ema + 0.05 * (cost - ema);
                    if (oldPause < 0 && (ema > budget || cost > 150)) oldPause = t;
                }
                t += gapAfter;
                frame++;
            }
            Check(verdict == null && worstMinute < budget,
                  $"a 3.5 ms body with a 150 ms spike every 10 s and a 1 s, 400 ms hitch every minute runs 10 minutes (worst minute average {worstMinute:0.000} ms < {budget} ms)");
            Check(oldPause >= 0 && oldPause < 11,
                  $"(until 0.29.0 the same run paused for good at the first spike, {oldPause:0.0} s in)");
        }
        // C. really over its budget: pauses after about a minute, not before
        {
            var w = new BudgetWatch();
            double at = FeedBudget(w, budget, 300, _ => 6.0, vr);
            string v = w.Verdict(budget);
            Check(at >= 59.5 && at <= 61.5 && v != null && v.StartsWith("over budget for a minute"),
                  $"a steady 6 ms body (budget 4.5) pauses after a minute of running ({at:0.00} s): \"{v}\"");
        }
        // D. far over its budget: pauses after about 10 s
        {
            var w = new BudgetWatch();
            double at = FeedBudget(w, budget, 300, _ => 20.0, 1.0 / 50);
            string v = w.Verdict(budget);
            Check(at >= 9.5 && at <= 11.5 && v != null && v.StartsWith("far over budget"),
                  $"a steady 20 ms body (over 4 x 4.5 ms) at 50 fps pauses after about 10 s ({at:0.00} s): \"{v}\"");
        }
        // E. just under: never pauses
        {
            var w = new BudgetWatch();
            double at = FeedBudget(w, budget, 600, _ => 4.4, vr);
            Check(at < 0, "a steady 4.4 ms body (budget 4.5) runs 10 minutes without pausing");
        }
        // F. gets heavier halfway: pauses once the last minute averages over the budget (about 30 s after 3 -> 6 ms)
        {
            var w = new BudgetWatch();
            double at = FeedBudget(w, budget, 300, t => t < 120 ? 3.0 : 6.0, vr);
            Check(at >= 145 && at <= 156, $"a body going from 3 to 6 ms at 120 s pauses when its minute average passes the budget ({at:0.0} s)");
        }
        // G. a long gap (a disabled renderer, culling) counts as a quarter second of running, not as empty seconds
        {
            var w = new BudgetWatch();
            double before = FeedBudget(w, budget, 30, _ => 3.0, vr);
            w.Add(6.0, 600);   // ten minutes off
            double after = FeedBudget(w, budget, 120, _ => 6.0, vr);
            Check(before < 0 && after >= 25 && after <= 40,
                  $"after a 10-minute gap, a 6 ms body pauses only once a whole minute of running averages over the budget ({after:0.0} s after it resumed)");
        }
        // H. it still stops one that freezes the world again and again: a 2 s frame every 4 s
        {
            var w = new BudgetWatch();
            double t = 0, at = -1;
            int frame = 0;
            while (t < 300 && at < 0)
            {
                bool freeze = frame % 181 == 180;   // 180 frames at 90 fps (2 s), then a 2 s frame: every 4 s of wall time
                double gapBefore = frame == 0 ? 0 : (frame - 1) % 181 == 180 ? 2.0 : vr;
                t += gapBefore;
                w.Add(freeze ? 2000 : 3.0, gapBefore);
                if (w.Verdict(budget) != null) at = t;
                frame++;
            }
            string v = w.Verdict(budget);
            Check(at > 0 && at < 150 && v != null && v.StartsWith("over budget for a minute"),
                  $"a body that freezes the world for 2 s every 4 s still pauses ({at:0} s of wall time): \"{v}\"");
        }
        // I. the status note and the windows' start
        {
            var w = new BudgetWatch();
            FeedBudget(w, budget, 30, _ => 6.0, vr);
            string note = w.Note(budget);
            Check(w.MinuteMs < 0 && w.TenSecondMs > budget && w.Verdict(budget) == null && note.Contains("pauses if that lasts a minute"),
                  $"after 30 s at 6 ms: no minute average yet, no pause, and the Status says so (\"{note.Trim()}\")");
            var calm = new BudgetWatch();
            FeedBudget(calm, budget, 30, _ => 3.0, vr);
            Check(calm.Note(budget) == "", "under the budget the Status gets no note");
        }
        // J. junk in is ignored
        {
            var w = new BudgetWatch();
            for (int i = 0; i < 4000; i++) w.Add(i % 3 == 0 ? double.NaN : i % 3 == 1 ? double.PositiveInfinity : -5, i % 2 == 0 ? double.NaN : 0.05);
            Check(w.Verdict(budget) == null && w.MinuteMs >= 0 && double.IsFinite(w.MinuteMs), "NaN, infinite and negative frame times or gaps count as 0 and never pause it");
        }
    }

    /// <summary>Feeds <paramref name="w"/> one frame every <paramref name="frameGap"/> s for <paramref name="seconds"/> s, the
    /// frame at time t costing cost(t) ms. Returns the time (s from the start of this call) of the first frame after which it
    /// would pause, or -1.</summary>
    static double FeedBudget(BudgetWatch w, float budget, double seconds, Func<double, double> cost, double frameGap)
    {
        int n = (int)Math.Round(seconds / frameGap);
        for (int i = 0; i < n; i++)
        {
            double t = i * frameGap;
            w.Add(cost(t), frameGap);
            if (w.Verdict(budget) != null) return t;
        }
        return -1;
    }
}
