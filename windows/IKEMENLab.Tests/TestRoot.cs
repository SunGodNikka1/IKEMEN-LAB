namespace IKEMENLab.Tests;

/// <summary>Disposable temporary IKEMEN-like tree for fixture tests.</summary>
internal sealed class TestRoot : IDisposable
{
    public TestRoot(string prefix = "ikemenlab")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(Path, "chars"));
        Directory.CreateDirectory(System.IO.Path.Combine(Path, "stages"));
        Directory.CreateDirectory(System.IO.Path.Combine(Path, "data"));
    }

    public string Path { get; }

    public string Write(string relative, string content)
    {
        var full = Full(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string WriteBytes(string relative, byte[] content)
    {
        var full = Full(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string Full(string relative)
        => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public static string CharDef(string name, string author = "Test") =>
        $"[Info]\nname = {name}\nauthor = {author}\n\n[Files]\nsprite = x.sff\nanim = x.air\ncmd = x.cmd\ncns = x.cns\n";

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { }
    }
}
