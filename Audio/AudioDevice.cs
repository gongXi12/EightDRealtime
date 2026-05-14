namespace EightDRealtime.Audio;

public sealed record AudioDevice(string Id, string Name, bool IsDefault)
{
    public override string ToString()
    {
        return IsDefault ? $"{Name}（默认）" : Name;
    }
}
