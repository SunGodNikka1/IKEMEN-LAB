using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IKEMENLab.App.ViewModels;
using IKEMENLab.Core.Services;

namespace IKEMENLab.App.Services;

/// <summary>
/// Developer/QA automation (--qa-script=file). Drives the running app through the same view-model
/// commands the UI binds to, so fixtures can be exercised end to end without synthetic mouse input
/// (which would steal focus from other windows, e.g. a running game). One command per line:
/// wait-ready, refresh, page NAME, sort characters|stages default|latest|oldest,
/// toggle character|stage ID, install [replace] PATH, hold PATH, release, dump PATH, snapshot PATH, sleep MS.
/// Lines starting with '#' are ignored.
/// </summary>
public sealed class QaScriptRunner(MainViewModel main, Window window)
{
    private readonly List<string> _log = [];
    private FileStream? _held;

    public async Task RunAsync(string scriptPath)
    {
        foreach (var raw in await File.ReadAllLinesAsync(scriptPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            _log.Add("> " + line);
            try
            {
                var rest = line.Length > parts[0].Length ? line[parts[0].Length..].Trim() : "";
                await ExecuteAsync(parts[0].ToLowerInvariant(), parts, rest);
            }
            catch (Exception ex)
            {
                _log.Add("! " + ex.Message);
            }
        }

        _held?.Dispose();
    }

    private async Task ExecuteAsync(string verb, string[] p, string rest)
    {
        switch (verb)
        {
            case "wait-ready":
                await WaitReadyAsync(TimeSpan.FromSeconds(p.Length > 1 ? double.Parse(p[1]) : 60));
                break;
            case "refresh":
                await main.RefreshAsync(null);
                break;
            case "page":
                main.SelectedNav = Enum.Parse<NavPage>(p[1], ignoreCase: true);
                break;
            case "sort":
                var mode = p[2].ToLowerInvariant() switch
                {
                    "latest" => BrowserSortMode.LatestAdded,
                    "oldest" => BrowserSortMode.OldestAdded,
                    _ => BrowserSortMode.Default
                };
                if (p[1].StartsWith("char", StringComparison.OrdinalIgnoreCase)) main.Characters.SortMode = mode;
                else main.Stages.SortMode = mode;
                break;
            case "toggle":
                await ToggleAsync(p[1], p[2]);
                await WaitReadyAsync(TimeSpan.FromSeconds(60));
                break;
            case "install":
                var replace = rest.StartsWith("replace ", StringComparison.OrdinalIgnoreCase);
                await InstallAsync(replace ? rest["replace ".Length..].Trim() : rest, replace);
                await main.RefreshAsync(null);
                break;
            case "hold":
                // Simulates another program keeping the file open (readable, not replaceable).
                _held?.Dispose();
                _held = new FileStream(rest, FileMode.Open, FileAccess.Read, FileShare.Read);
                break;
            case "release":
                _held?.Dispose();
                _held = null;
                break;
            case "sleep":
                await Task.Delay(int.Parse(p[1]));
                break;
            case "dump":
                Dump(rest);
                break;
            case "snapshot":
                Snapshot(rest);
                break;
            default:
                throw new InvalidOperationException("Unknown QA command: " + verb);
        }
    }

    private async Task WaitReadyAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (main.Snapshot is not null && !main.IsBusy) return;
            await Task.Delay(100);
        }

        throw new TimeoutException("App did not finish indexing.");
    }

    /// <summary>Installs a folder/archive through the Dashboard's installer (preview auto-confirmed).</summary>
    private async Task InstallAsync(string source, bool replace)
    {
        var root = main.Snapshot?.Installation.RootPath ?? throw new InvalidOperationException("No root");
        var result = await Task.Run(() =>
        {
            var inspect = main.Installer.Inspect([source], root);
            foreach (var item in inspect.Items)
            {
                item.Decision = item.DestinationExists
                    ? (replace ? IKEMENLab.Core.Install.InstallItemDecision.Replace : IKEMENLab.Core.Install.InstallItemDecision.Skip)
                    : IKEMENLab.Core.Install.InstallItemDecision.InstallNew;
            }

            var batch = main.Installer.Execute(inspect.Items, root);
            main.Installer.CleanupStaging(inspect.StagingDirectories);
            return batch;
        });
        foreach (var item in result.Items) _log.Add($"  install {item.Package.DisplayName}: {item.Outcome} {item.Error}");
    }

    private async Task ToggleAsync(string kind, string id)
    {
        if (kind.StartsWith("char", StringComparison.OrdinalIgnoreCase))
        {
            var row = main.Characters.Characters.FirstOrDefault(r => r.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("No character row " + id);
            await main.Characters.ToggleStatusAsync(row);
        }
        else
        {
            var row = main.Stages.Stages.FirstOrDefault(r => r.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("No stage row " + id);
            await main.Stages.ToggleStatusAsync(row);
        }
    }

    private void Dump(string path)
    {
        var state = new
        {
            root = main.Snapshot?.Installation.RootPath,
            characterSort = main.Characters.SortMode.ToString(),
            characters = main.Characters.Characters.Select(r => new
            {
                id = r.Entry.Id,
                name = r.DisplayName,
                status = r.Status.ToString(),
                switchOn = r.IsActive,
                dateAddedUtc = r.Entry.DateAddedUtc,
                dateAddedSource = r.Entry.DateAddedSource?.ToString(),
                label = r.DateAddedText
            }),
            stageSort = main.Stages.SortMode.ToString(),
            stages = main.Stages.Stages.Select(r => new
            {
                id = r.Entry.Id,
                name = r.Name,
                status = r.Entry.Status.ToString(),
                dateAddedUtc = r.Entry.DateAddedUtc,
                dateAddedSource = r.Entry.DateAddedSource?.ToString(),
                label = r.DateAddedText
            }),
            recentlyInstalled = main.Dashboard.RecentItems.Select(r => new
            {
                id = r.Item.Id,
                name = r.Name,
                date = r.DateText,
                dateAddedUtc = r.Item.DateAddedUtc,
                source = r.Item.DateAddedSource?.ToString()
            }),
            warnings = UserDialogs.RecordedWarnings,
            log = _log
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Snapshot(string path)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(Math.Max(root.ActualWidth, 1));
        var height = (int)Math.Ceiling(Math.Max(root.ActualHeight, 1));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
