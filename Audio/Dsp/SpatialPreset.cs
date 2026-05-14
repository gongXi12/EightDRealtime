namespace EightDRealtime.Audio.Dsp;

public sealed record SpatialPreset(
    string Id,
    string DisplayName,
    float RotationHz,
    float Depth,
    float CircleStrength,
    float HeightDepth,
    float HeightRate,
    float HrtfStrength,
    float ReverbWet,
    float LimiterThreshold)
{
    public static readonly SpatialPreset[] All =
    {
        new("crown_8d", "标准 8D 环绕", 0.105f, 0.94f, 0.94f, 1.00f, 1.00f, 0.92f, 0.16f, 0.90f),
        new("fast_5d", "快速环绕", 0.185f, 0.88f, 0.96f, 0.10f, 1.00f, 0.72f, 0.10f, 0.92f),
        new("extreme_8d", "强烈 8D 环绕", 0.145f, 1.00f, 1.00f, 1.00f, 1.18f, 1.00f, 0.24f, 0.86f),
        new("music_8d", "音乐均衡", 0.115f, 0.86f, 0.86f, 0.74f, 1.00f, 0.82f, 0.14f, 0.91f),
        new("voice_safe", "人声清晰", 0.038f, 0.34f, 0.42f, 0.16f, 0.75f, 0.28f, 0.04f, 0.96f)
    };

    public static SpatialPreset Default => All[0];
}

public sealed record SpatialSettings(
    bool Enabled,
    float InputGain,
    float OutputGain,
    float RotationHz,
    float Depth,
    float CircleStrength,
    float HeightDepth,
    float HeightRate,
    float HrtfStrength,
    float ReverbWet,
    float LimiterThreshold)
{
    public static SpatialSettings FromPreset(SpatialPreset preset)
    {
        return new SpatialSettings(
            Enabled: true,
            InputGain: 0.84f,
            OutputGain: 0.80f,
            RotationHz: preset.RotationHz,
            Depth: preset.Depth,
            CircleStrength: preset.CircleStrength,
            HeightDepth: preset.HeightDepth,
            HeightRate: preset.HeightRate,
            HrtfStrength: preset.HrtfStrength,
            ReverbWet: preset.ReverbWet,
            LimiterThreshold: preset.LimiterThreshold);
    }
}
