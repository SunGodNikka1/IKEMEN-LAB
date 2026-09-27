namespace IKEMENLab.Core.Models;

public sealed class InstallationCheck
{
    public required string RootPath { get; init; }
    public bool CharsPresent { get; init; }
    public bool StagesPresent { get; init; }
    public bool DataPresent { get; init; }
    public bool ExePresent { get; init; }
    public string? ExePath { get; init; }

    public bool IsStructurallyValid => CharsPresent && StagesPresent && DataPresent;
    public bool CanBrowse => IsStructurallyValid;
    public bool CanLaunch => IsStructurallyValid && ExePresent;

    public IReadOnlyList<string> MissingItems
    {
        get
        {
            var missing = new List<string>();
            if (!CharsPresent) missing.Add("chars/");
            if (!StagesPresent) missing.Add("stages/");
            if (!DataPresent) missing.Add("data/");
            if (!ExePresent) missing.Add("Ikemen_GO.exe");
            return missing;
        }
    }
}
