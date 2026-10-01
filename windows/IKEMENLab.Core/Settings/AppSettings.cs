namespace IKEMENLab.Core.Settings;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string? IkemenRoot { get; set; }

    // Combo playback (X-Ray). All optional; unset values are resolved from the install by PlaybackPreflight.
    /// <summary>The X-Ray sandbox engine build that carries the virtual-input hook. The production engine cannot receive scripted input.</summary>
    public string? XRayEnginePath { get; set; }
    /// <summary>Runtime DLLs a self-built engine needs next to it.</summary>
    public string? XRayEngineDlls { get; set; }
    /// <summary>Character folder (under chars/) used as the dummy P2.</summary>
    public string? XRayDummy { get; set; }
    /// <summary>Stage DEF (e.g. stages/kfm.def) the playback happens on.</summary>
    public string? XRayStage { get; set; }
    /// <summary>How close P1 walks to the dummy before a route starts (engine distance units, whole number 1-1000). Blank = default 60. A threshold, not an attack range.</summary>
    public string? XRayApproachDistance { get; set; }
}
