namespace IKEMENLab.Core.Settings;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string? IkemenRoot { get; set; }
}
