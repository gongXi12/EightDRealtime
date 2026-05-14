using EightDRealtime.Audio.Dsp;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace EightDRealtime.Audio;

public enum CaptureMode
{
    EndpointLoopback,
    ProcessExclusionLoopback
}

public sealed class Wasapi8DAudioEngine : IDisposable
{
    private readonly object _settingsLock = new();
    private Thread? _worker;
    private volatile bool _stopRequested;
    private SpatialSettings _settings = SpatialSettings.FromPreset(SpatialPreset.Default);

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<double>? LatencyChanged;
    public event EventHandler? Stopped;

    public bool IsRunning { get; private set; }

    public void Start(
        AudioDevice captureEndpoint,
        AudioDevice outputEndpoint,
        SpatialSettings settings,
        CaptureMode captureMode,
        bool suppressOriginalAudio)
    {
        if (IsRunning)
        {
            return;
        }

        if (captureMode == CaptureMode.EndpointLoopback
            && string.Equals(captureEndpoint.Id, outputEndpoint.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("普通捕获模式需要选择不同的捕获和输出设备。请开启同设备模式以避免回授。");
        }

        lock (_settingsLock)
        {
            _settings = settings;
        }

        _stopRequested = false;
        _worker = new Thread(() => RunAudioLoop(captureEndpoint, outputEndpoint, captureMode, suppressOriginalAudio))
        {
            IsBackground = true,
            Name = "8D WASAPI Audio"
        };
        IsRunning = true;
        _worker.Start();
    }

    public void UpdateSettings(SpatialSettings settings)
    {
        lock (_settingsLock)
        {
            _settings = settings;
        }
    }

    public void Stop()
    {
        _stopRequested = true;
        var worker = _worker;
        if (worker is not null && worker.IsAlive)
        {
            worker.Join(1_500);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private SpatialSettings SnapshotSettings()
    {
        lock (_settingsLock)
        {
            return _settings;
        }
    }

    private void RunAudioLoop(
        AudioDevice captureEndpoint,
        AudioDevice outputEndpoint,
        CaptureMode captureMode,
        bool suppressOriginalAudio)
    {
        IAudioClient? captureClient = null;
        IAudioClient? renderClient = null;
        IAudioCaptureClient? capture = null;
        IAudioRenderClient? render = null;
        AudioSessionSilencer? originalAudioSilencer = null;
        IntPtr captureFormatPointer = IntPtr.Zero;
        IntPtr renderFormatPointer = IntPtr.Zero;
        var comInitialized = false;

        try
        {
            var coHr = CoreAudioInterop.CoInitializeEx(IntPtr.Zero, CoInit.MultiThreaded);
            comInitialized = coHr >= 0;
            if (coHr < 0 && coHr != unchecked((int)0x80010106))
            {
                Marshal.ThrowExceptionForHR(coHr);
            }

            RaiseStatus(captureMode == CaptureMode.ProcessExclusionLoopback
                ? "正在打开系统捕获（排除本应用）"
                : $"正在打开捕获设备：{captureEndpoint.Name}");
            renderClient = CoreAudioDeviceEnumerator.ActivateAudioClient(outputEndpoint.Id);
            CoreAudioInterop.Check(renderClient.GetMixFormat(out renderFormatPointer), "无法读取输出设备格式。");
            var renderFormat = AudioFormat.FromPointer(renderFormatPointer);

            AudioFormat captureFormat;
            var captureAlreadyInitialized = false;
            var session = Guid.Empty;
            var bufferDuration = CoreAudioInterop.ReftimesPerSecond / 20;

            if (captureMode == CaptureMode.ProcessExclusionLoopback)
            {
                captureFormat = AudioFormat.Pcm16Stereo(44_100);
                captureFormatPointer = CoreAudioDeviceEnumerator.AllocatePcm16StereoWaveFormat(captureFormat.SampleRate);
                captureClient = CoreAudioDeviceEnumerator.ActivateProcessLoopbackClientExcludingCurrentProcess(
                    captureFormatPointer,
                    bufferDuration);
                captureAlreadyInitialized = true;
            }
            else
            {
                captureClient = CoreAudioDeviceEnumerator.ActivateAudioClient(captureEndpoint.Id);
                CoreAudioInterop.Check(captureClient.GetMixFormat(out captureFormatPointer), "无法读取捕获设备格式。");
                captureFormat = AudioFormat.FromPointer(captureFormatPointer);
            }

            if (captureFormat.Channels < 1 || renderFormat.Channels < 1)
            {
                throw new NotSupportedException("音频设备至少需要提供 1 个声道。");
            }

            if (!captureAlreadyInitialized)
            {
                CoreAudioInterop.Check(
                    captureClient.Initialize(
                        AudioClientShareMode.Shared,
                        AudioClientStreamFlags.Loopback,
                        bufferDuration,
                        0,
                        captureFormatPointer,
                        ref session),
                    "无法初始化 WASAPI 回环捕获。");
            }

            session = Guid.Empty;
            CoreAudioInterop.Check(
                renderClient.Initialize(
                    AudioClientShareMode.Shared,
                    AudioClientStreamFlags.None,
                    bufferDuration,
                    0,
                    renderFormatPointer,
                    ref session),
                "无法初始化音频输出。");

            var captureGuid = typeof(IAudioCaptureClient).GUID;
            var renderGuid = typeof(IAudioRenderClient).GUID;
            CoreAudioInterop.Check(captureClient.GetService(ref captureGuid, out var captureService), "无法打开捕获服务。");
            CoreAudioInterop.Check(renderClient.GetService(ref renderGuid, out var renderService), "无法打开输出服务。");
            capture = (IAudioCaptureClient)captureService;
            render = (IAudioRenderClient)renderService;

            CoreAudioInterop.Check(renderClient.GetBufferSize(out var renderBufferFrames), "无法读取输出缓冲区大小。");
            PrimeRenderBuffer(render, renderBufferFrames);

            var processor = new Spatial8DProcessor(captureFormat.SampleRate);
            var queue = new StereoRingBuffer(renderFormat.SampleRate * 4);
            var stopwatch = Stopwatch.StartNew();
            var nextLatencyUpdate = 0L;
            var captureBuffer = Array.Empty<float>();
            var resampleBuffer = Array.Empty<float>();
            var outputBuffer = Array.Empty<float>();

            CoreAudioInterop.Check(renderClient.Start(), "无法启动输出流。");
            CoreAudioInterop.Check(captureClient.Start(), "无法启动捕获流。");
            if (captureMode == CaptureMode.ProcessExclusionLoopback && suppressOriginalAudio)
            {
                originalAudioSilencer = new AudioSessionSilencer(outputEndpoint.Id);
                originalAudioSilencer.Apply();
            }

            RaiseStatus(captureMode == CaptureMode.ProcessExclusionLoopback
                ? suppressOriginalAudio
                    ? $"运行中：仅保留 8D 处理声 -> {outputEndpoint.Name}"
                    : $"运行中：所有应用（排除本应用） -> {outputEndpoint.Name}"
                : $"运行中：{captureEndpoint.Name} -> {outputEndpoint.Name}");

            while (!_stopRequested)
            {
                DrainCapturePackets(capture, captureFormat, renderFormat, processor, queue, ref captureBuffer, ref resampleBuffer);
                FillRenderBuffer(renderClient, render, renderFormat, renderBufferFrames, queue, ref outputBuffer);

                if (stopwatch.ElapsedMilliseconds >= nextLatencyUpdate)
                {
                    nextLatencyUpdate = stopwatch.ElapsedMilliseconds + 250;
                    originalAudioSilencer?.Apply();
                    CoreAudioInterop.Check(renderClient.GetCurrentPadding(out var padding), "无法读取输出延迟。");
                    var latencyMs = ((queue.AvailableFrames + padding) * 1000.0) / renderFormat.SampleRate;
                    RaiseLatency(latencyMs);
                }

                Thread.Sleep(3);
            }
        }
        catch (Exception ex)
        {
            RaiseStatus($"已停止：{ex.Message}");
        }
        finally
        {
            originalAudioSilencer?.Dispose();
            TryStop(captureClient);
            TryStop(renderClient);
            CoreAudioInterop.SafeRelease(capture);
            CoreAudioInterop.SafeRelease(render);
            CoreAudioInterop.SafeRelease(captureClient);
            CoreAudioInterop.SafeRelease(renderClient);
            if (captureFormatPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(captureFormatPointer);
            }

            if (renderFormatPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(renderFormatPointer);
            }

            if (comInitialized)
            {
                CoreAudioInterop.CoUninitialize();
            }

            IsRunning = false;
            RaiseLatency(0);
            if (_stopRequested)
            {
                RaiseStatus("已停止。");
            }

            Stopped?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DrainCapturePackets(
        IAudioCaptureClient capture,
        AudioFormat captureFormat,
        AudioFormat renderFormat,
        Spatial8DProcessor processor,
        StereoRingBuffer queue,
        ref float[] captureBuffer,
        ref float[] resampleBuffer)
    {
        CoreAudioInterop.Check(capture.GetNextPacketSize(out var packetFrames), "无法读取捕获包大小。");
        while (packetFrames > 0)
        {
            IntPtr data = IntPtr.Zero;
            uint frames = 0;
            var bufferAcquired = false;
            try
            {
                CoreAudioInterop.Check(
                    capture.GetBuffer(out data, out frames, out var flags, out _, out _),
                    "无法读取捕获缓冲区。");
                bufferAcquired = true;
                EnsureCapacity(ref captureBuffer, checked((int)frames) * 2);
                AudioBufferConverter.ReadToStereo(data, frames, captureFormat, flags.HasFlag(AudioClientBufferFlags.Silent), captureBuffer);
                processor.Process(captureBuffer, checked((int)frames), SnapshotSettings());

                var outputFrames = AudioBlockResampler.Resample(
                    captureBuffer,
                    checked((int)frames),
                    captureFormat.SampleRate,
                    renderFormat.SampleRate,
                    ref resampleBuffer);
                queue.Write(resampleBuffer, outputFrames);
            }
            finally
            {
                if (bufferAcquired)
                {
                    CoreAudioInterop.Check(capture.ReleaseBuffer(frames), "无法释放捕获缓冲区。");
                }
            }

            CoreAudioInterop.Check(capture.GetNextPacketSize(out packetFrames), "无法读取捕获包大小。");
        }
    }

    private static void FillRenderBuffer(
        IAudioClient renderClient,
        IAudioRenderClient render,
        AudioFormat renderFormat,
        uint renderBufferFrames,
        StereoRingBuffer queue,
        ref float[] outputBuffer)
    {
        CoreAudioInterop.Check(renderClient.GetCurrentPadding(out var padding), "无法读取输出延迟。");
        var availableFrames = renderBufferFrames - padding;
        if (availableFrames == 0)
        {
            return;
        }

        EnsureCapacity(ref outputBuffer, checked((int)availableFrames) * 2);
        var validFrames = queue.Read(outputBuffer, checked((int)availableFrames));
        if (validFrames < availableFrames)
        {
            Array.Clear(outputBuffer, validFrames * 2, (checked((int)availableFrames) - validFrames) * 2);
        }

        CoreAudioInterop.Check(render.GetBuffer(availableFrames, out var data), "无法获取输出缓冲区。");
        AudioBufferConverter.WriteFromStereo(data, availableFrames, renderFormat, outputBuffer, checked((int)availableFrames));
        CoreAudioInterop.Check(render.ReleaseBuffer(availableFrames, AudioClientBufferFlags.None), "无法释放输出缓冲区。");
    }

    private static void PrimeRenderBuffer(IAudioRenderClient render, uint renderBufferFrames)
    {
        CoreAudioInterop.Check(render.GetBuffer(renderBufferFrames, out _), "无法初始化静音输出缓冲区。");
        CoreAudioInterop.Check(render.ReleaseBuffer(renderBufferFrames, AudioClientBufferFlags.Silent), "无法释放静音输出缓冲区。");
    }

    private static void TryStop(IAudioClient? client)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            client.Stop();
        }
        catch
        {
            // Best-effort cleanup on the audio thread.
        }
    }

    private static void EnsureCapacity(ref float[] buffer, int samples)
    {
        if (buffer.Length < samples)
        {
            buffer = new float[samples];
        }
    }

    private void RaiseStatus(string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private void RaiseLatency(double latencyMs)
    {
        LatencyChanged?.Invoke(this, latencyMs);
    }
}
