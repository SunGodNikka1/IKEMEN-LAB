using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IKEMENLab.App.ViewModels;
using IKEMENLab.Core.Collections;
using IKEMENLab.Core.Services;

namespace IKEMENLab.App.Services;

/// <summary>
/// Developer/QA automation (--qa-script=file). Drives the running app through the same view-model
/// commands the UI binds to, so fixtures can be exercised end to end without synthetic mouse input
/// (which would steal focus from other windows, e.g. a running game). One command per line:
/// wait-ready, refresh, page NAME, sort characters|stages default|latest|oldest,
/// ui-sort default|latest|oldest [POPUP-PNG] (the visible Sort drop-down), toggle character|stage ID,
/// vsync on|off, volume N, collection-create NAME|id,id, activate-collection NAME,
/// install [replace] PATH, hold PATH, release, dump PATH, snapshot PATH, sleep MS.
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
            case "ui-sort":
                // ui-sort default|latest|oldest [POPUP-PNG]: drives the visible page's real Sort drop-down.
                var args = rest.Split(' ', 2);
                await UiSortAsync(args[0], args.Length > 1 ? args[1] : null);
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
            case "vsync":
                main.Dashboard.VSync = rest.Equals("on", StringComparison.OrdinalIgnoreCase);
                await Task.Delay(1500);
                _log.Add($"  vsync: {main.Dashboard.VSync} note='{main.Dashboard.QuickSettingsNote}'");
                break;
            case "volume":
                main.Dashboard.MasterVolume = double.Parse(rest, CultureInfo.InvariantCulture);
                main.Dashboard.CommitMasterVolumeCommand.Execute(null);
                await Task.Delay(1500);
                _log.Add($"  volume: {main.Dashboard.MasterVolume} note='{main.Dashboard.QuickSettingsNote}'");
                break;
            case "collection-create":
                await CreateCollectionAsync(rest);
                break;
            case "activate-collection":
                await ActivateCollectionAsync(rest);
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

    private async Task UiSortAsync(string mode, string? popupSnapshot)
    {
        window.UpdateLayout();
        var combo = Descendants(window).OfType<ComboBox>()
                        .FirstOrDefault(c => c.IsVisible && System.Windows.Automation.AutomationProperties.GetName(c) == "Sort")
                    ?? throw new InvalidOperationException("No visible Sort drop-down");
        combo.IsDropDownOpen = true;
        await Task.Delay(400);
        var item = combo.Items.Cast<object>().Select(i => (ComboBoxItem?)combo.ItemContainerGenerator.ContainerFromItem(i))
                       .FirstOrDefault(c => c?.Content is BrowserSortOption o && o.Mode.ToString().StartsWith(mode, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("No generated item for " + mode);
        if (popupSnapshot is not null && combo.Template.FindName("PART_Popup", combo) is Popup { Child: FrameworkElement popup })
        {
            Render(popup, popupSnapshot);
        }

        // Same effect as clicking the item: select it and close the list.
        item.IsSelected = true;
        combo.IsDropDownOpen = false;
        await Task.Delay(200);
        _log.Add($"  ui-sort: shown='{(combo.SelectedItem as BrowserSortOption)?.Label}' characters={main.Characters.SortMode} stages={main.Stages.SortMode}");
    }

    /// <summary>collection-create NAME|id1,id2 — a manual collection in the app's collection store.</summary>
    private async Task CreateCollectionAsync(string spec)
    {
        var parts = spec.Split('|', 2);
        var ids = parts[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var snapshot = main.Snapshot ?? throw new InvalidOperationException("No snapshot");
        var root = snapshot.Installation.RootPath;
        var store = new CollectionStore();
        var collection = store.Create(root, parts[0]);
        store.Add(root, collection.Id, snapshot.Characters.Where(c => ids.Contains(c.Id, StringComparer.OrdinalIgnoreCase)));
        await main.RefreshAsync(null);
        await WaitReadyAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>activate-collection NAME — Preview then Confirm, exactly as the Collections page buttons do.</summary>
    private async Task ActivateCollectionAsync(string name)
    {
        var collections = main.Collections;
        collections.Selected = collections.Collections.FirstOrDefault(c => c.Name == name)
                               ?? throw new InvalidOperationException("No collection " + name);
        collections.BeginActivateCommand.Execute(null);
        if (!collections.CanConfirmActivate)
        {
            _log.Add($"  activate: preview refused '{collections.Error}'");
            return;
        }

        collections.ConfirmActivateCommand.Execute(null);
        var until = DateTime.UtcNow.AddSeconds(60);
        do await Task.Delay(100);
        while (collections.IsActivating && DateTime.UtcNow < until);
        await WaitReadyAsync(TimeSpan.FromSeconds(60));
        _log.Add($"  activate: status='{collections.StatusText}' error='{collections.Error}'");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
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
                label = r.DateAddedText,
                column = r.DateColumnText
            }),
            stageSort = main.Stages.SortMode.ToString(),
            stages = main.Stages.Stages.Select(r => new
            {
                id = r.Entry.Id,
                name = r.Name,
                status = r.Entry.Status.ToString(),
                dateAddedUtc = r.Entry.DateAddedUtc,
                dateAddedSource = r.Entry.DateAddedSource?.ToString(),
                label = r.DateAddedText,
                column = r.DateColumnText
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
        Render((FrameworkElement)window.Content, path);
    }

    private static void Render(FrameworkElement root, string path)
    {
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
