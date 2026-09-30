using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IKEMENLab.App.ViewModels;
using IKEMENLab.App.Views;
using IKEMENLab.Core.Collections;
using IKEMENLab.Core.Services;

namespace IKEMENLab.App.Services;

/// <summary>
/// Developer/QA automation (--qa-script=file). Drives the running app through the same view-model
/// commands the UI binds to, so fixtures can be exercised end to end without synthetic mouse input
/// (which would steal focus from other windows, e.g. a running game). One command per line:
/// wait-ready, refresh, page NAME, sort characters|stages default|latest|oldest,
/// ui-sort default|latest|oldest [POPUP-PNG] (the visible Sort drop-down), toggle character|stage ID,
/// select character|stage ID, delete character ID [cancel] (Delete Character with the confirmation
/// answered yes, or no with "cancel"), refresh-button (the sidebar Refresh command),
/// vsync on|off, volume N, collection-create NAME|id,id, activate-collection NAME,
/// install [replace] PATH, hold PATH, release, dump PATH, snapshot PATH, sleep MS,
/// ui-click NAME (real Button Click by AutomationProperties.Name),
/// open-sprites / close-sprites / sprite-filter TEXT / sprite-zoom N / sprite-select GROUP,NUMBER /
/// sprite-palette INDEX / sprite-axis on|off / sprite-export PATH / sprite-minmax /
/// open-tuning / close-tuning / tune-set LABEL VALUE / tune-scale FACTOR / tune-save / tune-undo /
/// tune-external-set KEY VALUE (mutates the open CNS on disk for conflict tests).
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
            case "refresh-button":
                main.RefreshCommand.Execute(null);
                await Task.Delay(200);
                await WaitReadyAsync(TimeSpan.FromSeconds(60));
                _log.Add($"  refresh: page={main.SelectedNav} status='{main.StatusText}' label='{main.LastRefreshedText}'");
                break;
            case "select":
                Select(p[1], rest[p[1].Length..].Trim());
                break;
            case "delete":
                // delete character ID [cancel]; the id may contain spaces ("Muzan V3").
                var target = rest[p[1].Length..].Trim();
                var cancel = target.EndsWith(" cancel", StringComparison.OrdinalIgnoreCase);
                await DeleteAsync(cancel ? target[..^" cancel".Length].Trim() : target, cancel);
                await WaitReadyAsync(TimeSpan.FromSeconds(60));
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
            case "ui-click":
                await UiClickAsync(rest);
                break;
            case "open-sprites":
                await OpenSpritesAsync();
                break;
            case "close-sprites":
                CloseWindows<SpriteInspectorWindow>();
                break;
            case "sprite-filter":
                SpriteVm().Filter = rest;
                await Task.Delay(200);
                _log.Add($"  sprite-filter: shown={SpriteVm().Sprites.Count}");
                break;
            case "sprite-zoom":
                SpriteVm().Zoom = double.Parse(rest, CultureInfo.InvariantCulture);
                await Task.Delay(100);
                _log.Add($"  sprite-zoom: {SpriteVm().ZoomText}");
                break;
            case "sprite-select":
                SpriteSelect(rest);
                await Task.Delay(400);
                _log.Add($"  sprite-select: {SpriteVm().DetailText}");
                break;
            case "sprite-palette":
                SpritePalette(int.Parse(rest, CultureInfo.InvariantCulture));
                await Task.Delay(400);
                _log.Add($"  sprite-palette: {SpriteVm().SelectedPalette.Label}");
                break;
            case "sprite-axis":
                SpriteVm().ShowAxis = rest.Equals("on", StringComparison.OrdinalIgnoreCase);
                _log.Add($"  sprite-axis: {SpriteVm().ShowAxis}");
                break;
            case "sprite-export":
                await SpriteExportAsync(rest);
                break;
            case "sprite-minmax":
                await SpriteMinMaxAsync();
                break;
            case "open-tuning":
                await OpenTuningAsync();
                break;
            case "close-tuning":
                CloseWindows<CharacterTuningWindow>();
                break;
            case "tune-set":
                TuneSet(rest);
                await Task.Delay(150);
                break;
            case "tune-scale":
                TuneVm().ScaleCommand.Execute(rest);
                await Task.Delay(150);
                _log.Add($"  tune-scale: {rest} x={TuneRow("Width scale")?.Text} y={TuneRow("Height scale")?.Text}");
                break;
            case "tune-save":
                await UiClickAsync("Save tuning");
                await WaitTuningIdleAsync();
                _log.Add($"  tune-save: status='{TuneVm().Status}' canUndo={TuneVm().CanUndo} life={TuneRow("Life")?.Text}");
                break;
            case "tune-undo":
                await UiClickAsync("Undo last save");
                await WaitTuningIdleAsync();
                _log.Add($"  tune-undo: status='{TuneVm().Status}' life={TuneRow("Life")?.Text} x={TuneRow("Width scale")?.Text}");
                break;
            case "tune-external-set":
                TuneExternalSet(rest);
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

    private void Select(string kind, string id)
    {
        if (kind.StartsWith("char", StringComparison.OrdinalIgnoreCase))
        {
            main.Characters.Selected = main.Characters.Characters.FirstOrDefault(r => r.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                                       ?? throw new InvalidOperationException("No character row " + id);
        }
        else
        {
            main.Stages.Selected = main.Stages.Stages.FirstOrDefault(r => r.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                                   ?? throw new InvalidOperationException("No stage row " + id);
        }
    }

    /// <summary>delete character ID [cancel] — the same command as the row menu / inspector button.</summary>
    private async Task DeleteAsync(string id, bool cancel)
    {
        var row = main.Characters.Characters.FirstOrDefault(r => r.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException("No character row " + id);
        var previous = UserDialogs.QaConfirmAnswer;
        UserDialogs.QaConfirmAnswer = !cancel;
        try
        {
            var result = await main.Characters.DeleteCharacterAsync(row);
            _log.Add(result is null
                ? $"  delete {id}: not performed (cancelled or refused)"
                : $"  delete {id}: success={result.Success} removedLines=[{string.Join(",", result.RemovedRosterEntries.Select(r => r.LineNumber))}] " +
                  $"restored={result.SelectDefRestored} error='{result.Error}' selected='{main.Characters.Selected?.Entry.Id}'");
        }
        finally
        {
            UserDialogs.QaConfirmAnswer = previous;
        }
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

    private async Task UiClickAsync(string name)
    {
        window.UpdateLayout();
        await Task.Delay(50);
        var until = DateTime.UtcNow.AddSeconds(10);
        Button? button = null;
        while (DateTime.UtcNow < until)
        {
            button = AllButtons().FirstOrDefault(b =>
                b.IsVisible && b.IsEnabled &&
                string.Equals(System.Windows.Automation.AutomationProperties.GetName(b), name, StringComparison.Ordinal));
            if (button is not null) break;
            await Task.Delay(50);
        }

        button ??= AllButtons().FirstOrDefault(b =>
                        b.IsVisible &&
                        string.Equals(System.Windows.Automation.AutomationProperties.GetName(b), name, StringComparison.Ordinal));
        if (button is null)
            throw new InvalidOperationException("No visible button named '" + name + "'");
        if (!button.IsEnabled)
            throw new InvalidOperationException("Button '" + name + "' is disabled");

        // RaiseEvent(ClickEvent) alone does not call ButtonBase.OnClick, so Command never runs.
        // Invoke via the automation peer — same path as a real UI Automation click.
        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
            invoke.Invoke();
        else if (button.Command is { } cmd && cmd.CanExecute(button.CommandParameter))
            cmd.Execute(button.CommandParameter);
        else
            throw new InvalidOperationException("Button '" + name + "' is not invokable");
        await Task.Delay(250);
        _log.Add($"  ui-click: '{name}' ok");
    }

    private async Task WaitTuningIdleAsync()
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            var status = TuneVm().Status ?? "";
            if (status.StartsWith("Saved ", StringComparison.Ordinal) ||
                status.StartsWith("Restored ", StringComparison.Ordinal) ||
                status.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("changed in the CNS", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Fix the highlighted", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(150);
                return;
            }

            await Task.Delay(50);
        }
    }

    private async Task OpenSpritesAsync()
    {
        var before = Application.Current.Windows.OfType<SpriteInspectorWindow>().Count();
        await UiClickAsync("Inspect Sprites");
        // Large SFFs with verifyDecode can take a while on cold disk.
        var until = DateTime.UtcNow.AddSeconds(90);
        SpriteInspectorWindow? win = null;
        while (DateTime.UtcNow < until)
        {
            win = Application.Current.Windows.OfType<SpriteInspectorWindow>().LastOrDefault();
            if (win is not null && win.DataContext is SpriteInspectorViewModel vm && !vm.IsLoading) break;
            await Task.Delay(100);
        }

        if (win?.DataContext is not SpriteInspectorViewModel loaded || loaded.IsLoading)
            throw new TimeoutException("Sprite Inspector did not finish loading.");
        _log.Add($"  open-sprites: before={before} title='{win.Title}' summary='{loaded.Summary}' sprites={loaded.Sprites.Count} findings={loaded.Findings.Count}");
    }

    private async Task OpenTuningAsync()
    {
        await UiClickAsync("Edit Size and Stats");
        var until = DateTime.UtcNow.AddSeconds(15);
        CharacterTuningWindow? win = null;
        while (DateTime.UtcNow < until)
        {
            win = Application.Current.Windows.OfType<CharacterTuningWindow>().LastOrDefault();
            if (win is not null && win.DataContext is CharacterTuningViewModel) break;
            await Task.Delay(50);
        }

        if (win?.DataContext is not CharacterTuningViewModel vm)
            throw new TimeoutException("Size & Stats window did not open.");
        _log.Add($"  open-tuning: cns='{vm.CnsFile}' hasSnapshot={vm.HasSnapshot} intro='{vm.Intro}'");
    }

    private void CloseWindows<T>() where T : Window
    {
        var open = Application.Current.Windows.OfType<T>().ToList();
        foreach (var w in open) w.Close();
        _log.Add($"  close: {typeof(T).Name} count={open.Count}");
    }

    private SpriteInspectorViewModel SpriteVm() =>
        Application.Current.Windows.OfType<SpriteInspectorWindow>().LastOrDefault()?.DataContext as SpriteInspectorViewModel
        ?? throw new InvalidOperationException("Sprite Inspector is not open.");

    private CharacterTuningViewModel TuneVm() =>
        Application.Current.Windows.OfType<CharacterTuningWindow>().LastOrDefault()?.DataContext as CharacterTuningViewModel
        ?? throw new InvalidOperationException("Size & Stats is not open.");

    private TuningRowViewModel? TuneRow(string label) =>
        TuneVm().StatRows.Concat(TuneVm().SizeRows).FirstOrDefault(r => r.Label.Equals(label, StringComparison.OrdinalIgnoreCase));

    private void SpriteSelect(string spec)
    {
        var vm = SpriteVm();
        if (spec.Contains(','))
        {
            var parts = spec.Split(',');
            var g = int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            var n = int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
            vm.SelectedSprite = vm.Sprites.FirstOrDefault(s => s.Group == g && s.Number == n)
                                ?? throw new InvalidOperationException("No filtered sprite " + spec);
        }
        else
        {
            var index = int.Parse(spec, CultureInfo.InvariantCulture);
            vm.SelectedSprite = vm.Sprites.ElementAtOrDefault(index)
                                ?? throw new InvalidOperationException("No sprite index " + spec);
        }
    }

    private void SpritePalette(int index)
    {
        var vm = SpriteVm();
        if (index < 0 || index >= vm.Palettes.Count) throw new InvalidOperationException("Palette index out of range");
        vm.SelectedPalette = vm.Palettes[index];
    }

    private async Task SpriteExportAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SpriteInspectorViewModel.QaExportPath = path;
        try
        {
            await UiClickAsync("Export PNG");
            await Task.Delay(300);
            var vm = SpriteVm();
            _log.Add($"  sprite-export: path='{vm.LastExportPath}' exists={File.Exists(path)} bytes={(File.Exists(path) ? new FileInfo(path).Length : 0)}");
            if (!File.Exists(path)) throw new InvalidOperationException("Export did not write " + path);
        }
        finally
        {
            SpriteInspectorViewModel.QaExportPath = null;
        }
    }

    private async Task SpriteMinMaxAsync()
    {
        var win = Application.Current.Windows.OfType<SpriteInspectorWindow>().LastOrDefault()
                  ?? throw new InvalidOperationException("Sprite Inspector is not open.");
        win.WindowState = WindowState.Minimized;
        await Task.Delay(200);
        win.WindowState = WindowState.Normal;
        await Task.Delay(200);
        var w = win.Width;
        var h = win.Height;
        win.Width = Math.Max(win.MinWidth, w - 40);
        win.Height = Math.Max(win.MinHeight, h - 40);
        await Task.Delay(150);
        win.Width = w;
        win.Height = h;
        _log.Add("  sprite-minmax: ok");
    }

    private void TuneSet(string rest)
    {
        // tune-set LABEL VALUE — label may contain spaces ("Attack distance 160").
        var split = rest.LastIndexOf(' ');
        if (split <= 0) throw new InvalidOperationException("tune-set needs LABEL VALUE");
        var label = rest[..split].Trim();
        var value = rest[(split + 1)..].Trim();
        var row = TuneRow(label) ?? throw new InvalidOperationException("No tuning row " + label);
        row.Text = value;
        _log.Add($"  tune-set: {label}='{row.Text}' error='{row.Error}' changed={row.IsChanged}");
    }

    private void TuneExternalSet(string rest)
    {
        var split = rest.IndexOf(' ');
        if (split <= 0) throw new InvalidOperationException("tune-external-set needs KEY VALUE");
        var key = rest[..split].Trim();
        var value = rest[(split + 1)..].Trim();
        var root = main.Snapshot?.Installation.RootPath ?? throw new InvalidOperationException("No root");
        var selected = main.Characters.Selected ?? throw new InvalidOperationException("No selected character");
        var cns = IKEMENLab.Core.Characters.CharacterDetailsReader.ResolveCns(root, selected.Entry)
                  ?? throw new InvalidOperationException("No CNS");
        var text = File.ReadAllText(cns);
        var replaced = System.Text.RegularExpressions.Regex.Replace(
            text, $@"(?im)^\s*{System.Text.RegularExpressions.Regex.Escape(key)}\s*=\s*.*$", $"{key} = {value}",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        if (replaced == text)
            throw new InvalidOperationException("Key not found in CNS: " + key);
        File.WriteAllText(cns, replaced);
        _log.Add($"  tune-external-set: {key}={value} path='{cns}'");
    }

    private IEnumerable<Button> AllButtons()
    {
        foreach (Window w in Application.Current.Windows)
        {
            foreach (var b in Descendants(w).OfType<Button>())
                yield return b;
        }
    }

    private void Dump(string path)
    {
        var state = new
        {
            root = main.Snapshot?.Installation.RootPath,
            page = main.SelectedNav.ToString(),
            selectedCharacter = main.Characters.Selected?.Entry.Id,
            selectedStage = main.Stages.Selected?.Entry.Id,
            dashboard = new
            {
                characters = main.Dashboard.CharacterCount,
                stages = main.Dashboard.StageCount,
                activeCharacters = main.Snapshot?.ActiveCharacterCount
            },
            lastRefreshed = main.LastRefreshedText,
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
