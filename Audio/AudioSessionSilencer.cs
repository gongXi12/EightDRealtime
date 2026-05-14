using System.Runtime.InteropServices;

namespace EightDRealtime.Audio;

internal sealed class AudioSessionSilencer : IDisposable
{
    private readonly string _deviceId;
    private readonly uint _currentProcessId = (uint)Environment.ProcessId;
    private readonly Dictionary<string, bool> _originalMuteStates = new(StringComparer.Ordinal);
    private Guid _eventContext = Guid.NewGuid();
    private bool _disposed;

    public AudioSessionSilencer(string deviceId)
    {
        _deviceId = deviceId;
    }

    public int Apply()
    {
        if (_disposed)
        {
            return 0;
        }

        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? enumerator = null;
        var mutedCount = 0;

        try
        {
            manager = CoreAudioDeviceEnumerator.ActivateAudioSessionManager(_deviceId);
            CoreAudioInterop.Check(manager.GetSessionEnumerator(out enumerator), "无法读取音频会话。");
            CoreAudioInterop.Check(enumerator.GetCount(out var count), "无法读取音频会话数量。");

            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    CoreAudioInterop.Check(enumerator.GetSession(index, out control), "无法读取音频会话。");
                    if (control is not IAudioSessionControl2 control2 || control is not ISimpleAudioVolume volume)
                    {
                        continue;
                    }

                    if (IsCurrentProcessSession(control2))
                    {
                        continue;
                    }

                    var key = GetSessionKey(control2, index);
                    CoreAudioInterop.Check(volume.GetMute(out var wasMuted), "无法读取音频会话静音状态。");
                    if (!_originalMuteStates.ContainsKey(key))
                    {
                        _originalMuteStates[key] = wasMuted;
                    }

                    if (!wasMuted)
                    {
                        CoreAudioInterop.Check(volume.SetMute(true, ref _eventContext), "无法静音原始音频会话。");
                        mutedCount++;
                    }
                }
                finally
                {
                    CoreAudioInterop.SafeRelease(control);
                }
            }
        }
        finally
        {
            CoreAudioInterop.SafeRelease(enumerator);
            CoreAudioInterop.SafeRelease(manager);
        }

        return mutedCount;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Restore();
    }

    private void Restore()
    {
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? enumerator = null;

        try
        {
            manager = CoreAudioDeviceEnumerator.ActivateAudioSessionManager(_deviceId);
            CoreAudioInterop.Check(manager.GetSessionEnumerator(out enumerator), "无法读取音频会话。");
            CoreAudioInterop.Check(enumerator.GetCount(out var count), "无法读取音频会话数量。");

            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    CoreAudioInterop.Check(enumerator.GetSession(index, out control), "无法读取音频会话。");
                    if (control is not IAudioSessionControl2 control2 || control is not ISimpleAudioVolume volume)
                    {
                        continue;
                    }

                    var key = GetSessionKey(control2, index);
                    if (_originalMuteStates.TryGetValue(key, out var originalMute))
                    {
                        CoreAudioInterop.Check(volume.SetMute(originalMute, ref _eventContext), "无法恢复原始音频会话。");
                    }
                }
                finally
                {
                    CoreAudioInterop.SafeRelease(control);
                }
            }
        }
        catch
        {
            // Stop should never fail just because a source app disappeared.
        }
        finally
        {
            CoreAudioInterop.SafeRelease(enumerator);
            CoreAudioInterop.SafeRelease(manager);
        }
    }

    private bool IsCurrentProcessSession(IAudioSessionControl2 control)
    {
        var hr = control.GetProcessId(out var processId);
        return hr >= 0 && processId == _currentProcessId;
    }

    private static string GetSessionKey(IAudioSessionControl2 control, int fallbackIndex)
    {
        return control.GetSessionInstanceIdentifier(out var id) >= 0 && !string.IsNullOrWhiteSpace(id)
            ? id
            : $"session-{fallbackIndex}";
    }
}
