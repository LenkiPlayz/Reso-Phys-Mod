using System.Diagnostics;

namespace Softbody.Core;

/// <summary>
/// 0.29.1: decides when a Softbody, SkinnedSoftbody or Cloth pauses itself for its Budget.
/// Until 0.29.0 one long frame was enough: it pushed the 20-frame average over the budget (or took over 100 to 150 ms on
/// its own), and the component paused until a setting changed. So a lag spike that had nothing to do with it (a hitch, a
/// garbage collection, a world loading) stopped it for good.
/// Now only a sustained overrun does:
/// <list type="bullet">
/// <item>its cost per frame, averaged over the last minute of running, is over the budget; or</item>
/// <item>averaged over the last 10 s, it is over <see cref="HeavyFactor"/> times the budget, so one that really drags the
/// frame rate down stops sooner.</item>
/// </list>
/// A 150 ms spike moves a minute's average (about 5,400 frames at 90 fps) by about 0.03 ms.
/// Time is running time: a longer gap between two frames (a hitch, a disabled renderer, culling) counts as at most
/// <see cref="MaxGap"/>, so a pause never fills the window with empty seconds.
/// Engine-free, so the offline tests drive it with exact frame times; the plugin feeds it on the world thread.
/// </summary>
public sealed class BudgetWatch
{
    /// <summary>The long window: over the budget on average for this long pauses it.</summary>
    public const int MinuteSeconds = 60;
    /// <summary>The short window, for far over the budget.</summary>
    public const int HeavySeconds = 10;
    /// <summary>"Far over": this many times the budget.</summary>
    public const float HeavyFactor = 4f;
    /// <summary>A gap between two frames counts as at most this much running time (s).</summary>
    public const double MaxGap = 0.25;

    // One slot per second of running. The slot at _head is the second being filled; the others are whole seconds.
    readonly double[] _ms = new double[MinuteSeconds + 1];
    readonly int[] _frames = new int[MinuteSeconds + 1];
    int _head, _whole;
    double _inSecond;
    long _stamp;

    /// <summary>The average cost per frame over the last minute of running (ms), or -1 until it has run a minute.</summary>
    public double MinuteMs { get; private set; } = -1;
    /// <summary>The average cost per frame over the last 10 s of running (ms), or -1 until it has run 10 s.</summary>
    public double TenSecondMs { get; private set; } = -1;
    /// <summary>Whole seconds of running recorded (up to a minute).</summary>
    public int Seconds => _whole;

    /// <summary>One frame's cost (ms), timed with the real clock. World thread, once per counted frame.</summary>
    public void Add(double frameMs)
    {
        long now = Stopwatch.GetTimestamp();
        double gap = _stamp == 0 ? 0 : (now - _stamp) / (double)Stopwatch.Frequency;
        _stamp = now;
        Add(frameMs, gap);
    }

    /// <summary>One frame's cost (ms), <paramref name="gapSeconds"/> after the previous one.</summary>
    public void Add(double frameMs, double gapSeconds)
    {
        if (!double.IsFinite(frameMs) || frameMs < 0) frameMs = 0;
        if (!double.IsFinite(gapSeconds) || gapSeconds < 0) gapSeconds = 0;
        _ms[_head] += frameMs;
        _frames[_head]++;
        _inSecond += Math.Min(gapSeconds, MaxGap);
        if (_inSecond < 1.0) return;
        _inSecond -= 1.0;   // MaxGap < 1, so at most one whole second ends per frame
        _head = (_head + 1) % _ms.Length;
        _ms[_head] = 0;
        _frames[_head] = 0;
        if (_whole < MinuteSeconds) _whole++;
        MinuteMs = _whole >= MinuteSeconds ? Average(MinuteSeconds) : -1;
        TenSecondMs = _whole >= HeavySeconds ? Average(HeavySeconds) : -1;
    }

    /// <summary>The average cost per frame over the last <paramref name="seconds"/> whole seconds.</summary>
    double Average(int seconds)
    {
        double ms = 0;
        long frames = 0;
        for (int i = 1; i <= seconds; i++)
        {
            int k = (_head - i + _ms.Length) % _ms.Length;
            ms += _ms[k];
            frames += _frames[k];
        }
        return frames > 0 ? ms / frames : 0;
    }

    /// <summary>Why it should pause now (for its Status), or null to keep running.</summary>
    public string Verdict(float budgetMs)
    {
        if (TenSecondMs >= 0 && TenSecondMs > budgetMs * HeavyFactor)
            return $"far over budget for {HeavySeconds} s ({TenSecondMs:0.00} ms per frame on average, over {HeavyFactor:0} x {budgetMs:0.##} ms)";
        if (MinuteMs >= 0 && MinuteMs > budgetMs)
            return $"over budget for a minute ({MinuteMs:0.00} ms per frame on average > {budgetMs:0.##} ms)";
        return null;
    }

    /// <summary>A note for the running Status while the last 10 s averaged over the budget (changes at most once a second).</summary>
    public string Note(float budgetMs) =>
        TenSecondMs > budgetMs ? $" | over budget lately ({TenSecondMs:0.00} ms > {budgetMs:0.##} ms): it pauses if that lasts a minute" : "";
}
