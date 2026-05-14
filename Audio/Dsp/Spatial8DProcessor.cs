namespace EightDRealtime.Audio.Dsp;

public sealed class Spatial8DProcessor
{
    private const float TwoPi = MathF.PI * 2f;
    private readonly int _sampleRate;
    private readonly FractionalDelayLine _leftDelay;
    private readonly FractionalDelayLine _rightDelay;
    private readonly OnePoleLowpass _leftLow;
    private readonly OnePoleLowpass _rightLow;
    private readonly OnePoleLowpass _leftHighReference;
    private readonly OnePoleLowpass _rightHighReference;
    private readonly OnePoleLowpass _heightSmoother;
    private readonly SimpleStereoReverb _reverb;
    private float _orbitPhase = -MathF.PI / 2f;
    private float _heightPhase = -MathF.PI / 2f;

    public Spatial8DProcessor(int sampleRate)
    {
        _sampleRate = Math.Max(8_000, sampleRate);
        var maxDelaySamples = Math.Max(16, (int)(_sampleRate * 0.0035f));
        _leftDelay = new FractionalDelayLine(maxDelaySamples);
        _rightDelay = new FractionalDelayLine(maxDelaySamples);
        _leftLow = new OnePoleLowpass(0.020f);
        _rightLow = new OnePoleLowpass(0.020f);
        _leftHighReference = new OnePoleLowpass(0.16f);
        _rightHighReference = new OnePoleLowpass(0.16f);
        _heightSmoother = new OnePoleLowpass(0.006f);
        _reverb = new SimpleStereoReverb(_sampleRate);
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
        var circle = Clamp(settings.CircleStrength, 0f, 1f);
        var heightDepth = Clamp(settings.HeightDepth, 0f, 1f);
        var heightRate = Clamp(settings.HeightRate, 0.25f, 2.5f);
        var hrtf = Clamp(settings.HrtfStrength, 0f, 1f);
        var baseReverb = Clamp(settings.ReverbWet, 0f, 0.65f);
        var orbitStep = TwoPi * Clamp(settings.RotationHz, 0f, 0.75f) / _sampleRate;
        var heightStep = orbitStep * heightRate;
        var dryKeep = 1f - depth * (0.68f + circle * 0.18f);
        var movingGain = 0.78f + depth * 0.56f;
        var maxItdSamples = _sampleRate * (0.00030f + 0.00055f * hrtf) * circle;

        for (var frame = 0; frame < frames; frame++)
        {
            var i = frame * 2;
            var leftIn = interleavedStereo[i] * inputGain;
            var rightIn = interleavedStereo[i + 1] * inputGain;

            var lowLeft = _leftLow.Process(leftIn);
            var lowRight = _rightLow.Process(rightIn);
            var lowCenter = (lowLeft + lowRight) * 0.5f;
            var highLeft = leftIn - lowLeft;
            var highRight = rightIn - lowRight;
            var highMid = (highLeft + highRight) * 0.5f;
            var highSide = (highLeft - highRight) * 0.5f;

            // Vertical crown orbit: left/right panning and up/down cues share the same round path.
            var horizontal = MathF.Sin(_orbitPhase);
            var heightRaw = MathF.Cos(_heightPhase);
            var smoothedHeight = _heightSmoother.Process(heightRaw);
            var shapedHeight = ShapeHeight(smoothedHeight);
            var height = shapedHeight * heightDepth;
            var verticalAmount = Clamp(MathF.Abs(height), 0f, 1f);
            var orbitX = horizontal * circle * (1f - verticalAmount * 0.42f);
            var lateral = MathF.Abs(orbitX);
            var up = Smooth01(Clamp(height * 1.22f, 0f, 1f));
            var down = Smooth01(Clamp(-height * 1.22f, 0f, 1f));

            var movingSource = highMid + highSide * (0.18f * (1f - circle));
            var heightAirLeft = movingSource - _leftHighReference.Process(movingSource);
            var heightAirRight = movingSource - _rightHighReference.Process(movingSource);
            var heightAir = (heightAirLeft + heightAirRight) * 0.5f;
            var upperAir = up * heightDepth * (0.42f + 0.58f * hrtf);
            var lowerWarmth = down * heightDepth * (0.25f + 0.18f * hrtf);
            movingSource *= 1f + up * (0.05f + 0.05f * hrtf) - down * (0.07f + 0.04f * hrtf);
            movingSource += heightAir * upperAir;
            movingSource += lowCenter * lowerWarmth;
            movingSource *= 1f / (1f + up * 0.12f + down * 0.09f);

            var leftGain = MathF.Sqrt(Clamp((1f - orbitX) * 0.5f, 0f, 1f));
            var rightGain = MathF.Sqrt(Clamp((1f + orbitX) * 0.5f, 0f, 1f));
            var farEarShadow = lateral * (0.24f + 0.28f * hrtf);
            if (orbitX > 0f)
            {
                leftGain *= 1f - farEarShadow;
                rightGain *= 1f + lateral * 0.08f;
            }
            else
            {
                rightGain *= 1f - farEarShadow;
                leftGain *= 1f + lateral * 0.08f;
            }

            var panEnergy = MathF.Sqrt((leftGain * leftGain + rightGain * rightGain) * 0.5f);
            if (panEnergy > 0.001f)
            {
                leftGain /= panEnergy;
                rightGain /= panEnergy;
            }

            var leftDelaySamples = orbitX > 0f ? orbitX * maxItdSamples : 0f;
            var rightDelaySamples = orbitX < 0f ? -orbitX * maxItdSamples : 0f;
            var spatialLeft = _leftDelay.Process(movingSource, leftDelaySamples) * leftGain;
            var spatialRight = _rightDelay.Process(movingSource, rightDelaySamples) * rightGain;

            var heightTone = 1f + up * 0.025f - down * 0.015f;
            spatialLeft *= heightTone;
            spatialRight *= heightTone;

            var wet = Clamp(baseReverb + lateral * 0.04f + up * 0.035f + down * 0.015f, 0f, 0.72f);
            _reverb.Process(spatialLeft, spatialRight, wet, out var roomLeft, out var roomRight);
            spatialLeft = Lerp(spatialLeft, roomLeft, wet * 0.62f);
            spatialRight = Lerp(spatialRight, roomRight, wet * 0.62f);

            var dryLeft = (lowCenter * 0.42f + highLeft * 0.58f) * dryKeep;
            var dryRight = (lowCenter * 0.42f + highRight * 0.58f) * dryKeep;
            var bassAnchor = lowCenter * (0.11f + (1f - depth) * 0.22f);
            interleavedStereo[i] = (dryLeft + spatialLeft * movingGain + bassAnchor) * outputGain;
            interleavedStereo[i + 1] = (dryRight + spatialRight * movingGain + bassAnchor) * outputGain;

            _orbitPhase += orbitStep;
            _heightPhase += heightStep;
            if (_orbitPhase >= TwoPi)
            {
                _orbitPhase -= TwoPi;
            }

            if (_heightPhase >= TwoPi)
            {
                _heightPhase -= TwoPi;
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

    private static float ShapeHeight(float value)
    {
        var amount = MathF.Pow(Clamp(MathF.Abs(value), 0f, 1f), 0.72f);
        return value < 0f ? -amount : amount;
    }

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
        while (read < 0f)
        {
            read += _buffer.Length;
        }

        var index0 = (int)read;
        var index1 = (index0 + 1) % _buffer.Length;
        var frac = read - index0;
        var output = _buffer[index0] * (1f - frac) + _buffer[index1] * frac;
        _writeIndex++;
        if (_writeIndex >= _buffer.Length)
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

internal sealed class SimpleStereoReverb
{
    private readonly FeedbackDelay[] _left;
    private readonly FeedbackDelay[] _right;

    public SimpleStereoReverb(int sampleRate)
    {
        _left = new FeedbackDelay[]
        {
            new FeedbackDelay(Ms(sampleRate, 17.9f), 0.48f),
            new FeedbackDelay(Ms(sampleRate, 31.3f), 0.45f),
            new FeedbackDelay(Ms(sampleRate, 49.1f), 0.39f)
        };
        _right = new FeedbackDelay[]
        {
            new FeedbackDelay(Ms(sampleRate, 21.7f), 0.47f),
            new FeedbackDelay(Ms(sampleRate, 35.9f), 0.43f),
            new FeedbackDelay(Ms(sampleRate, 55.3f), 0.38f)
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

        leftOut = leftIn + leftSum * 0.14f;
        rightOut = rightIn + rightSum * 0.14f;
    }

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
