using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>A table in the diagram's list, ticked to be drawn.</summary>
public sealed class ErdTableChoice : ObservableObject
{
    public ErdTableChoice(ErdTable table, SolutionComponentItem item, IReadOnlyList<string> solutions)
    {
        Table = table;
        Item = item;
        Solutions = solutions;
    }

    public ErdTable Table { get; }

    /// <summary>The table's row in a solution, to open its details from the diagram.</summary>
    public SolutionComponentItem Item { get; }

    /// <summary>The ticked solutions holding the table.</summary>
    public IReadOnlyList<string> Solutions { get; }

    /// <summary>The logical name, and the solutions holding it where there are several.</summary>
    public string Detail => Table.IsIntersect
        ? $"{Table.LogicalName}  (many-to-many table)"
        : Solutions.Count > 1 ? $"{Table.LogicalName}  ({string.Join(", ", Solutions)})" : Table.LogicalName;

    private bool _isChosen;
    public bool IsChosen
    {
        get => _isChosen;
        set => SetProperty(ref _isChosen, value);
    }
}

/// <summary>An entry in the column detail list.</summary>
public sealed record ErdColumnChoice(ErdColumnDetail Value, string Label);

/// <summary>An entry in the layout list.</summary>
public sealed record ErdLayoutChoice(ErdLayoutStyle Value, string Label);

/// <summary>
/// An entity relationship diagram of the tables in one or more solutions, drawn as it is shaped -
/// which tables, how many of their columns, and whether the tables outside they look up are shown -
/// and saved as SVG, PNG or Mermaid.
/// </summary>
public sealed class ErdViewModel : ObservableObject, IDisposable, Ai.IAiContext
{
    private const int TableType = 1;
    private const int TableReads = 4;

    public const double MinZoom = 0.1;
    public const double MaxZoom = 2;

    private readonly EnvironmentSessionViewModel _session;

    /// <summary>Tables read already, by metadata id - ticking another solution reads only what is new.</summary>
    private readonly Dictionary<Guid, ErdTable?> _read = new();
    private IReadOnlyDictionary<string, string?>? _displayNames;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _layoutCts;
    private int _layoutVersion;
    private IReadOnlyList<SolutionInfo> _drawn = [];

    /// <summary>Stops reading and laying out; nobody is left to see the diagram.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _layoutCts?.Cancel();
        _layoutCts?.Dispose();
        _layoutCts = null;
    }

    public ErdViewModel(EnvironmentSessionViewModel session)
    {
        _session = session;

        DrawCommand = new AsyncRelayCommand(_ => DrawAsync(), _ => !IsBusy && Chosen.Count > 0);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        ClearCommand = new RelayCommand(_ =>
        {
            foreach (var choice in Choices) choice.IsChosen = false;
        }, _ => !IsBusy && Choices.Any(c => c.IsChosen));
        AllTablesCommand = new RelayCommand(_ => TickTables(true), _ => Tables.Count > 0);
        NoTablesCommand = new RelayCommand(_ => TickTables(false), _ => Tables.Count > 0);
        SuggestedTablesCommand = new RelayCommand(_ => TickSuggested(), _ => Tables.Count > 0);
        NeighboursCommand = new RelayCommand(p => TickNeighbours(p as ErdTableChoice), p => p is ErdTableChoice);

        CopyMermaidCommand = new RelayCommand(_ => CopyMermaid(), _ => _diagram is not null);
        CopyImageCommand = new RelayCommand(_ => CopyImage(), _ => Placed is not null);
        SaveSvgCommand = new RelayCommand(_ => SaveSvg(), _ => Placed is not null);
        SavePngCommand = new RelayCommand(_ => SavePng(), _ => Placed is not null);
        SaveMermaidCommand = new RelayCommand(_ => SaveMermaid(), _ => _diagram is not null);

        ZoomInCommand = new RelayCommand(_ => Zoom *= 1.25);
        ZoomOutCommand = new RelayCommand(_ => Zoom /= 1.25);
        ResetZoomCommand = new RelayCommand(_ => Zoom = 1);

        // The solution on screen, ticked - whether or not the list holds it yet.
        var solutions = session.Solutions.ToList();
        if (session.SelectedSolution is { } selected && !solutions.Contains(selected)) solutions.Insert(0, selected);

        foreach (var solution in solutions)
        {
            var choice = new DocumentedSolution(solution) { IsChosen = ReferenceEquals(solution, session.SelectedSolution) };
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DocumentedSolution.IsChosen)) ChoicesChanged();
            };
            Choices.Add(choice);
        }

        ChoicesView = CollectionViewSource.GetDefaultView(Choices);
        ChoicesView.Filter = o => o is DocumentedSolution c && Matches(c, _solutionFilter);

        TablesView = CollectionViewSource.GetDefaultView(Tables);
        TablesView.Filter = o => o is ErdTableChoice t && Matches(t, _tableFilter);
    }

    /// <summary>Asks where to save, with a file filter and a suggested name; null when cancelled. Replaced in tests.</summary>
    internal Func<string, string, string?> PickFile { get; set; } = (filter, suggested) =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = suggested };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    /// <summary>Lays a diagram out; the font's measure in the app, a stand-in in tests.</summary>
    internal ErdTextMeasure Measure { get; set; } = Views.ErdDrawing.Measure;

    /// <summary>Draws the placed diagram for the preview; null in tests, which have no use for it.</summary>
    internal Func<ErdPlacedDiagram, ImageSource?> Render { get; set; } = placed => new DrawingImage(Views.ErdDrawing.Draw(placed));

    /// <summary>Puts an image on the clipboard. Replaced in tests.</summary>
    internal Action<BitmapSource> SetClipboardImage { get; set; } = Clipboard.SetImage;

    /// <summary>The tab the screen was opened from, for its environment line.</summary>
    public EnvironmentSessionViewModel Session => _session;

    public string Title => $"Entity relationships — {_session.Title}";

    // ---------------------------------------------------------------- solutions

    /// <summary>Every solution in the environment, to tick the ones whose tables are drawn.</summary>
    public ObservableCollection<DocumentedSolution> Choices { get; } = new();

    public ICollectionView ChoicesView { get; }

    private string _solutionFilter = string.Empty;
    /// <summary>Narrows the list by friendly or unique name; ticked solutions stay ticked when hidden.</summary>
    public string SolutionFilter
    {
        get => _solutionFilter;
        set
        {
            if (SetProperty(ref _solutionFilter, value ?? string.Empty)) ChoicesView.Refresh();
        }
    }

    private static bool Matches(DocumentedSolution choice, string filter) =>
        filter.Length == 0 ||
        choice.Solution.FriendlyName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        choice.Solution.UniqueName.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private bool _includePatches;
    /// <summary>Draws the tables of each ticked solution's patches with it.</summary>
    public bool IncludePatches
    {
        get => _includePatches;
        set
        {
            if (SetProperty(ref _includePatches, value)) ChoicesChanged();
        }
    }

    /// <summary>The solutions to draw: those ticked, then - if asked for - their patches, each once.</summary>
    public IReadOnlyList<SolutionInfo> Chosen
    {
        get
        {
            var chosen = Choices.Where(c => c.IsChosen).Select(c => c.Solution).ToList();
            if (!IncludePatches) return chosen;

            var ids = chosen.Select(s => s.SolutionId).ToHashSet();
            var patches = Choices.Select(c => c.Solution)
                .Where(s => s.ParentSolutionId is { } parent && ids.Contains(parent) && !ids.Contains(s.SolutionId));
            return [.. chosen, .. patches];
        }
    }

    private void ChoicesChanged()
    {
        OnPropertyChanged(nameof(SolutionLabel));
        DrawCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The solutions ticked - the line under the heading.</summary>
    public string SolutionLabel => Chosen switch
    {
        [] => "No solution ticked",
        [var one] => one.DisplayLabel,
        var many => $"{many.Count:N0} solutions: {string.Join(", ", many.Select(s => s.FriendlyName))}"
    };

    // ---------------------------------------------------------------- tables

    /// <summary>The tables of the solutions last read, ticked to be drawn.</summary>
    public ObservableCollection<ErdTableChoice> Tables { get; } = new();

    public ICollectionView TablesView { get; }

    private string _tableFilter = string.Empty;
    /// <summary>Narrows the table list by display or logical name.</summary>
    public string TableFilter
    {
        get => _tableFilter;
        set
        {
            if (SetProperty(ref _tableFilter, value ?? string.Empty)) TablesView.Refresh();
        }
    }

    private static bool Matches(ErdTableChoice choice, string filter) =>
        filter.Length == 0 ||
        choice.Table.Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        choice.Table.LogicalName.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public bool HasTables => Tables.Count > 0;

    public string TablesLabel => Tables.Count == 0
        ? "Tables"
        : $"Tables  ({Tables.Count(t => t.IsChosen):N0} of {Tables.Count:N0})";

    /// <summary>Ticks or unticks every table the filter shows.</summary>
    private void TickTables(bool chosen)
    {
        foreach (var table in TablesView.Cast<ErdTableChoice>()) table.IsChosen = chosen;
    }

    /// <summary>
    /// The solutions' own tables, the out-of-box tables they extend, and the tables their own
    /// lookups point at - an out-of-box table held only for a form or a view stays out.
    /// </summary>
    private IReadOnlySet<string> Suggested()
    {
        return ErdBuilder.Suggested(Tables.Select(t => t.Table).ToList(), Prefixes());
    }

    /// <summary>
    /// The drawn solutions' publisher prefixes - "ect" - which name the relationships they made.
    /// The default solution's publisher counts only when it is drawn alone: beside real
    /// solutions, its prefix names whatever makers built outside them.
    /// </summary>
    private IReadOnlyList<string> Prefixes()
    {
        var solutions = _drawn.All(s => s.IsDefaultSolution) ? _drawn : _drawn.Where(s => !s.IsDefaultSolution);
        return solutions.Select(s => s.PublisherPrefix).OfType<string>().Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private ErdOptions Options() => new(Columns, ShowOutsideTables, ShowSystemRelationships, ShowPeople, Prefixes(), ShowReferenceTables, ShowStandalone);

    private void TickSuggested()
    {
        var suggested = Suggested();
        foreach (var table in Tables) table.IsChosen = suggested.Contains(table.Table.LogicalName);
    }

    /// <summary>Draws one table and the tables related to it, to read a large diagram a piece at a time.</summary>
    private void TickNeighbours(ErdTableChoice? choice)
    {
        if (choice is null) return;

        var neighbours = ErdBuilder.Neighbours(choice.Table.LogicalName, Tables.Select(t => t.Table).ToList(), Options());
        foreach (var table in Tables) table.IsChosen = neighbours.Contains(table.Table.LogicalName);

        Status = neighbours.Count == 1
            ? $"{choice.Table.Label} has no relationship to another table in these solutions."
            : $"Drawing {choice.Table.Label} and the {neighbours.Count - 1:N0} table(s) related to it.";
    }

    private void TableChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ErdTableChoice.IsChosen)) return;

        OnPropertyChanged(nameof(TablesLabel));
        Relayout();
    }

    // ---------------------------------------------------------------- options

    public IReadOnlyList<ErdLayoutChoice> LayoutChoices { get; } =
    [
        new(ErdLayoutStyle.Grouped, "Grouped - related tables together"),
        new(ErdLayoutStyle.Layered, "Layered - tables looked up above")
    ];

    private ErdLayoutStyle _layout = ErdLayoutStyle.Grouped;
    public ErdLayoutStyle Layout
    {
        get => _layout;
        set
        {
            if (SetProperty(ref _layout, value)) Relayout();
        }
    }

    private bool _showStandalone;
    /// <summary>Draws the chosen tables with no line to another, which say nothing about the relationships.</summary>
    public bool ShowStandalone
    {
        get => _showStandalone;
        set
        {
            if (SetProperty(ref _showStandalone, value)) Relayout();
        }
    }

    private bool _showReferenceTables;
    /// <summary>Draws the tables only looked up - types, codes, statuses; otherwise the lookups to them are only listed.</summary>
    public bool ShowReferenceTables
    {
        get => _showReferenceTables;
        set
        {
            if (SetProperty(ref _showReferenceTables, value)) Relayout();
        }
    }

    public IReadOnlyList<ErdColumnChoice> ColumnChoices { get; } =
    [
        new(ErdColumnDetail.None, "Table names only"),
        new(ErdColumnDetail.Drawn, "Keys and the lookups drawn"),
        new(ErdColumnDetail.Keys, "Keys and every lookup"),
        new(ErdColumnDetail.Custom, "Keys, lookups and custom columns"),
        new(ErdColumnDetail.All, "Every column")
    ];

    private ErdColumnDetail _columns = ErdColumnDetail.Drawn;
    public ErdColumnDetail Columns
    {
        get => _columns;
        set
        {
            if (SetProperty(ref _columns, value)) Relayout();
        }
    }

    private bool _showOutsideTables = true;
    /// <summary>Draws the tables outside the solutions that the chosen tables look up, without their columns.</summary>
    public bool ShowOutsideTables
    {
        get => _showOutsideTables;
        set
        {
            if (SetProperty(ref _showOutsideTables, value)) Relayout();
        }
    }

    private bool _showSystemRelationships;
    /// <summary>Draws the platform's relationships too - owner, regarding, Microsoft's apps' - not only the solutions' own.</summary>
    public bool ShowSystemRelationships
    {
        get => _showSystemRelationships;
        set
        {
            if (SetProperty(ref _showSystemRelationships, value)) Relayout();
        }
    }

    private bool _showPeople;
    /// <summary>Draws users, teams and business units and the lines to them; otherwise their lookups are only listed.</summary>
    public bool ShowPeople
    {
        get => _showPeople;
        set
        {
            if (SetProperty(ref _showPeople, value)) Relayout();
        }
    }

    // ---------------------------------------------------------------- the diagram

    private ErdDiagram? _diagram;

    /// <summary>The diagram as Mermaid writes it: every lookup listed, where the boxes list only those drawn.</summary>
    private ErdDiagram? _mermaid;

    private ErdPlacedDiagram? _placed;
    /// <summary>The diagram as laid out - what is shown and saved.</summary>
    public ErdPlacedDiagram? Placed
    {
        get => _placed;
        private set
        {
            if (!SetProperty(ref _placed, value)) return;

            OnPropertyChanged(nameof(HasDiagram));
            OnPropertyChanged(nameof(EmptyHeading));
            OnPropertyChanged(nameof(EmptyText));
            CopyMermaidCommand.RaiseCanExecuteChanged();
            CopyImageCommand.RaiseCanExecuteChanged();
            SaveSvgCommand.RaiseCanExecuteChanged();
            SavePngCommand.RaiseCanExecuteChanged();
            SaveMermaidCommand.RaiseCanExecuteChanged();
        }
    }

    private ImageSource? _image;
    public ImageSource? Image
    {
        get => _image;
        private set => SetProperty(ref _image, value);
    }

    public bool HasDiagram => Placed is { Boxes.Count: > 0 };

    public string EmptyHeading => IsBusy ? "Reading the tables…" : Tables.Count > 0 ? "No tables ticked" : "Nothing drawn yet";

    public string EmptyText => IsBusy
        ? "Each table's columns and relationships are read from the environment's metadata."
        : Tables.Count > 0
            ? "Tick the tables to draw in the list on the left."
            : "Tick one or more solutions and choose Draw. Their tables are drawn with the lookups and many-to-many relationships between them.";

    private double _zoom = 1;
    public double Zoom
    {
        get => _zoom;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, MinZoom, MaxZoom), 3);
            if (SetProperty(ref _zoom, clamped)) OnPropertyChanged(nameof(ZoomLabel));
        }
    }

    public string ZoomLabel => $"{Zoom:P0}";

    // ---------------------------------------------------------------- commands and state

    public AsyncRelayCommand DrawCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand AllTablesCommand { get; }
    public RelayCommand NoTablesCommand { get; }
    public RelayCommand SuggestedTablesCommand { get; }
    public RelayCommand NeighboursCommand { get; }
    public RelayCommand CopyMermaidCommand { get; }
    public RelayCommand CopyImageCommand { get; }
    public RelayCommand SaveSvgCommand { get; }
    public RelayCommand SavePngCommand { get; }
    public RelayCommand SaveMermaidCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ResetZoomCommand { get; }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;

            DrawCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            ClearCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(EmptyHeading));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    private string _status = "Tick the solutions whose tables to draw, then Draw.";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    // ---------------------------------------------------------------- reading

    private async Task DrawAsync()
    {
        var solutions = Chosen;
        if (_session.Client is not { } client || solutions.Count == 0) return;

        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        var clock = Stopwatch.StartNew();

        try
        {
            // Each table once, with the solutions that hold it.
            var found = new Dictionary<Guid, (SolutionComponentItem Item, List<string> Solutions)>();
            for (var i = 0; i < solutions.Count; i++)
            {
                Status = solutions.Count == 1
                    ? $"Reading the tables of {solutions[i].FriendlyName}…"
                    : $"Reading the tables of {solutions[i].FriendlyName} ({i + 1:N0} of {solutions.Count:N0})…";

                var components = await client.GetSolutionComponentsAsync(solutions[i].SolutionId, ct: cts.Token);
                foreach (var item in components.Where(c => c.ComponentType == TableType))
                {
                    if (!found.TryGetValue(item.ObjectId, out var entry)) found[item.ObjectId] = entry = (item, new List<string>());
                    entry.Solutions.Add(solutions[i].FriendlyName);
                }
            }

            var unread = found.Keys.Where(id => !_read.ContainsKey(id)).ToList();
            var done = 0;
            using (var gate = new SemaphoreSlim(TableReads))
            {
                await Task.WhenAll(unread.Select(async id =>
                {
                    await gate.WaitAsync(cts.Token);
                    try
                    {
                        var item = found[id].Item;
                        var table = await client.GetErdTableAsync(id, item.SchemaName ?? item.Name, cts.Token);
                        lock (_read) _read[id] = table;
                    }
                    finally
                    {
                        gate.Release();
                    }

                    var count = Interlocked.Increment(ref done);
                    Status = $"Reading the columns and relationships of {unread.Count:N0} table(s) - {count:N0} done…";
                }));
            }

            if (_displayNames is null)
            {
                try
                {
                    _displayNames = await client.GetTableDisplayNamesAsync(cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The tables outside are named by logical name instead.
                    Log.Warn("ERD: could not read the table display names - " + ex.Message);
                }
            }

            // A table listed before keeps its tick; a new one is ticked if it is suggested.
            var before = Tables.ToDictionary(t => t.Table.LogicalName, t => t.IsChosen, StringComparer.OrdinalIgnoreCase);
            var missing = 0;

            foreach (var table in Tables) table.PropertyChanged -= TableChanged;
            Tables.Clear();
            _drawn = solutions;

            foreach (var (id, (item, holders)) in found)
            {
                if (_read.GetValueOrDefault(id) is not { } table)
                {
                    missing++;
                    continue;
                }

                Tables.Add(new ErdTableChoice(table, item, holders) { IsChosen = before.GetValueOrDefault(table.LogicalName) });
            }

            var suggested = Suggested();
            foreach (var choice in Tables)
            {
                if (!before.ContainsKey(choice.Table.LogicalName)) choice.IsChosen = suggested.Contains(choice.Table.LogicalName);
                choice.PropertyChanged += TableChanged;
            }

            TablesView.SortDescriptions.Clear();
            TablesView.SortDescriptions.Add(new SortDescription("Table.Label", ListSortDirection.Ascending));

            OnPropertyChanged(nameof(HasTables));
            OnPropertyChanged(nameof(TablesLabel));
            AllTablesCommand.RaiseCanExecuteChanged();
            NoTablesCommand.RaiseCanExecuteChanged();
            SuggestedTablesCommand.RaiseCanExecuteChanged();

            await RelayoutAsync();

            var skipped = missing == 0 ? string.Empty : $" {missing:N0} table(s) could not be read.";
            Status = found.Count == 0
                ? $"No tables in {(solutions.Count == 1 ? "this solution" : "these solutions")}."
                : $"{DrawnSummary()} Read in {clock.Elapsed.TotalSeconds:0.0} s.{skipped}";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Status = "Could not read the tables - " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            IsBusy = false;
        }
    }

    /// <summary>"12 tables and 15 relationships, with 3 tables outside the solutions."</summary>
    private string DrawnSummary()
    {
        if (_diagram is not { } diagram) return string.Empty;

        var outside = diagram.Boxes.Count(b => b.IsOutside);
        var inside = diagram.Boxes.Count - outside;
        return $"{inside:N0} table(s) and {diagram.Links.Count:N0} relationship(s)" +
               (outside == 0 ? "." : $", with {outside:N0} table(s) outside the solutions.");
    }

    // ---------------------------------------------------------------- layout

    /// <summary>Lays the diagram out again once ticking and choosing has settled for a moment.</summary>
    private async void Relayout()
    {
        var version = ++_layoutVersion;
        await Task.Delay(150);
        // Nothing read yet: the options wait for the first Draw.
        if (version != _layoutVersion || IsBusy || Tables.Count == 0) return;

        try
        {
            await RelayoutAsync();
            Status = DrawnSummary();
        }
        catch (OperationCanceledException)
        {
            // A newer layout took its place.
        }
        catch (Exception ex)
        {
            Status = "Could not lay the diagram out - " + ex.Message;
        }
    }

    internal async Task RelayoutAsync()
    {
        _layoutCts?.Cancel();
        _layoutCts?.Dispose();
        var cts = _layoutCts = new CancellationTokenSource();

        var tables = Tables.Where(t => t.IsChosen).Select(t => t.Table).ToList();
        var diagram = ErdBuilder.Build(tables, Options(), _displayNames);
        var (title, caption) = Heading(diagram);
        var measure = Measure;

        var style = Layout;
        var placed = await Task.Run(() => ErdLayout.Arrange(diagram, measure, title, caption, cts.Token, style), cts.Token);
        cts.Token.ThrowIfCancellationRequested();

        _diagram = diagram;
        _mermaid = Columns == ErdColumnDetail.Drawn
            ? ErdBuilder.Build(tables, Options() with { Columns = ErdColumnDetail.Keys }, _displayNames)
            : diagram;
        Placed = placed;
        Image = HasDiagram ? Render(placed) : null;
    }

    /// <summary>The diagram's heading: the solutions; then the environment, the tables and the day.</summary>
    private (string Title, string Caption) Heading(ErdDiagram diagram)
    {
        var names = _drawn.Select(s => s.FriendlyName).ToList();
        var title = names.Count <= 3
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(2))} and {names.Count - 2:N0} more solutions";

        var tables = diagram.Boxes.Count(b => !b.IsOutside);
        return (title, $"{_session.Title}  ·  {tables:N0} table{(tables == 1 ? string.Empty : "s")}  ·  {DateTime.Now:d MMM yyyy}");
    }

    // ---------------------------------------------------------------- opening a table

    /// <summary>Opens the details of the table drawn at a point of the diagram, if it is one of the solutions'.</summary>
    public void OpenTableAt(double x, double y)
    {
        if (Placed?.BoxAt(x, y) is not { } box) return;

        if (box.Box.IsOutside)
        {
            Status = $"{box.Box.LogicalName} is outside the solutions drawn.";
            return;
        }

        if (Tables.FirstOrDefault(t => string.Equals(t.Table.LogicalName, box.Box.LogicalName, StringComparison.OrdinalIgnoreCase)) is { } table)
        {
            _session.OpenDetails(table.Item);
        }
    }

    // ---------------------------------------------------------------- export

    /// <summary>"contoso_core-erd", or for several solutions the environment's and the day's.</summary>
    internal string FileStem() => _drawn.Count == 1
        ? $"{DocPage.Slug(_drawn[0].UniqueName)}-erd"
        : $"{DocPage.Slug(_session.Title)}-erd-{DateTime.Now:yyyy-MM-dd}";

    private string Mermaid() => ErdMermaidWriter.ToMermaid(_mermaid ?? _diagram!, Placed?.Title);

    /// <summary>On screen the diagram is boxes and lines; the assistant reads it as its Mermaid.</summary>
    public string? DescribeForAi() => _diagram is null
        ? null
        : "The entity relationship diagram, as Mermaid (the screen draws the same):\n```mermaid\n" + Mermaid().TrimEnd() + "\n```";

    private void CopyMermaid()
    {
        if (_diagram is null) return;

        Status = ClipboardText.TryCopy(Mermaid(), out var failure)
            ? "Copied the diagram as Mermaid - paste it into a ```mermaid block in a wiki, README or pull request."
            : "Could not copy - " + failure;
    }

    private void CopyImage()
    {
        if (Placed is not { } placed) return;

        try
        {
            SetClipboardImage(Views.ErdDrawing.Render(placed));
            Status = "Copied the diagram as an image.";
        }
        catch (Exception ex)
        {
            Status = "Could not copy the image - " + ex.Message;
        }
    }

    private void SaveSvg()
    {
        if (Placed is not { } placed) return;
        Save("SVG image (*.svg)|*.svg", ".svg", path => File.WriteAllText(path, ErdSvgWriter.ToSvg(placed)));
    }

    private void SavePng()
    {
        if (Placed is not { } placed) return;
        Save("PNG image (*.png)|*.png", ".png", path =>
        {
            using var stream = File.Create(path);
            Views.ErdDrawing.SavePng(Views.ErdDrawing.Render(placed), stream);
        });
    }

    /// <summary>A .mmd file holds the diagram alone; a .md file holds it in a fenced block, ready to render.</summary>
    private void SaveMermaid()
    {
        if (_diagram is null) return;
        Save("Mermaid (*.mmd)|*.mmd|Markdown (*.md)|*.md", ".mmd", path =>
        {
            var mermaid = Mermaid();
            File.WriteAllText(path, Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
                ? $"```mermaid\n{mermaid}```\n"
                : mermaid);
        });
    }

    private void Save(string filter, string extension, Action<string> write)
    {
        if (PickFile(filter, FileStem() + extension) is not { } path) return;

        try
        {
            write(path);
            Status = $"Saved {path}.";
        }
        catch (Exception ex)
        {
            Status = "Could not save - " + ex.Message;
        }
    }
}
