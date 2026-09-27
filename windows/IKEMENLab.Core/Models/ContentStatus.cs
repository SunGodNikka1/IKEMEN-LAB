namespace IKEMENLab.Core.Models;

/// <summary>
/// Registration state derived by comparing the filesystem with the active select.def.
/// Read-only: computing it never touches select.def.
/// </summary>
public enum ContentStatus
{
    /// <summary>Folder/DEF exists but no select.def line references it.</summary>
    Unregistered,

    /// <summary>Referenced by an uncommented select.def line.</summary>
    Active,

    /// <summary>Only referenced by a commented-out (";") select.def line.</summary>
    Disabled
}
