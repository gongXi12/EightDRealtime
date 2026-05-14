namespace EightDRealtime.Audio.Dsp;

public sealed record SpatialSettings(
    bool Enabled,
    float InputGain,
    float OutputGain,
    float RotationHz,
    float Depth,
    float CircleStrength,
    float HrtfStrength,
    float ReverbWet,
    float LimiterThreshold);
