using System.Runtime.InteropServices;

namespace EightDRealtime.Audio;

internal static class CoreAudioDeviceEnumerator
{
    private const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";

    public static IReadOnlyList<AudioDevice> GetActiveRenderDevices()
    {
        var results = new List<AudioDevice>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        IMMDevice? defaultDevice = null;
        string? defaultId = null;

        try
        {
            enumerator = CreateDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out defaultDevice) >= 0)
            {
                CoreAudioInterop.Check(defaultDevice.GetId(out defaultId), "无法读取默认音频设备 ID。");
            }

            CoreAudioInterop.Check(
                enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out collection),
                "无法枚举播放设备。");
            CoreAudioInterop.Check(collection.GetCount(out var count), "无法读取播放设备数量。");

            for (var i = 0u; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    CoreAudioInterop.Check(collection.Item(i, out device), "无法读取播放设备。");
                    CoreAudioInterop.Check(device.GetId(out var id), "无法读取播放设备 ID。");
                    var name = TryGetFriendlyName(device) ?? id;
                    results.Add(new AudioDevice(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                finally
                {
                    CoreAudioInterop.SafeRelease(device);
                }
            }
        }
        finally
        {
            CoreAudioInterop.SafeRelease(defaultDevice);
            CoreAudioInterop.SafeRelease(collection);
            CoreAudioInterop.SafeRelease(enumerator);
        }

        return results
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static IAudioClient ActivateAudioClient(string deviceId)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = CreateDeviceEnumerator();
            CoreAudioInterop.Check(enumerator.GetDevice(deviceId, out device), "无法打开音频设备。");
            var iid = typeof(IAudioClient).GUID;
            CoreAudioInterop.Check(
                device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out var audioClientObject),
                "无法启动 WASAPI 音频客户端。");
            return (IAudioClient)audioClientObject;
        }
        finally
        {
            CoreAudioInterop.SafeRelease(device);
            CoreAudioInterop.SafeRelease(enumerator);
        }
    }

    public static IAudioSessionManager2 ActivateAudioSessionManager(string deviceId)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = CreateDeviceEnumerator();
            CoreAudioInterop.Check(enumerator.GetDevice(deviceId, out device), "无法打开音频设备。");
            var iid = typeof(IAudioSessionManager2).GUID;
            CoreAudioInterop.Check(
                device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out var sessionManagerObject),
                "无法启动音频会话管理器。");
            return (IAudioSessionManager2)sessionManagerObject;
        }
        finally
        {
            CoreAudioInterop.SafeRelease(device);
            CoreAudioInterop.SafeRelease(enumerator);
        }
    }

    public static IAudioClient ActivateProcessLoopbackClientExcludingCurrentProcess(
        IntPtr waveFormatPointer,
        long bufferDuration)
    {
        if (Environment.OSVersion.Version.Build < 20348)
        {
            throw new PlatformNotSupportedException("同设备捕获需要 Windows 10 Build 20348 或更新版本。");
        }

        using var activationParams = PropVariantBlob.FromStructure(new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = (uint)Environment.ProcessId,
                ProcessLoopbackMode = ProcessLoopbackMode.ExcludeTargetProcessTree
            }
        });

        var handler = new ActivateAudioInterfaceCompletionHandler(
            waveFormatPointer,
            bufferDuration,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality);
        var iid = typeof(IAudioClient).GUID;

        CoreAudioInterop.Check(
            CoreAudioInterop.ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref iid,
                activationParams.Pointer,
                handler,
                out var operation),
            "无法启动同设备捕获。");

        try
        {
            return handler.WaitForResult(TimeSpan.FromSeconds(5));
        }
        finally
        {
            CoreAudioInterop.SafeRelease(operation);
            GC.KeepAlive(handler);
        }
    }

    public static IntPtr AllocatePcm16StereoWaveFormat(int sampleRate)
    {
        var waveFormat = new WaveFormatEx
        {
            FormatTag = WaveFormatTags.Pcm,
            Channels = 2,
            SamplesPerSecond = (uint)sampleRate,
            BitsPerSample = 16,
            BlockAlign = 4,
            AverageBytesPerSecond = (uint)sampleRate * 4,
            ExtraSize = 0
        };
        var pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatEx>());
        Marshal.StructureToPtr(waveFormat, pointer, false);
        return pointer;
    }

    private static IMMDeviceEnumerator CreateDeviceEnumerator()
    {
        var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))
            ?? throw new InvalidOperationException("无法创建系统音频设备枚举器。");
        return (IMMDeviceEnumerator)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("无法创建系统音频设备枚举器。"));
    }

    private static string? TryGetFriendlyName(IMMDevice device)
    {
        try
        {
            return GetFriendlyName(device);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            CoreAudioInterop.Check(device.OpenPropertyStore(StorageAccessMode.Read, out store), "无法读取音频设备属性。");
            var key = PropertyKeys.DeviceFriendlyName;
            CoreAudioInterop.Check(store.GetValue(ref key, out var value), "无法读取音频设备名称。");
            try
            {
                return value.GetString();
            }
            finally
            {
                value.Dispose();
            }
        }
        finally
        {
            CoreAudioInterop.SafeRelease(store);
        }
    }
}

internal static class CoreAudioInterop
{
    public const long ReftimesPerSecond = 10_000_000;

    public static void Check(int hr, string message)
    {
        if (hr < 0)
        {
            throw new COMException(message, hr);
        }
    }

    public static void SafeRelease(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr pvReserved, CoInit dwCoInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}

internal sealed record AudioFormat(
    ushort Channels,
    int SampleRate,
    ushort BitsPerSample,
    ushort BlockAlign,
    AudioSampleKind SampleKind)
{
    public int BytesPerFrame => BlockAlign;

    public static AudioFormat FromPointer(IntPtr waveFormatPointer)
    {
        var ex = Marshal.PtrToStructure<WaveFormatEx>(waveFormatPointer);
        var kind = AudioSampleKind.Unknown;
        var bits = ex.BitsPerSample;

        if (ex.FormatTag == WaveFormatTags.Pcm)
        {
            kind = AudioSampleKind.Pcm;
        }
        else if (ex.FormatTag == WaveFormatTags.IeeeFloat)
        {
            kind = AudioSampleKind.Float;
        }
        else if (ex.FormatTag == WaveFormatTags.Extensible && ex.ExtraSize >= 22)
        {
            var extensible = Marshal.PtrToStructure<WaveFormatExtensible>(waveFormatPointer);
            bits = extensible.ValidBitsPerSample == 0 ? ex.BitsPerSample : extensible.ValidBitsPerSample;
            if (extensible.SubFormat == WaveFormatTags.PcmSubFormat)
            {
                kind = AudioSampleKind.Pcm;
            }
            else if (extensible.SubFormat == WaveFormatTags.IeeeFloatSubFormat)
            {
                kind = AudioSampleKind.Float;
            }
        }

        if (kind == AudioSampleKind.Unknown)
        {
            throw new NotSupportedException($"Unsupported audio mix format: tag={ex.FormatTag}, bits={bits}.");
        }

        return new AudioFormat(ex.Channels, (int)ex.SamplesPerSecond, bits, ex.BlockAlign, kind);
    }

    public static AudioFormat Pcm16Stereo(int sampleRate)
    {
        return new AudioFormat(2, sampleRate, 16, 4, AudioSampleKind.Pcm);
    }
}

internal enum AudioSampleKind
{
    Unknown,
    Pcm,
    Float
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(
        ref Guid iid,
        ClsCtx clsCtx,
        IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

    [PreserveSig]
    int OpenPropertyStore(StorageAccessMode accessMode, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out DeviceState state);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint propertyCount);

    [PreserveSig]
    int GetAt(uint propertyIndex, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(
        AudioClientShareMode shareMode,
        AudioClientStreamFlags streamFlags,
        long bufferDuration,
        long periodicity,
        IntPtr waveFormat,
        ref Guid audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferSize);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint currentPadding);

    [PreserveSig]
    int IsFormatSupported(
        AudioClientShareMode shareMode,
        IntPtr waveFormat,
        out IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid serviceGuid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(
        out IntPtr data,
        out uint frameCount,
        out AudioClientBufferFlags flags,
        out ulong devicePosition,
        out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint frameCount);

    [PreserveSig]
    int GetNextPacketSize(out uint frameCount);
}

[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig]
    int GetBuffer(uint frameCount, out IntPtr data);

    [PreserveSig]
    int ReleaseBuffer(uint frameCount, AudioClientBufferFlags flags);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(ref Guid audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);

    [PreserveSig]
    int GetSimpleAudioVolume(ref Guid audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);

    [PreserveSig]
    int RegisterSessionNotification(IntPtr sessionNotification);

    [PreserveSig]
    int UnregisterSessionNotification(IntPtr sessionNotification);

    [PreserveSig]
    int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);

    [PreserveSig]
    int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int sessionCount);

    [PreserveSig]
    int GetSession(int sessionIndex, out IAudioSessionControl sessionControl);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig]
    int GetState(out AudioSessionState state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingId);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr newNotifications);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr newNotifications);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out AudioSessionState state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingId);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr newNotifications);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr newNotifications);

    [PreserveSig]
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionId);

    [PreserveSig]
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceId);

    [PreserveSig]
    int GetProcessId(out uint processId);

    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolume(out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(
        [MarshalAs(UnmanagedType.Error)] out int activateResult,
        [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport]
[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

[ClassInterface(ClassInterfaceType.None)]
internal sealed class ActivateAudioInterfaceCompletionHandler :
    IActivateAudioInterfaceCompletionHandler,
    IAgileObject
{
    private readonly ManualResetEventSlim _completed = new(false);
    private readonly IntPtr _waveFormatPointer;
    private readonly long _bufferDuration;
    private readonly AudioClientStreamFlags _streamFlags;
    private IAudioClient? _audioClient;
    private Exception? _exception;

    public ActivateAudioInterfaceCompletionHandler(
        IntPtr waveFormatPointer,
        long bufferDuration,
        AudioClientStreamFlags streamFlags)
    {
        _waveFormatPointer = waveFormatPointer;
        _bufferDuration = bufferDuration;
        _streamFlags = streamFlags;
    }

    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        try
        {
            CoreAudioInterop.Check(
                operation.GetActivateResult(out var activateResult, out var activatedInterface),
                "无法读取同设备捕获启动结果。");
            CoreAudioInterop.Check(activateResult, "同设备捕获启动失败。");

            var audioClient = (IAudioClient)activatedInterface;
            var session = Guid.Empty;
            CoreAudioInterop.Check(
                audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    _streamFlags,
                    _bufferDuration,
                    0,
                    _waveFormatPointer,
                    ref session),
                "无法初始化同设备捕获。");
            _audioClient = audioClient;
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
        finally
        {
            _completed.Set();
        }

        return 0;
    }

    public IAudioClient WaitForResult(TimeSpan timeout)
    {
        if (!_completed.Wait(timeout))
        {
            throw new TimeoutException("启动同设备捕获超时。");
        }

        if (_exception is not null)
        {
            throw _exception;
        }

        return _audioClient ?? throw new InvalidOperationException("同设备捕获没有返回音频客户端。");
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

internal static class PropertyKeys
{
    public static readonly PropertyKey DeviceFriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant : IDisposable
{
    private ushort _variantType;
    private ushort _reserved1;
    private ushort _reserved2;
    private ushort _reserved3;
    private IntPtr _value;
    private IntPtr _value2;

    public string? GetString()
    {
        const ushort vtLpwStr = 31;
        return _variantType == vtLpwStr ? Marshal.PtrToStringUni(_value) : null;
    }

    public void Dispose()
    {
        PropVariantClear(ref this);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}

internal sealed class PropVariantBlob : IDisposable
{
    public IntPtr Pointer { get; }

    private readonly IntPtr _dataPointer;

    private PropVariantBlob(IntPtr pointer, IntPtr dataPointer)
    {
        Pointer = pointer;
        _dataPointer = dataPointer;
    }

    public static PropVariantBlob FromStructure<T>(T value)
        where T : struct
    {
        var dataSize = Marshal.SizeOf<T>();
        var dataPointer = Marshal.AllocCoTaskMem(dataSize);
        Marshal.StructureToPtr(value, dataPointer, false);

        var variant = new PropVariantNative
        {
            VariantType = VariantTypes.Blob,
            Blob = new Blob
            {
                Size = dataSize,
                Data = dataPointer
            }
        };

        var variantPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<PropVariantNative>());
        Marshal.StructureToPtr(variant, variantPointer, false);
        return new PropVariantBlob(variantPointer, dataPointer);
    }

    public void Dispose()
    {
        if (Pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(Pointer);
        }

        if (_dataPointer != IntPtr.Zero)
        {
            Marshal.DestroyStructure<AudioClientActivationParams>(_dataPointer);
            Marshal.FreeCoTaskMem(_dataPointer);
        }
    }
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariantNative
{
    [FieldOffset(0)]
    public ushort VariantType;

    [FieldOffset(2)]
    private readonly ushort _reserved1;

    [FieldOffset(4)]
    private readonly ushort _reserved2;

    [FieldOffset(6)]
    private readonly ushort _reserved3;

    [FieldOffset(8)]
    public Blob Blob;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Blob
{
    public int Size;
    public IntPtr Data;
}

internal static class VariantTypes
{
    public const ushort Blob = 65;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public AudioClientActivationType ActivationType;
    public AudioClientProcessLoopbackParams ProcessLoopbackParams;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParams
{
    public uint TargetProcessId;
    public ProcessLoopbackMode ProcessLoopbackMode;
}

internal enum AudioClientActivationType
{
    Default,
    ProcessLoopback
}

internal enum ProcessLoopbackMode
{
    IncludeTargetProcessTree,
    ExcludeTargetProcessTree
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSecond;
    public uint AverageBytesPerSecond;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatExtensible
{
    public WaveFormatEx Format;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;
}

internal static class WaveFormatTags
{
    public const ushort Pcm = 0x0001;
    public const ushort IeeeFloat = 0x0003;
    public const ushort Extensible = 0xFFFE;
    public static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");
}

internal enum EDataFlow
{
    Render,
    Capture,
    All
}

internal enum ERole
{
    Console,
    Multimedia,
    Communications
}

[Flags]
internal enum DeviceState : uint
{
    Active = 0x00000001,
    Disabled = 0x00000002,
    NotPresent = 0x00000004,
    Unplugged = 0x00000008,
    All = 0x0000000F
}

[Flags]
internal enum ClsCtx : uint
{
    InprocServer = 0x1,
    InprocHandler = 0x2,
    LocalServer = 0x4,
    RemoteServer = 0x10,
    All = InprocServer | InprocHandler | LocalServer | RemoteServer
}

internal enum StorageAccessMode
{
    Read = 0
}

internal enum AudioClientShareMode
{
    Shared = 0,
    Exclusive = 1
}

internal enum AudioSessionState
{
    Inactive = 0,
    Active = 1,
    Expired = 2
}

[Flags]
internal enum AudioClientStreamFlags : uint
{
    None = 0,
    Loopback = 0x00020000,
    EventCallback = 0x00040000,
    NoPersist = 0x00080000,
    AutoConvertPcm = 0x80000000,
    SrcDefaultQuality = 0x08000000
}

[Flags]
internal enum AudioClientBufferFlags : uint
{
    None = 0,
    DataDiscontinuity = 0x1,
    Silent = 0x2,
    TimestampError = 0x4
}

internal enum CoInit : uint
{
    MultiThreaded = 0x0,
    ApartmentThreaded = 0x2,
    DisableOle1Dde = 0x4,
    SpeedOverMemory = 0x8
}
