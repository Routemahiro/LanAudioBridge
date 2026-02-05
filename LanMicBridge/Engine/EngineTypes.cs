namespace LanMicBridge.Engine;

internal enum JitterMode
{
    LowLatency = 0,
    Stable = 1,
    UltraStable = 2
}

internal enum CaptureApiMode
{
    Wasapi = 0,
    Mme = 1
}

internal enum SendQuality
{
    Low = 0,
    Standard = 1,
    High = 2,
    Ultra = 3
}

internal enum SendMode
{
    Opus = 0,
    PcmDirect = 1
}

internal readonly record struct ReceiverStats(long Packets, int LossPercent, int JitterMs, int DelayMs);

internal readonly record struct AudioLevel(float PeakDb, float RmsDb);

