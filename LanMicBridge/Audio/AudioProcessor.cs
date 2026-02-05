namespace LanMicBridge.Audio;

internal static class AudioProcessor
{
    private const float Epsilon = 1e-9f;

    public static void ApplyGain(short[] pcm, float gain)
    {
        if (Math.Abs(gain - 1f) < 0.001f)
        {
            return;
        }

        for (var i = 0; i < pcm.Length; i++)
        {
            var value = (int)Math.Round(pcm[i] * gain);
            if (value > short.MaxValue)
            {
                value = short.MaxValue;
            }
            else if (value < short.MinValue)
            {
                value = short.MinValue;
            }

            pcm[i] = (short)value;
        }
    }

    public static void ApplyAgcAndGain(
        short[] pcm,
        float rmsDbPre,
        ref float agcGainDb,
        float userGain,
        out float outPeak,
        out float outRms,
        float targetRmsDb,
        float noBoostBelowDb,
        float maxBoostDb,
        float maxCutDb,
        float attack,
        float release,
        float gateFloorDb,
        float gateRangeDb)
    {
        float targetGainDb;
        if (rmsDbPre < noBoostBelowDb)
        {
            targetGainDb = 0f;
        }
        else
        {
            targetGainDb = Math.Clamp(targetRmsDb - rmsDbPre, maxCutDb, maxBoostDb);
        }

        var lerp = targetGainDb > agcGainDb ? attack : release;
        agcGainDb = Lerp(agcGainDb, targetGainDb, lerp);

        var gate = Math.Clamp((rmsDbPre - gateFloorDb) / gateRangeDb, 0f, 1f);
        var gainLinear = DbToLinear(agcGainDb) * userGain * gate;
        ApplyGainWithSoftClip(pcm, gainLinear);

        LanMicBridge.AudioMeter.ComputePeakRms(pcm, out outPeak, out outRms);
    }

    public static void ApplyGainWithSoftClip(short[] pcm, float gain)
    {
        if (Math.Abs(gain - 1f) < 0.001f)
        {
            return;
        }

        for (var i = 0; i < pcm.Length; i++)
        {
            var x = (pcm[i] / 32768f) * gain;
            var y = (float)Math.Tanh(x);
            var value = (int)Math.Round(y * short.MaxValue);
            if (value > short.MaxValue)
            {
                value = short.MaxValue;
            }
            else if (value < short.MinValue)
            {
                value = short.MinValue;
            }

            pcm[i] = (short)value;
        }
    }

    public static float DbToLinear(float db)
    {
        return (float)Math.Pow(10.0, db / 20.0);
    }

    public static float LinearToDb(float linear)
    {
        return 20f * (float)Math.Log10(linear + Epsilon);
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }
}

