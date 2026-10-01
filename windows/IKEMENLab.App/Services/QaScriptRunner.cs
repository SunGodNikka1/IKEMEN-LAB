using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
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

        // The transcript was only ever accumulated in a list and then dropped, so a scripted run left no
        // record to assert on. Write it beside the script so an automated run can verify what happened.
        try
        {
            var logPath = Path.ChangeExtension(scriptPath, ".log");
            await File.WriteAllLinesAsync(logPath, _log);
        }
        catch (Exception ex)
        {
            _log.Add("! could not write log: " + ex.Message);
        }
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
            case "char-tools":
                CharTools();
                break;
            case "dump":
                Dump(rest);
                break;
            case "snapshot":
                Snapshot(rest);
                break;
            case "xray-open":
                await XRayOpenAsync(rest);
                break;
            case "xray-reopen":
                await XRayOpenAsync(rest);
                break;
            case "xray-lens":
                await XRayLensAsync(rest);
                break;
            case "xray-select":
                XRayVm().Select(rest);
                await Task.Delay(120);
                _log.Add($"  xray-select: id={XRayVm().SelectedId} title='{XRayVm().Title}' source='{XRayVm().SourceText}'");
                break;
            case "xray-search":
                XRayVm().SearchText = rest;
                await Task.Delay(120);
                _log.Add($"  xray-search: text='{rest}' results={XRayVm().SearchResults.Count} visible={XRayVm().HasSearchResults}");
                break;
            case "xray-back":
                XRayVm().BackCommand.Execute(null);
                await Task.Delay(120);
                _log.Add($"  xray-back: id={XRayVm().SelectedId}");
                break;
            case "xray-sprite":
                // Deep link: what the Sprite Inspector button does.
                XRayVm().OpenSprite(rest);
                await Task.Delay(400);
                _log.Add($"  xray-sprite: asked for {rest}");
                break;
            case "xray-resize":
                XRayResize(rest);
                break;
            case "xray-minimize":
                XRayWin().WindowState = WindowState.Minimized;
                await Task.Delay(200);
                _log.Add($"  xray-minimize: state={XRayWin().WindowState}");
                break;
            case "xray-restore":
                XRayWin().WindowState = WindowState.Normal;
                await Task.Delay(200);
                _log.Add($"  xray-restore: state={XRayWin().WindowState} size={XRayWin().Width}x{XRayWin().Height}");
                break;
            case "xray-close":
                XRayWin().Close();
                await Task.Delay(200);
                _log.Add("  xray-close: window closed");
                break;
            case "xray-dump":
                DumpXRay(rest);
                break;
            case "xray-shot":
                XRayShot(rest);
                break;
            case "playback-play":
    PlaybackPlay(rest);
    break;
case "playback-cancel":
    PlaybackCancel();
    break;
case "playback-replay":
    PlaybackReplay();
    break;
case "playback-status":
    PlaybackStatus();
    break;
case "playback-wait":
    await PlaybackWait(rest);
    break;
case "playback-inspect":
    PlaybackAction(p => p.InspectFailureCommand, "InspectFailure");
    break;
case "playback-trace":
    PlaybackAction(p => p.ViewTraceCommand, "ViewTrace");
    break;
case "xray-combos-find":
                ComboFind();
                break;
            case "xray-combos-config":
                ComboConfig(rest);
                break;
            case "xray-combo-edge":
                ComboPick((o, r) => o.Combos.SelectedEdge = r, ComboLens().Edges, rest);
                break;
            case "xray-combo-route":
                ComboPick((o, r) => o.Combos.SelectedRoute = r, ComboLens().Routes, rest);
                break;
            case "xray-combo-step":
                ComboPick((o, r) => o.Combos.SelectedStep = r, ComboLens().RouteSteps, rest);
                break;
            case "xray-combos-dump":
                DumpCombos(rest);
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

    // ------------------------------------------------------------------ Character X-Ray

    private XRayWindow XRayWin() =>
        Application.Current.Windows.OfType<XRayWindow>().LastOrDefault()
        ?? throw new InvalidOperationException("The X-Ray window is not open");

    private XRayViewModel XRayVm() => (XRayViewModel)XRayWin().DataContext;

    /// <summary>xray-open ID — the same route the Characters page X-Ray button takes.</summary>
    private async Task XRayOpenAsync(string id)
    {
        main.SelectedNav = NavPage.Characters;
        var row = main.Characters.Characters.FirstOrDefault(r => r.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException("No character row " + id);
        main.Characters.Selected = row;
        main.Characters.OpenXRayCommand.Execute(row);

        var until = DateTime.UtcNow.AddSeconds(180);
        while (XRayVm().IsLoading && DateTime.UtcNow < until) await Task.Delay(100);
        await Task.Delay(500);
        var vm = XRayVm();
        _log.Add($"  xray-open: '{vm.CharacterName}' status='{vm.Status}' selected={vm.SelectedId} title='{vm.Title}'");
    }

    /// <summary>xray-lens NAME — switches the visible lens the way the tab strip does.</summary>
    private async Task XRayLensAsync(string name)
    {
        var vm = XRayVm();
        vm.ActiveLens = Enum.Parse<XRayLensKind>(name, ignoreCase: true);
        await Task.Delay(250);
        var win = XRayWin();
        win.UpdateLayout();
        var visible = win.Content is Grid grid
            ? string.Join(",", Descendants(grid).OfType<FrameworkElement>()
                .Where(e => e.Name.StartsWith("Lens", StringComparison.Ordinal) && e.IsVisible)
                .Select(e => e.Name))
            : "n/a";
        _log.Add($"  xray-lens: active={vm.ActiveLens} visiblePanels=[{visible}] size={win.ActualWidth}x{win.ActualHeight}");
    }

    private void XRayResize(string spec)
    {
        var parts = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var w = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var h = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : XRayWin().Height;
        var win = XRayWin();
        win.WindowState = WindowState.Normal;
        win.Width = w;
        win.Height = h;
        win.UpdateLayout();
        _log.Add($"  xray-resize: {w}x{h} -> actual {win.ActualWidth}x{win.ActualHeight}");
    }

    private void XRayShot(string path)
    {
        var win = XRayWin();
        win.UpdateLayout();
        Render((FrameworkElement)win.Content, path);
    }

    // ------------------------------------------------------------------ Combos lens

    private ComboLens ComboLens() => XRayVm().Combos;

    private void ComboFind()
    {
        var lens = ComboLens();
        lens.FindRoutesCommand.Execute(null);
        // FindAsync resumes on the UI dispatcher after Task.Run. Sleep would block that same dispatcher
        // and deadlock the continuation, so pump the queue instead of waiting on it.
        var until = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < until)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (lens.FindRoutesCommand.CanExecute(null) && lens.RouteStatus != "Searching…") break;
        }

        XRayWin().UpdateLayout();
        _log.Add($"  xray-combos-find: status='{lens.RouteStatus}' edges={lens.Edges.Count} routes={lens.Routes.Count} steps={lens.RouteSteps.Count}");
    }

    /// <summary>xray-combos-config "meter=1000 moves=6 hitConfirm=false links=true fromSelected=false"</summary>
    private void ComboConfig(string spec)
    {
        var lens = ComboLens();
        foreach (var part in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            switch (kv[0])
            {
                case "meter": lens.MeterText = kv[1]; break;
                case "moves": lens.MaxMoves = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                case "hitConfirm": lens.HitConfirmOnly = bool.Parse(kv[1]); break;
                case "links": lens.AllowLinks = bool.Parse(kv[1]); break;
                case "fromSelected": lens.FromSelected = bool.Parse(kv[1]); break;
                default: throw new InvalidOperationException("Unknown combo config key " + kv[0]);
            }
        }

        _log.Add($"  xray-combos-config: meter='{lens.MeterText}' moves={lens.MaxMoves} hitConfirm={lens.HitConfirmOnly} links={lens.AllowLinks} fromSelected={lens.FromSelected}");
    }

    private void ComboPick(Action<XRayViewModel, XRayRow?> set, ObservableCollection<XRayRow> rows, string needle)
    {
        var row = rows.FirstOrDefault(r => r.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"no row matching '{needle}' among {rows.Count}");
        set(XRayVm(), row);
        XRayWin().UpdateLayout();
        _log.Add($"  picked '{row.Title}' id={row.Id}; shared selection is now {XRayVm().SelectedId}");
    }

    private void DumpCombos(string path)
    {
        var win = XRayWin();
        var lens = ComboLens();
        win.UpdateLayout();
        var state = new
        {
            character = XRayVm().CharacterName,
            lens = XRayVm().ActiveLens.ToString(),
            caption = lens.Caption,
            readiness = lens.Readiness,
            routeStatus = lens.RouteStatus,
            controls = new
            {
                maxMoves = lens.MaxMoves,
                meterText = lens.MeterText,
                hitConfirmOnly = lens.HitConfirmOnly,
                allowLinks = lens.AllowLinks,
                fromSelected = lens.FromSelected
            },
            edges = lens.Edges.Select(r => new { r.Id, r.Title, r.Subtitle, r.Glyph, r.HasConfidence, r.IsHighlighted }),
            routes = lens.Routes.Select(r => new { r.Id, r.Title, r.Subtitle, r.Glyph, r.HasConfidence }),
            routeSteps = lens.RouteSteps.Select(r => new { r.Id, r.Title, r.Glyph, r.HasConfidence, r.IsHighlighted }),
            selected = new
            {
                edge = lens.SelectedEdge?.Id,
                route = lens.SelectedRoute?.Id,
                step = lens.SelectedStep?.Id,
                shared = XRayVm().SelectedId
            },
            window = new { width = win.ActualWidth, height = win.ActualHeight, state = win.WindowState.ToString() }
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// xray-dump PATH — the observable state of the live window: the shared selection, the details/source
    /// panel and, for every lens, how many rows exist and how many the current selection highlighted.
    /// That is what proves one selection really does propagate to the other five lenses.
    /// </summary>
    private void DumpXRay(string path)
    {
        var win = XRayWin();
        var vm = XRayVm();
        win.UpdateLayout();

        var lenses = new List<object>();
        foreach (var lens in vm.Lenses)
        {
            var collections = new List<object>();
            foreach (var prop in lens.GetType().GetProperties())
            {
                if (prop.GetIndexParameters().Length != 0) continue;
                if (!prop.PropertyType.IsGenericType ||
                    prop.PropertyType.GetGenericTypeDefinition() != typeof(ObservableCollection<>)) continue;

                var items = ((System.Collections.IEnumerable?)prop.GetValue(lens))?.Cast<object>().ToList() ?? [];
                int Flag(string n) => items.Count(i => i.GetType().GetProperty(n)?.GetValue(i) is true);
                collections.Add(new
                {
                    name = prop.Name,
                    total = items.Count,
                    highlighted = Flag("IsHighlighted"),
                    selected = Flag("IsSelected"),
                    center = Flag("IsCenter"),
                    confidences = items.Select(i => i.GetType().GetProperty("Confidence")?.GetValue(i)
                                                 ?? i.GetType().GetProperty("SpriteConfidence")?.GetValue(i))
                                       .Where(c => c is not null)
                                       .GroupBy(c => c!.ToString())
                                       .Select(g => new { level = g.Key, count = g.Count() })
                                       .ToArray()
                });
            }

            lenses.Add(new { lens = lens.GetType().Name, collections });
        }

        var state = new
        {
            character = vm.CharacterName,
            status = vm.Status,
            isLoading = vm.IsLoading,
            activeLens = vm.ActiveLens.ToString(),
            selectedId = vm.SelectedId,
            title = vm.Title,
            kind = vm.KindText,
            idText = vm.IdText,
            hasSource = vm.HasSource,
            sourceText = vm.SourceText,
            sourceLines = vm.SourceLines.Count,
            sourceLinesInSpan = vm.SourceLines.Count(l => l.InSpan),
            searchText = vm.SearchText,
            searchResults = vm.SearchResults.Select(r => new { r.Id, r.Title }),
            legend = vm.Legend,
            labels = vm.Labels.Select(l => new { l.Text, l.Category, l.Glyph }),
            sections = vm.Sections.Select(s => new
            {
                s.Title,
                count = s.Items.Count,
                confidences = s.Items.GroupBy(i => i.Confidence?.ToString() ?? "none")
                             .Select(g => new { level = g.Key, count = g.Count() }).ToArray(),
                firstId = s.Items.FirstOrDefault()?.Id
            }),
            index = new { objects = vm.Index?.Objects.Count, relationships = vm.Index?.Relationships.Count },
            lenses,
            window = new { state = win.WindowState.ToString(), width = win.ActualWidth, height = win.ActualHeight, resize = win.ResizeMode.ToString() }
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Render(FrameworkElement root, string path)
    {
        var width = (int)Math.Ceiling(Math.Max(root.ActualWidth, 1));
        var height = (int)Math.Ceiling(Math.Max(root.ActualHeight, 1));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Opaque base first. Over a transparent surface every semi-transparent brush in the app
            // (buttons, chips, hover fills) reads as a solid block and the snapshot misreports the UI.
            var background = root is Control c && c.Background is Brush b ? b : Brushes.Black;
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>char-tools - list the action buttons the selected character detail panel actually renders,
    /// with the command each one is bound to. A build must be able to prove the X-Ray entry point exists
    /// and is invokable, not merely that the window type is present in the assembly.</summary>
    private void CharTools()
    {
        var inspector = Descendants(window).OfType<Views.CharacterInspectorView>().FirstOrDefault()
            ?? throw new InvalidOperationException("The character detail panel is not on screen");
        var buttons = Descendants(inspector).OfType<System.Windows.Controls.Button>().ToList();
        if (buttons.Count == 0) throw new InvalidOperationException("The character detail panel rendered no actions");
        _log.Add($"  char-tools: {buttons.Count} action(s)");
        foreach (var b in buttons)
        {
            var name = System.Windows.Automation.AutomationProperties.GetName(b);
            var can = b.Command?.CanExecute(b.CommandParameter);
            _log.Add($"    name=[{name}] visible={b.IsVisible} w={b.ActualWidth:F0} right={b.ActualWidth + b.TranslatePoint(new System.Windows.Point(0, 0), inspector).X:F0} panelW={inspector.ActualWidth:F0} bound={b.Command is not null} canExecute={can}");
        }
    }

    // ---------------------------------------------------------------- Combo Playback (M4)

    private ComboPlaybackPanel Playback() => ComboLens().Playback;

    /// <summary>playback-play [engine|dlls|dummy|stage] - configure the real setup model then invoke the real Play command.
    /// Fire-and-forget on purpose: a script has to be able to cancel, switch route or close the window while the engine runs.</summary>
    private void PlaybackPlay(string rest)
    {
        var panel = Playback();
        var parts = rest.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && parts[0].Length > 0) panel.EnginePath = parts[0];
        if (parts.Length > 1) panel.EngineDlls = parts[1];
        if (parts.Length > 2) panel.Dummy = parts[2];
        if (parts.Length > 3) panel.Stage = parts[3];
        if (panel.CurrentRouteKey.Length == 0)
            throw new InvalidOperationException("playback-play needs a selected route; use xray-combo-route first");
        if (!panel.PlayCommand.CanExecute(null))
            throw new InvalidOperationException("playback-play refused: setup not ready, or a run is already active");
        panel.PlayCommand.Execute(null);
        _log.Add($"  playback-play: invoked for route {panel.CurrentRouteKey}");
    }

    private void PlaybackCancel()
    {
        var panel = Playback();
        if (!panel.IsBusy) { _log.Add("  playback-cancel: unavailable, no run is active"); return; }
        _log.Add(panel.RequestCancel()
            ? "  playback-cancel: accepted"
            : "  playback-cancel: refused, the result was already committed");
    }

    private void PlaybackReplay()
    {
        var panel = Playback();
        if (!panel.ReplayCommand.CanExecute(null))
            throw new InvalidOperationException("playback-replay refused: no recorded run for the selected route");
        var before = panel.Outcome?.Record.Id ?? string.Empty;
        panel.ReplayCommand.Execute(null);
        _log.Add($"  playback-replay: invoked, previous run id {before}");
    }

    /// <summary>Invoke an existing panel command the way its button does, refusing when the button would be disabled.</summary>
    private void PlaybackAction(Func<ComboPlaybackPanel, System.Windows.Input.ICommand> pick, string name)
    {
        var panel = Playback();
        var command = pick(panel);
        if (!command.CanExecute(null))
            throw new InvalidOperationException($"playback-{name.ToLowerInvariant()} refused: the command is disabled for this route");
        command.Execute(null);
        _log.Add($"  playback-{name.ToLowerInvariant()}: invoked");
    }


    /// <summary>playback-status - one machine-readable key=value block so a script can assert without parsing prose.</summary>
    private void PlaybackStatus()
    {
        var p = Playback();
        var r = p.Outcome?.Record;
        _log.Add($"  status.selectedRouteKey={p.CurrentRouteKey}");
        _log.Add($"  status.runRouteKey={p.RunRouteKey}");
        _log.Add($"  status.runId={r?.Id ?? string.Empty}");
        _log.Add($"  status.sessionState={p.SessionState}");
        _log.Add($"  status.phase={p.SessionPhase}");
        _log.Add($"  status.isBusy={p.IsBusy}");
        _log.Add($"  status.verdict={r?.Status ?? string.Empty}");
        _log.Add($"  status.reason={r?.Reason ?? string.Empty}");
        _log.Add($"  status.failedStep={r?.FailedStep?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}");
        _log.Add($"  status.hasResult={p.HasResult}");
        _log.Add($"  status.showFailure={p.ShowFailure}");
        _log.Add($"  status.canPlay={p.PlayCommand.CanExecute(null)}");
        _log.Add($"  status.canCancel={p.CancelCommand.CanExecute(null)}");
        _log.Add($"  status.canReplay={p.ReplayCommand.CanExecute(null)}");
        _log.Add($"  status.canInspect={p.CanInspect}");
        _log.Add($"  status.canViewTrace={p.ViewTraceCommand.CanExecute(null)}");
        _log.Add($"  status.recordDirectory={r?.Directory ?? string.Empty}");
        _log.Add($"  status.tracePath={r?.TracePath ?? string.Empty}");
        _log.Add($"  status.enginePath={p.EnginePath}");
        _log.Add($"  status.engineDlls={p.EngineDlls}");
        _log.Add($"  status.recordedEngineSha={r?.EngineSha256 ?? string.Empty}");
        _log.Add($"  status.engineSource={p.Outcome?.Report.EngineSource ?? string.Empty}");
        _log.Add($"  status.engineVersion={p.Outcome?.Report.EngineVersion ?? string.Empty}");
        _log.Add($"  status.dummy={r?.Dummy ?? string.Empty}");
        _log.Add($"  status.stage={r?.Stage ?? string.Empty}");
        _log.Add($"  status.notificationsApplied={p.NotificationsApplied}");
        _log.Add($"  status.notificationsDropped={p.NotificationsDropped}");
        _log.Add($"  status.sessionClosed={p.SessionIsClosed}");
        _log.Add($"  status.error={p.SessionError}");
        _log.Add($"  status.setupSummary={p.SetupSummary}");
    }

    /// <summary>playback-wait &lt;running|verdict|idle|phase=NAME&gt; [timeoutMs] - bounded; always resolves or throws.</summary>
    private async Task PlaybackWait(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var what = parts.ElementAtOrDefault(0) ?? "verdict";
        var ms = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 180000;
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            var p = Playback();
            var hit = what switch
            {
                "running" => p.SessionState == "Running",
                "idle" => p.SessionState == "Idle",
                "verdict" => p.HasResult || p.SessionState is "Cancelled" or "Error",
                var s when s.StartsWith("phase=", StringComparison.OrdinalIgnoreCase) =>
                    p.SessionPhase.Contains(s["phase=".Length..], StringComparison.OrdinalIgnoreCase),
                _ => throw new InvalidOperationException($"unknown playback-wait condition '{what}'")
            };
            if (hit)
            {
                _log.Add($"  playback-wait: {what} satisfied (state={p.SessionState} phase={p.SessionPhase})");
                return;
            }
            await Task.Delay(100);
        }

        throw new TimeoutException($"playback-wait timed out after {ms}ms waiting for '{what}' (state={Playback().SessionState} phase={Playback().SessionPhase})");
    }
}


