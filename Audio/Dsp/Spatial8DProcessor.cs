namespace EightDRealtime.Audio.Dsp;

public sealed class Spatial8DProcessor
{
    private const float TwoPi = MathF.PI * 2f;
    private readonly int _sampleRate;
    private readonly FractionalDelayLine _leftDelay;
    private readonly FractionalDelayLine _rightDelay;
    private readonly OnePoleLowpass _leftLow;
    private readonly OnePoleLowpass _rightLow;
    private readonly BiquadPeakFilter _rearShadowL;
    private readonly BiquadPeakFilter _rearShadowR;
    private readonly BiquadPeakFilter _frontPresenceL;
    private readonly BiquadPeakFilter _frontPresenceR;
    private readonly HallStereoReverb _reverb;
    private float _orbitPhase;
    private float _lastRearGainDb = float.NaN;
    private float _lastFrontGainDb = float.NaN;

    public Spatial8DProcessor(int sampleRate)
    {
        _sampleRate = Math.Max(8_000, sampleRate);
        var maxDelaySamples = Math.Max(16, (int)(_sampleRate * 0.024f));
        _leftDelay = new FractionalDelayLine(maxDelaySamples);
        _rightDelay = new FractionalDelayLine(maxDelaySamples);
        _leftLow = new OnePoleLowpass(0.020f);
        _rightLow = new OnePoleLowpass(0.020f);
        _rearShadowL = new BiquadPeakFilter();
        _rearShadowR = new BiquadPeakFilter();
        _frontPresenceL = new BiquadPeakFilter();
        _frontPresenceR = new BiquadPeakFilter();
        _reverb = new HallStereoReverb(_sampleRate);
    }

    public void Process(float[] interleavedStereo, int frames, SpatialSettings settings)
    {
        if (frames <= 0)
        {
            return;
        }

        var inputGain = Clamp(settings.InputGain, 0f, 2f);
        var outputGain = Clamp(settings.OutputGain, 0f, 2f);
        var threshold = Clamp(settings.LimiterThreshold, 0.5f, 1f);

        if (!settings.Enabled)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var i = frame * 2;
                interleavedStereo[i] *= inputGain * outputGain;
                interleavedStereo[i + 1] *= inputGain * outputGain;
            }

            Limit(interleavedStereo, frames, threshold);
            return;
        }

        var depth = Clamp(settings.Depth, 0f, 1f);
        var radiusControl = Clamp(settings.CircleStrength / 4f, 0f, 1f);
        var hrtf = Clamp(settings.HrtfStrength, 0f, 1f);
        var baseReverb = Clamp(settings.ReverbWet, 0f, 0.65f);
        var orbitHz = Clamp(settings.RotationHz, 0.02f, 0.75f);
        var orbitStep = TwoPi * orbitHz / _sampleRate;

        var orbitRadius = 0.70f + 0.20f * radiusControl;
        var ellipseX = 0.78f + 0.12f * radiusControl;
        var ellipseZ = 1.08f + 0.16f * radiusControl;
        var maxInterEarDelaySamples = _sampleRate * (0.005f + 0.007f * depth) * hrtf;
        var rearPredelaySamples = _sampleRate * 0.006f * depth;
        var wetMix = depth;
        var dryMix = 1f - depth * 0.78f;

        for (var frame = 0; frame < frames; frame++)
        {
            var i = frame * 2;
            var leftIn = interleavedStereo[i] * inputGain;
            var rightIn = interleavedStereo[i + 1] * inputGain;

            var lowLeft = _leftLow.Process(leftIn);
            var lowRight = _rightLow.Process(rightIn);
            var bassCenter = (lowLeft + lowRight) * 0.5f;
            var movingLeftIn = leftIn - lowLeft * 0.82f;
            var movingRightIn = rightIn - lowRight * 0.82f;

            // Elliptical XYZ orbit:
            // X = left/right, Y = outward distance from head center, Z = front/back.
            var cosA = MathF.Cos(_orbitPhase);
            var sinA = MathF.Sin(_orbitPhase);
            var x = cosA * ellipseX;
            var z = sinA * ellipseZ;
            var rear = Smooth01(Clamp(z, 0f, 1f));
            var front = Smooth01(Clamp(-z, 0f, 1f));
            var lateral = Clamp(MathF.Abs(x), 0f, 1f);
            var distance = Clamp(orbitRadius + rear * 0.10f + lateral * 0.05f - front * 0.04f, 0.58f, 0.98f);

            var leftGain = MathF.Sqrt(Clamp((1f + x) * 0.5f, 0f, 1f));
            var rightGain = MathF.Sqrt(Clamp((1f - x) * 0.5f, 0f, 1f));
            var farEarShadow = lateral * (0.16f + 0.22f * hrtf);
            if (x > 0f)
            {
                rightGain *= 1f - farEarShadow;
            }
            else
            {
                leftGain *= 1f - farEarShadow;
            }

            var panEnergy = MathF.Sqrt((leftGain * leftGain + rightGain * rightGain) * 0.5f);
            if (panEnergy > 0.001f)
            {
                leftGain /= panEnergy;
                rightGain /= panEnergy;
            }

            var leftDelaySamples = x < 0f ? -x * maxInterEarDelaySamples : 0f;
            var rightDelaySamples = x > 0f ? x * maxInterEarDelaySamples : 0f;
            var sharedDistanceDelay = rear * rearPredelaySamples + distance * _sampleRate * 0.0025f;
            leftDelaySamples += sharedDistanceDelay;
            rightDelaySamples += sharedDistanceDelay + _sampleRate * 0.00065f * depth;

            var spatialLeft = _leftDelay.Process(movingLeftIn, leftDelaySamples) * leftGain;
            var spatialRight = _rightDelay.Process(movingRightIn, rightDelaySamples) * rightGain;

            var rearGainDb = (-4.5f * rear - 3.0f * distance) * hrtf;
            if (float.IsNaN(_lastRearGainDb) || MathF.Abs(rearGainDb - _lastRearGainDb) > 0.3f)
            {
                _rearShadowL.SetPeak(_sampleRate, 4500f, rearGainDb, 1.0f);
                _rearShadowR.SetPeak(_sampleRate, 4500f, rearGainDb, 1.0f);
                _lastRearGainDb = rearGainDb;
            }

            var shadowL = _rearShadowL.Process(spatialLeft);
            var shadowR = _rearShadowR.Process(spatialRight);
            spatialLeft = Lerp(spatialLeft, shadowL, Clamp(rear + distance * 0.35f, 0f, 1f));
            spatialRight = Lerp(spatialRight, shadowR, Clamp(rear + distance * 0.35f, 0f, 1f));

            var frontGainDb = 1.8f * front * hrtf;
            if (float.IsNaN(_lastFrontGainDb) || MathF.Abs(frontGainDb - _lastFrontGainDb) > 0.3f)
            {
                _frontPresenceL.SetPeak(_sampleRate, 3500f, frontGainDb, 1.2f);
                _frontPresenceR.SetPeak(_sampleRate, 3500f, frontGainDb, 1.2f);
                _lastFrontGainDb = frontGainDb;
            }

            var frontL = _frontPresenceL.Process(spatialLeft);
            var frontR = _frontPresenceR.Process(spatialRight);
            spatialLeft = Lerp(spatialLeft, frontL, front * 0.55f);
            spatialRight = Lerp(spatialRight, frontR, front * 0.55f);

            var distanceAttenuation = 1f - 0.35f * distance * depth;
            spatialLeft *= distanceAttenuation;
            spatialRight *= distanceAttenuation;

            var wet = Clamp(baseReverb + distance * 0.08f + rear * 0.10f, 0f, 0.72f);
            _reverb.Process(spatialLeft, spatialRight, wet, out var roomLeft, out var roomRight);
            spatialLeft = Lerp(spatialLeft, roomLeft, wet * 0.72f);
            spatialRight = Lerp(spatialRight, roomRight, wet * 0.72f);

            interleavedStereo[i] = (movingLeftIn * dryMix + spatialLeft * wetMix + bassCenter * 0.24f) * outputGain;
            interleavedStereo[i + 1] = (movingRightIn * dryMix + spatialRight * wetMix + bassCenter * 0.24f) * outputGain;

            _orbitPhase += orbitStep;
            if (_orbitPhase >= TwoPi)
            {
                _orbitPhase -= TwoPi;
            }
        }

        Limit(interleavedStereo, frames, threshold);
    }

    private static void Limit(float[] interleavedStereo, int frames, float threshold)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            var i = frame * 2;
            var left = interleavedStereo[i];
            var right = interleavedStereo[i + 1];
            var peak = MathF.Max(MathF.Abs(left), MathF.Abs(right));
            if (peak > threshold)
            {
                var gain = threshold / peak;
                left *= gain;
                right *= gain;
            }

            interleavedStereo[i] = SoftClip(left);
            interleavedStereo[i + 1] = SoftClip(right);
        }
    }

    private static float SoftClip(float value)
    {
        if (value > 1.5f)
        {
            return 1f;
        }

        if (value < -1.5f)
        {
            return -1f;
        }

        return value - (value * value * value / 6f);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * Clamp(t, 0f, 1f);

    private static float Smooth01(float value)
    {
        value = Clamp(value, 0f, 1f);
        return value * value * (3f - 2f * value);
    }

    private static float Clamp(float value, float min, float max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }
}

internal sealed class FractionalDelayLine
{
    private readonly float[] _buffer;
    private int _writeIndex;

    public FractionalDelayLine(int maxDelaySamples)
    {
        _buffer = new float[Math.Max(4, maxDelaySamples + 4)];
    }

    public float Process(float input, float delaySamples)
    {
        _buffer[_writeIndex] = input;
        var read = _writeIndex - delaySamples;
        var bufLen = _buffer.Length;
        while (read < 0f)
        {
            read += bufLen;
        }

        while (read >= bufLen)
        {
            read -= bufLen;
        }

        var index0 = (int)read;
        var index1 = (index0 + 1) % bufLen;
        var frac = read - index0;
        var output = _buffer[index0] * (1f - frac) + _buffer[index1] * frac;
        _writeIndex++;
        if (_writeIndex >= bufLen)
        {
            _writeIndex = 0;
        }

        return output;
    }
}

internal sealed class OnePoleLowpass
{
    private readonly float _coefficient;
    private float _state;

    public OnePoleLowpass(float coefficient)
    {
        _coefficient = Math.Clamp(coefficient, 0.001f, 1f);
    }

    public float Process(float input)
    {
        _state += (input - _state) * _coefficient;
        return _state;
    }
}

internal sealed class HallStereoReverb
{
    private readonly FeedbackDelay[] _left;
    private readonly FeedbackDelay[] _right;
    private readonly OnePoleLowpass _airLeft = new(0.055f);
    private readonly OnePoleLowpass _airRight = new(0.055f);

    public HallStereoReverb(int sampleRate)
    {
        _left = new[]
        {
            new FeedbackDelay(Ms(sampleRate, 43.7f), 0.58f),
            new FeedbackDelay(Ms(sampleRate, 71.3f), 0.55f),
            new FeedbackDelay(Ms(sampleRate, 113.9f), 0.50f),
            new FeedbackDelay(Ms(sampleRate, 167.1f), 0.45f)
        };
        _right = new[]
        {
            new FeedbackDelay(Ms(sampleRate, 49.1f), 0.57f),
            new FeedbackDelay(Ms(sampleRate, 83.9f), 0.53f),
            new FeedbackDelay(Ms(sampleRate, 127.7f), 0.49f),
            new FeedbackDelay(Ms(sampleRate, 181.3f), 0.44f)
        };
    }

    public void Process(float leftIn, float rightIn, float wet, out float leftOut, out float rightOut)
    {
        if (wet <= 0.001f)
        {
            leftOut = leftIn;
            rightOut = rightIn;
            return;
        }

        var mono = (leftIn + rightIn) * 0.5f;
        var leftSum = 0f;
        var rightSum = 0f;
        foreach (var delay in _left)
        {
            leftSum += delay.Process(mono);
        }

        foreach (var delay in _right)
        {
            rightSum += delay.Process(mono);
        }

        var airLoss = 0.32f + wet * 0.32f;
        leftOut = leftIn + Lerp(leftSum, _airLeft.Process(leftSum), airLoss) * 0.105f;
        rightOut = rightIn + Lerp(rightSum, _airRight.Process(rightSum), airLoss) * 0.105f;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    private static int Ms(int sampleRate, float ms) => Math.Max(1, (int)(sampleRate * ms / 1000f));
}

internal sealed class FeedbackDelay
{
    private readonly float[] _buffer;
    private readonly float _feedback;
    private int _index;

    public FeedbackDelay(int samples, float feedback)
    {
        _buffer = new float[Math.Max(1, samples)];
        _feedback = feedback;
    }

    public float Process(float input)
    {
        var delayed = _buffer[_index];
        _buffer[_index] = input + delayed * _feedback;
        _index++;
        if (_index >= _buffer.Length)
        {
            _index = 0;
        }

        return delayed;
    }
}

internal sealed class BiquadPeakFilter
{
    private const float TwoPi = MathF.PI * 2f;
    private float _b0 = 1f, _b1, _b2, _a1, _a2;
    private float _x1, _x2, _y1, _y2;

    public void SetPeak(int sampleRate, float centerFreq, float gainDb, float q)
    {
        var w0 = TwoPi * MathF.Min(centerFreq, sampleRate * 0.49f) / sampleRate;
        var cosW0 = MathF.Cos(w0);
        var sinW0 = MathF.Sin(w0);
        var alpha = sinW0 / (2f * q);
        var a = MathF.Pow(10f, gainDb / 40f);

        _b0 = 1f + alpha * a;
        _b1 = -2f * cosW0;
        _b2 = 1f - alpha * a;
        var a0 = 1f + alpha / a;
        _a1 = -2f * cosW0 / a0;
        _a2 = (1f - alpha / a) / a0;
        _b0 /= a0;
        _b1 /= a0;
        _b2 /= a0;
    }

    public float Process(float input)
    {
        var output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = input;
        _y2 = _y1;
        _y1 = output;
        return output;
    }
}
