using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>One column, as the settings dialog lets it be ticked.</summary>
public sealed class ColumnChoice : ObservableObject
{
    public required EntityColumn Column { get; init; }

    private bool _isCompared = true;
    public bool IsCompared
    {
        get => _isCompared;
        set => SetProperty(ref _isCompared, value);
    }

    private bool _isKey;
    /// <summary>Only meaningful while the key is a hand-picked set of columns.</summary>
    public bool IsKey
    {
        get => _isKey;
        set => SetProperty(ref _isKey, value);
    }

    public string LogicalName => Column.LogicalName;
    public string DisplayName => Column.DisplayName ?? string.Empty;
    public string TypeLabel => Column.TypeLabel;
}

/// <summary>One entry in the key dropdown.</summary>
public sealed class KeyOption
{
    public required string Label { get; init; }
    public required RecordKeySource Source { get; init; }
    public string? AlternateKeyName { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();

    public override string ToString() => Label;
}

/// <summary>
/// How one table takes part in the comparison: what identifies a row, which rows to read, and
/// which columns count. The column list comes from the source environment - the comparison itself
/// narrows it to what the target also has.
/// </summary>
public sealed class ReferenceEntitySettingsViewModel : ObservableObject
{
    private readonly ReferenceEntityConfig _config;
    private readonly DataverseClient _client;
    private readonly EntitySummary _entity;

    public ReferenceEntitySettingsViewModel(ReferenceEntityConfig config, EntitySummary entity, DataverseClient client)
    {
        _config = config;
        _entity = entity;
        _client = client;

        _filter = config.Filter ?? string.Empty;

        Columns = new ObservableCollection<ColumnChoice>();
        ColumnsView = CollectionViewSource.GetDefaultView(Columns);
        ColumnsView.Filter = FilterColumn;

        CompareAllCommand = new RelayCommand(_ => SetAllCompared(true), _ => Columns.Count > 0);
        CompareNoneCommand = new RelayCommand(_ => SetAllCompared(false), _ => Columns.Count > 0);
        ResetDefaultsCommand = new RelayCommand(_ => ApplyExclusions(null), _ => Columns.Count > 0);
    }

    public string Title => $"{_entity.Label} - comparison settings";

    public ObservableCollection<ColumnChoice> Columns { get; }
    public ICollectionView ColumnsView { get; }
    public ObservableCollection<KeyOption> KeyOptions { get; } = new();

    public RelayCommand CompareAllCommand { get; }
    public RelayCommand CompareNoneCommand { get; }
    public RelayCommand ResetDefaultsCommand { get; }

    private bool _isBusy = true;
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    private string _status = "Loading table metadata...";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private KeyOption? _selectedKey;
    public KeyOption? SelectedKey
    {
        get => _selectedKey;
        set
        {
            if (!SetProperty(ref _selectedKey, value)) return;

            OnPropertyChanged(nameof(IsCustomKey));
            SyncKeyTicks();
        }
    }

    public bool IsCustomKey => SelectedKey?.Source == RecordKeySource.Columns;

    private string _filter;
    /// <summary>OData <c>$filter</c>, applied to both environments' row queries.</summary>
    public string Filter
    {
        get => _filter;
        set => SetProperty(ref _filter, value);
    }

    private string _columnSearch = string.Empty;
    public string ColumnSearch
    {
        get => _columnSearch;
        set
        {
            if (SetProperty(ref _columnSearch, value)) ColumnsView.Refresh();
        }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var columnsTask = _client.GetEntityColumnsAsync(_entity.LogicalName, ct);
            var keysTask = _client.GetAlternateKeysAsync(_entity.LogicalName, ct);

            await Task.WhenAll(columnsTask, keysTask);

            var columns = await columnsTask;
            var keys = await keysTask;

            Columns.Clear();
            foreach (var column in columns) Columns.Add(new ColumnChoice { Column = column });

            BuildKeyOptions(keys);
            ApplyExclusions(_config.ExcludedColumns);
            SelectSavedKey(keys);

            Status = keys.Count == 0
                ? $"{columns.Count:N0} comparable columns. This table defines no alternate keys."
                : $"{columns.Count:N0} comparable columns, {keys.Count} alternate key(s).";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Status = "Could not read the table metadata - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            CompareAllCommand.RaiseCanExecuteChanged();
            CompareNoneCommand.RaiseCanExecuteChanged();
            ResetDefaultsCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Writes the dialog back into the configuration it was opened for.</summary>
    public bool Apply(out string? error)
    {
        error = null;

        var key = SelectedKey;
        if (key is null)
        {
            error = "Choose what identifies a row.";
            return false;
        }

        if (key.Source == RecordKeySource.Columns)
        {
            var picked = Columns.Where(c => c.IsKey).Select(c => c.LogicalName).ToList();
            if (picked.Count == 0)
            {
                error = "Tick at least one column to use as the key.";
                return false;
            }

            _config.KeyColumns = picked;
            _config.AlternateKeyName = null;
        }
        else if (key.Source == RecordKeySource.AlternateKey)
        {
            // The key's columns are stored too, so the comparison still works the same way if the
            // key is later dropped from the table.
            _config.AlternateKeyName = key.AlternateKeyName;
            _config.KeyColumns = key.Columns.ToList();
        }
        else
        {
            _config.AlternateKeyName = null;
            _config.KeyColumns = null;
        }

        _config.KeySource = key.Source;
        _config.Filter = string.IsNullOrWhiteSpace(Filter) ? null : Filter.Trim();
        _config.ExcludedColumns = Columns.Where(c => !c.IsCompared).Select(c => c.LogicalName).ToList();

        return true;
    }

    private void BuildKeyOptions(IReadOnlyList<AlternateKeyInfo> keys)
    {
        KeyOptions.Clear();

        KeyOptions.Add(new KeyOption
        {
            Label = $"Primary key - {_entity.PrimaryIdAttribute}",
            Source = RecordKeySource.PrimaryId,
            Columns = new[] { _entity.PrimaryIdAttribute }
        });

        foreach (var key in keys)
        {
            KeyOptions.Add(new KeyOption
            {
                Label = "Alternate key - " + key.Label,
                Source = RecordKeySource.AlternateKey,
                AlternateKeyName = key.LogicalName,
                Columns = key.KeyAttributes
            });
        }

        KeyOptions.Add(new KeyOption { Label = "Columns I pick", Source = RecordKeySource.Columns });
    }

    private void SelectSavedKey(IReadOnlyList<AlternateKeyInfo> keys)
    {
        SelectedKey = _config.KeySource switch
        {
            RecordKeySource.AlternateKey => KeyOptions.FirstOrDefault(
                                                o => o.Source == RecordKeySource.AlternateKey &&
                                                     string.Equals(o.AlternateKeyName, _config.AlternateKeyName,
                                                         StringComparison.OrdinalIgnoreCase))
                                            ?? KeyOptions[0],

            RecordKeySource.Columns => KeyOptions.First(o => o.Source == RecordKeySource.Columns),

            _ => KeyOptions[0]
        };

        // A saved alternate key that no longer exists falls back to the primary key above; say so
        // rather than letting the dialog quietly show a different key than was saved.
        if (_config.KeySource == RecordKeySource.AlternateKey &&
            SelectedKey?.Source != RecordKeySource.AlternateKey)
        {
            Status = $"The saved alternate key '{_config.AlternateKeyName}' no longer exists on this table " +
                     $"({keys.Count} found); fell back to the primary key.";
        }

        SyncKeyTicks();
    }

    /// <summary>Puts the key ticks where the chosen key says they should be, so switching to
    /// "columns I pick" starts from the key that was in force rather than from nothing.</summary>
    private void SyncKeyTicks()
    {
        var names = SelectedKey?.Source == RecordKeySource.Columns
            ? _config.KeyColumns ?? SelectedKey.Columns
            : SelectedKey?.Columns ?? Array.Empty<string>();

        var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var column in Columns) column.IsKey = set.Contains(column.LogicalName);
    }

    private void ApplyExclusions(IReadOnlyList<string>? excluded)
    {
        var set = (excluded ?? SystemColumns.DefaultExclusions(Columns.Select(c => c.Column)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var column in Columns) column.IsCompared = !set.Contains(column.LogicalName);
    }

    private void SetAllCompared(bool value)
    {
        foreach (var column in ColumnsView.Cast<ColumnChoice>()) column.IsCompared = value;
    }

    private bool FilterColumn(object obj)
    {
        if (obj is not ColumnChoice choice) return false;
        if (string.IsNullOrWhiteSpace(ColumnSearch)) return true;

        return choice.LogicalName.Contains(ColumnSearch, StringComparison.OrdinalIgnoreCase) ||
               choice.DisplayName.Contains(ColumnSearch, StringComparison.OrdinalIgnoreCase);
    }
}
