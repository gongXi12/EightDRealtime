namespace EightDRealtime.Audio.Dsp;

/// <summary>
/// 8D 水平环绕处理器。
/// 声音在平行于地面的圆上旋转：左 → 前 → 右 → 后 → 左。
/// 左右感来自 ITD（耳间时间差）+ ILD（耳间声级差）。
/// 前后感来自混响量和频谱变化（后方更暗、混响更多；前方更亮、更直接）。
/// </summary>
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
    private readonly SimpleStereoReverb _reverb;
    private float _orbitPhase;
    private float _lastRearGainDb = float.NaN;
    private float _lastFrontGainDb = float.NaN;
    private float _rearBlend;  // 0 = off, 1 = full
    private float _frontBlend; // 0 = off, 1 = full

    public Spatial8DProcessor(int sampleRate)
    {
        _sampleRate = Math.Max(8_000, sampleRate);
        var maxDelaySamples = Math.Max(16, (int)(_sampleRate * 0.0035f));
        _leftDelay = new FractionalDelayLine(maxDelaySamples);
        _rightDelay = new FractionalDelayLine(maxDelaySamples);
        _leftLow = new OnePoleLowpass(0.020f);
        _rightLow = new OnePoleLowpass(0.020f);
        _rearShadowL = new BiquadPeakFilter();
        _rearShadowR = new BiquadPeakFilter();
        _frontPresenceL = new BiquadPeakFilter();
        _frontPresenceR = new BiquadPeakFilter();
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
        var circle = Clamp(settings.CircleStrength, 0f, 4f);
        var hrtf = Clamp(settings.HrtfStrength, 0f, 1f);
        var baseReverb = Clamp(settings.ReverbWet, 0f, 0.65f);
        var orbitStep = TwoPi * Clamp(settings.RotationHz, 0f, 0.75f) / _sampleRate;

        // ITD: 最大延迟约 0.7ms（对应头部半径 ~10cm 的最大路径差）
        var maxItdSamples = _sampleRate * 0.00070f * hrtf;

        // Dry/wet mix: depth 控制空间效果强度
        var wetMix = depth;
        var dryMix = 1f - depth * 0.65f;

        for (var frame = 0; frame < frames; frame++)
        {
            var i = frame * 2;
            var leftIn = interleavedStereo[i] * inputGain;
            var rightIn = interleavedStereo[i + 1] * inputGain;

            // --- 低频分离：bass 固定居中锚定，避免低频飘移 ---
            var lowLeft = _leftLow.Process(leftIn);
            var lowRight = _rightLow.Process(rightIn);
            var bassCenter = (lowLeft + lowRight) * 0.5f;

            // --- 水平环绕轨道 ---
            // 0 = 左, π/2 = 前, π = 右, 3π/2 = 后
            var cosA = MathF.Cos(_orbitPhase);
            var sinA = MathF.Sin(_orbitPhase);

            // ILD（声级差）：equal-power panning，水平分量
            var leftGain = MathF.Sqrt(Clamp((1f + cosA) * 0.5f, 0f, 1f));
            var rightGain = MathF.Sqrt(Clamp((1f - cosA) * 0.5f, 0f, 1f));

            // ITD（时间差）：仅由头部尺寸决定，不受环绕范围影响
            // 左声道：声源在右时延迟（cosA < 0 → 远耳是左耳），声源在左时不延迟
            // 右声道：声源在左时延迟（cosA > 0 → 远耳是右耳），声源在右时不延迟
            var itdLeft = cosA < 0f ? -cosA * maxItdSamples : 0f;
            var itdRight = cosA > 0f ? cosA * maxItdSamples : 0f;

            // 空间化信号：左右声道分别进入延迟线，保留原始立体声宽度
            var spatialLeft = _leftDelay.Process(leftIn, itdLeft) * leftGain;
            var spatialRight = _rightDelay.Process(rightIn, itdRight) * rightGain;

            // --- 前后频谱处理 ---
            // sin > 0 = 后方（head shadow: 高频衰减）, sin < 0 = 前方（presence boost）
            var rear = Clamp(sinA * circle, 0f, 1f);
            var front = Clamp(-sinA * circle, 0f, 1f);

            // 迟滞区间：避免在阈值附近反复开/关导致咔嗒声
            // 开启阈值 0.08，关闭阈值 0.04
            _rearBlend = rear > 0.08f ? 1f : rear < 0.04f ? 0f : _rearBlend;
            _frontBlend = front > 0.08f ? 1f : front < 0.04f ? 0f : _frontBlend;

            if (_rearBlend > 0f)
            {
                // Head shadow: 只削弱 4.5kHz 附近的中高频（峰值），不影响低频
                var rearGainDb = -4f * rear * hrtf;
                // 仅在系数变化 > 0.3dB 时重算，避免每样本更新导致的噪声
                if (float.IsNaN(_lastRearGainDb) || MathF.Abs(rearGainDb - _lastRearGainDb) > 0.3f)
                {
                    _rearShadowL.SetPeak(_sampleRate, 4500f, rearGainDb, 1.0f);
                    _rearShadowR.SetPeak(_sampleRate, 4500f, rearGainDb, 1.0f);
                    _lastRearGainDb = rearGainDb;
                }

                var rearL = _rearShadowL.Process(spatialLeft);
                var rearR = _rearShadowR.Process(spatialRight);
                spatialLeft = Lerp(spatialLeft, rearL, _rearBlend);
                spatialRight = Lerp(spatialRight, rearR, _rearBlend);
            }

            if (_frontBlend > 0f)
            {
                var frontGainDb = 3f * front * hrtf;
                if (float.IsNaN(_lastFrontGainDb) || MathF.Abs(frontGainDb - _lastFrontGainDb) > 0.3f)
                {
                    _frontPresenceL.SetPeak(_sampleRate, 3500f, frontGainDb, 1.2f);
                    _frontPresenceR.SetPeak(_sampleRate, 3500f, frontGainDb, 1.2f);
                    _lastFrontGainDb = frontGainDb;
                }

                var frontL = _frontPresenceL.Process(spatialLeft);
                var frontR = _frontPresenceR.Process(spatialRight);
                spatialLeft = Lerp(spatialLeft, frontL, _frontBlend);
                spatialRight = Lerp(spatialRight, frontR, _frontBlend);
            }

            // --- 混响：后方更多（距离感），前方更少（贴耳感）---
            var wet = Clamp(baseReverb + rear * 0.18f + circle * 0.03f, 0f, 0.72f);
            _reverb.Process(spatialLeft, spatialRight, wet, out var roomL, out var roomR);
            spatialLeft = Lerp(spatialLeft, roomL, wet * 0.6f);
            spatialRight = Lerp(spatialRight, roomR, wet * 0.6f);

            // --- 最终混音 ---
            interleavedStereo[i] = (leftIn * dryMix + spatialLeft * wetMix + bassCenter * 0.12f) * outputGain;
            interleavedStereo[i + 1] = (rightIn * dryMix + spatialRight * wetMix + bassCenter * 0.12f) * outputGain;

            // --- 推进轨道相位 ---
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
            new(Ms(sampleRate, 17.9f), 0.48f),
            new(Ms(sampleRate, 31.3f), 0.45f),
            new(Ms(sampleRate, 49.1f), 0.39f)
        };
        _right = new FeedbackDelay[]
        {
            new(Ms(sampleRate, 21.7f), 0.47f),
            new(Ms(sampleRate, 35.9f), 0.43f),
            new(Ms(sampleRate, 55.3f), 0.38f)
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

internal sealed class BiquadLowShelfFilter
{
    private const float TwoPi = MathF.PI * 2f;
    private float _b0 = 1f, _b1, _b2, _a1, _a2;
    private float _x1, _x2, _y1, _y2;

    public void SetShelf(int sampleRate, float cutoffFreq, float gainDb, float q)
    {
        var w0 = TwoPi * MathF.Min(cutoffFreq, sampleRate * 0.49f) / sampleRate;
        var cosW0 = MathF.Cos(w0);
        var sinW0 = MathF.Sin(w0);
        var alpha = sinW0 / (2f * q);
        var a = MathF.Pow(10f, gainDb / 40f);
        var sqrtA = MathF.Sqrt(a);

        var a0 = (a + 1f) + (a - 1f) * cosW0 + 2f * sqrtA * alpha;
        _b0 = a * ((a + 1f) - (a - 1f) * cosW0 + 2f * sqrtA * alpha) / a0;
        _b1 = 2f * a * ((a - 1f) - (a + 1f) * cosW0) / a0;
        _b2 = a * ((a + 1f) - (a - 1f) * cosW0 - 2f * sqrtA * alpha) / a0;
        _a1 = -2f * ((a - 1f) + (a + 1f) * cosW0) / a0;
        _a2 = ((a + 1f) + (a - 1f) * cosW0 - 2f * sqrtA * alpha) / a0;
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
