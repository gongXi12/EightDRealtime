namespace EightDRealtime.Audio;

internal sealed class StereoRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacityFrames;
    private int _readFrame;
    private int _writeFrame;
    private int _availableFrames;

    public StereoRingBuffer(int capacityFrames)
    {
        _capacityFrames = Math.Max(128, capacityFrames);
        _buffer = new float[_capacityFrames * 2];
    }

    public int AvailableFrames => _availableFrames;

    public void Write(float[] source, int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        var sourceOffsetFrame = 0;
        if (frames > _capacityFrames)
        {
            sourceOffsetFrame = frames - _capacityFrames;
            frames = _capacityFrames;
        }

        var overflow = _availableFrames + frames - _capacityFrames;
        if (overflow > 0)
        {
            Drop(overflow);
        }

        for (var frame = 0; frame < frames; frame++)
        {
            var sourceIndex = (sourceOffsetFrame + frame) * 2;
            var targetIndex = _writeFrame * 2;
            _buffer[targetIndex] = source[sourceIndex];
            _buffer[targetIndex + 1] = source[sourceIndex + 1];
            _writeFrame = (_writeFrame + 1) % _capacityFrames;
        }

        _availableFrames += frames;
    }

    public int Read(float[] target, int frames)
    {
        var framesToRead = Math.Min(frames, _availableFrames);
        for (var frame = 0; frame < framesToRead; frame++)
        {
            var sourceIndex = _readFrame * 2;
            var targetIndex = frame * 2;
            target[targetIndex] = _buffer[sourceIndex];
            target[targetIndex + 1] = _buffer[sourceIndex + 1];
            _readFrame = (_readFrame + 1) % _capacityFrames;
        }

        _availableFrames -= framesToRead;
        return framesToRead;
    }

    private void Drop(int frames)
    {
        var framesToDrop = Math.Min(frames, _availableFrames);
        _readFrame = (_readFrame + framesToDrop) % _capacityFrames;
        _availableFrames -= framesToDrop;
    }
}

internal static class AudioBlockResampler
{
    public static int Resample(float[] input, int inputFrames, int inputRate, int outputRate, ref float[] output)
    {
        if (inputFrames <= 0)
        {
            return 0;
        }

        if (inputRate == outputRate)
        {
            EnsureCapacity(ref output, inputFrames * 2);
            Array.Copy(input, output, inputFrames * 2);
            return inputFrames;
        }

        var outputFrames = Math.Max(1, (int)Math.Round(inputFrames * (double)outputRate / inputRate));
        EnsureCapacity(ref output, outputFrames * 2);
        var ratio = inputRate / (double)outputRate;

        for (var frame = 0; frame < outputFrames; frame++)
        {
            var sourcePosition = (frame + 0.5d) * ratio - 0.5d;
            if (sourcePosition < 0d)
            {
                sourcePosition = 0d;
            }

            var index0 = (int)sourcePosition;
            var index1 = Math.Min(index0 + 1, inputFrames - 1);
            var fraction = (float)(sourcePosition - index0);
            var outIndex = frame * 2;
            var in0 = index0 * 2;
            var in1 = index1 * 2;
            output[outIndex] = input[in0] + (input[in1] - input[in0]) * fraction;
            output[outIndex + 1] = input[in0 + 1] + (input[in1 + 1] - input[in0 + 1]) * fraction;
        }

        return outputFrames;
    }

    private static void EnsureCapacity(ref float[] buffer, int samples)
    {
        if (buffer.Length < samples)
        {
            buffer = new float[samples];
        }
    }
}

internal static class AudioBufferConverter
{
    public static unsafe void ReadToStereo(
        IntPtr source,
        uint frameCount,
        AudioFormat format,
        bool silent,
        float[] target)
    {
        var frames = checked((int)frameCount);
        if (silent || source == IntPtr.Zero)
        {
            Array.Clear(target, 0, frames * 2);
            return;
        }

        var basePointer = (byte*)source;
        var channels = Math.Max(1, (int)format.Channels);
        var bytesPerSample = Math.Max(1, format.BytesPerFrame / channels);
        for (var frame = 0; frame < frames; frame++)
        {
            var framePointer = basePointer + frame * format.BytesPerFrame;
            var left = ReadSample(framePointer, format, bytesPerSample);
            var right = channels > 1
                ? ReadSample(framePointer + bytesPerSample, format, bytesPerSample)
                : left;
            var targetIndex = frame * 2;
            target[targetIndex] = left;
            target[targetIndex + 1] = right;
        }
    }

    public static unsafe void WriteFromStereo(
        IntPtr target,
        uint frameCount,
        AudioFormat format,
        float[] source,
        int validFrames)
    {
        var frames = checked((int)frameCount);
        var basePointer = (byte*)target;
        var channels = Math.Max(1, (int)format.Channels);
        var bytesPerSample = Math.Max(1, format.BytesPerFrame / channels);

        for (var frame = 0; frame < frames; frame++)
        {
            var sourceIndex = frame * 2;
            var left = frame < validFrames ? source[sourceIndex] : 0f;
            var right = frame < validFrames ? source[sourceIndex + 1] : 0f;
            var mono = (left + right) * 0.5f;
            var framePointer = basePointer + frame * format.BytesPerFrame;

            for (var channel = 0; channel < channels; channel++)
            {
                var sample = channel switch
                {
                    0 => channels == 1 ? mono : left,
                    1 => right,
                    _ => 0f
                };
                WriteSample(framePointer + channel * bytesPerSample, format, bytesPerSample, sample);
            }
        }
    }

    private static unsafe float ReadSample(byte* pointer, AudioFormat format, int bytesPerSample)
    {
        if (format.SampleKind == AudioSampleKind.Float && bytesPerSample >= 4)
        {
            return *(float*)pointer;
        }

        return bytesPerSample switch
        {
            1 => ((int)*pointer - 128) / 128f,
            2 => *(short*)pointer / 32768f,
            3 => ReadInt24(pointer) / 8_388_608f,
            _ => *(int*)pointer / 2_147_483_648f
        };
    }

    private static unsafe void WriteSample(byte* pointer, AudioFormat format, int bytesPerSample, float value)
    {
        value = Math.Clamp(value, -1f, 1f);
        if (format.SampleKind == AudioSampleKind.Float && bytesPerSample >= 4)
        {
            *(float*)pointer = value;
            return;
        }

        switch (bytesPerSample)
        {
            case 1:
                *pointer = (byte)Math.Clamp((int)MathF.Round(value * 127f + 128f), 0, 255);
                break;
            case 2:
                *(short*)pointer = (short)Math.Clamp((int)MathF.Round(value * 32767f), short.MinValue, short.MaxValue);
                break;
            case 3:
                WriteInt24(pointer, (int)Math.Clamp(MathF.Round(value * 8_388_607f), -8_388_608f, 8_388_607f));
                break;
            default:
                *(int*)pointer = (int)Math.Clamp(value * 2_147_483_647f, int.MinValue, int.MaxValue);
                break;
        }
    }

    private static unsafe int ReadInt24(byte* pointer)
    {
        var value = pointer[0] | (pointer[1] << 8) | (pointer[2] << 16);
        if ((value & 0x800000) != 0)
        {
            value |= unchecked((int)0xFF000000);
        }

        return value;
    }

    private static unsafe void WriteInt24(byte* pointer, int value)
    {
        pointer[0] = (byte)(value & 0xFF);
        pointer[1] = (byte)((value >> 8) & 0xFF);
        pointer[2] = (byte)((value >> 16) & 0xFF);
    }
}
