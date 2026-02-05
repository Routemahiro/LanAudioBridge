namespace LanMicBridge;

partial class Form1
{
    private static void ApplyGain(short[] pcm, float gain)
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

    private static void ApplyAgcAndGain(
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

        AudioMeter.ComputePeakRms(pcm, out outPeak, out outRms);
    }

    private static void ApplyGainWithSoftClip(short[] pcm, float gain)
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

    private static float DbToLinear(float db)
    {
        return (float)Math.Pow(10.0, db / 20.0);
    }

    private static float LinearToDb(float linear)
    {
        return 20f * (float)Math.Log10(linear + 1e-9f);
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    private void FillTestToneFrame(short[] pcm)
    {
        var amplitude = (float)Math.Pow(10, TestToneLevelDb / 20.0);
        var phaseStep = 2.0 * Math.PI * TestToneHz / SampleRate;

        for (var i = 0; i < pcm.Length; i++)
        {
            var value = Math.Sin(_sendTestPhase) * amplitude;
            _sendTestPhase += phaseStep;
            if (_sendTestPhase >= Math.PI * 2)
            {
                _sendTestPhase -= Math.PI * 2;
            }

            pcm[i] = (short)Math.Round(Math.Clamp(value, -1.0, 1.0) * short.MaxValue);
        }
    }
}
