using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

public enum QueuePlanFilter
{
    All,
    Add,
    Remove,
    InBoth,
    Skip,
    Keep
}

public sealed class QueuePlanRowViewModel
{
    public QueuePlanRowViewModel(QueuePlanRow row) => Row = row;

    public QueuePlanRow Row { get; }
    public QueuePlanStatus Status => Row.Status;
    public string StatusLabel => Status switch
    {
        QueuePlanStatus.Add => "Add",
        QueuePlanStatus.Remove => "Remove",
        QueuePlanStatus.Skip => "Skip",
        QueuePlanStatus.Keep => "Keep",
        _ => "In both"
    };

    public string Name => Row.User.FullName;
    public string? Upn => Row.User.DomainName;
    public string? Email => Row.User.Email;
    public bool? IsDisabled => Row.User.IsDisabled;
    public string? AccessMode => Row.User.AccessMode;
    public string Detail => Row.Detail;
}

/// <summary>
/// Makes a queue's members the same people as a team's: team members missing from the queue are
/// added, queue members not in the team are removed. Everything is previewed first and written
/// only once confirmed.
/// </summary>
public sealed class QueueSyncViewModel : ObservableObject
{
    private readonly DataverseClient _client;
    private CancellationTokenSource? _previewCts;
    private IReadOnlyList<MemberUser> _teamMembers = Array.Empty<MemberUser>();
    private IReadOnlyList<MemberUser> _queueMembers = Array.Empty<MemberUser>();

    public QueueSyncViewModel(EnvironmentSessionViewModel session, DataverseClient client)
    {
        Session = session;
        _client = client;

        TeamsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Teams);
        TeamsView.Filter = o => o is TeamInfo t && EntraTeamSyncViewModel.Matches(t.SearchText, TeamSearchText);

        QueuesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Queues);
        QueuesView.Filter = o => o is QueueInfo q && EntraTeamSyncViewModel.Matches(q.SearchText, QueueSearchText);

        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = FilterRow;

        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync());
        PreviewCommand = new AsyncRelayCommand(_ => PreviewAsync(), _ => CanPreview);
        ApplyCommand = new AsyncRelayCommand(_ => ConfirmAsync(), _ => HasPlan && CountAdd + CountRemove > 0 && !IsBusy);
    }

    public EnvironmentSessionViewModel Session { get; }
    public string Title => $"Queue membership sync — {Session.Title}";

    public ObservableCollection<TeamInfo> Teams { get; } = new();
    public ListCollectionView TeamsView { get; }
    public ObservableCollection<QueueInfo> Queues { get; } = new();
    public ListCollectionView QueuesView { get; }

    public ObservableCollection<QueuePlanRowViewModel> Rows { get; } = new();
    public ListCollectionView RowsView { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }

    private string _teamSearchText = string.Empty;
    public string TeamSearchText
    {
        get => _teamSearchText;
        set
        {
            if (SetProperty(ref _teamSearchText, value)) TeamsView.Refresh();
        }
    }

    private string _queueSearchText = string.Empty;
    public string QueueSearchText
    {
        get => _queueSearchText;
        set
        {
            if (SetProperty(ref _queueSearchText, value)) QueuesView.Refresh();
        }
    }

    private TeamInfo? _selectedTeam;
    public TeamInfo? SelectedTeam
    {
        get => _selectedTeam;
        set
        {
            if (SetProperty(ref _selectedTeam, value)) SelectionChanged();
        }
    }

    private QueueInfo? _selectedQueue;
    public QueueInfo? SelectedQueue
    {
        get => _selectedQueue;
        set
        {
            if (SetProperty(ref _selectedQueue, value)) SelectionChanged();
        }
    }

    public bool CanPreview => SelectedTeam is not null && SelectedQueue is not null && !IsBusy;

    /// <summary>What the preview on screen was worked out for - so a changed pick cannot apply a stale plan.</summary>
    private (TeamInfo Team, QueueInfo Queue)? _planFor;

    public bool HasPlan => _planFor is not null;

    public string PlanHeading => _planFor is { } p ? $"{p.Team.Name}  →  {p.Queue.Name}" : "Pick a team and a queue, then Preview.";

    private QueuePlanFilter _filter;
    public QueuePlanFilter Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value)) RowsView.Refresh();
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) RowsView.Refresh();
        }
    }

    private bool _additiveOnly;
    /// <summary>
    /// Only add: queue members who are not in the team are kept rather than removed. Switching
    /// it re-plans from the membership already read, without asking Dataverse again.
    /// </summary>
    public bool AdditiveOnly
    {
        get => _additiveOnly;
        set
        {
            if (!SetProperty(ref _additiveOnly, value)) return;

            if (Filter is QueuePlanFilter.Remove or QueuePlanFilter.Keep) Filter = QueuePlanFilter.All;
            if (HasPlan) Replan();
        }
    }

    private int _countAll, _countAdd, _countRemove, _countInBoth, _countSkip, _countKeep;
    public int CountAll { get => _countAll; private set => SetProperty(ref _countAll, value); }
    public int CountAdd { get => _countAdd; private set => SetProperty(ref _countAdd, value); }
    public int CountRemove { get => _countRemove; private set => SetProperty(ref _countRemove, value); }
    public int CountInBoth { get => _countInBoth; private set => SetProperty(ref _countInBoth, value); }
    public int CountSkip { get => _countSkip; private set => SetProperty(ref _countSkip, value); }
    public int CountKeep { get => _countKeep; private set => SetProperty(ref _countKeep, value); }

    private string _warnings = string.Empty;
    public string Warnings
    {
        get => _warnings;
        private set
        {
            if (SetProperty(ref _warnings, value)) OnPropertyChanged(nameof(HasWarnings));
        }
    }

    public bool HasWarnings => !string.IsNullOrEmpty(Warnings);

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanPreview));
            PreviewCommand.RaiseCanExecuteChanged();
            ApplyCommand.RaiseCanExecuteChanged();
        }
    }

    private string _status = "Loading teams and queues...";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Loading teams and queues...";

        try
        {
            var teamsTask = _client.GetTeamsAsync(entraGroupTeamsOnly: false);
            var queuesTask = _client.GetQueuesAsync();
            var teams = await teamsTask;
            var queues = await queuesTask;

            var teamId = SelectedTeam?.TeamId;
            var queueId = SelectedQueue?.QueueId;

            Teams.Clear();
            foreach (var team in teams) Teams.Add(team);
            Queues.Clear();
            foreach (var queue in queues) Queues.Add(queue);

            SelectedTeam = Teams.FirstOrDefault(t => t.TeamId == teamId);
            SelectedQueue = Queues.FirstOrDefault(q => q.QueueId == queueId);

            Status = $"{teams.Count:N0} teams and {queues.Count:N0} active queues. Pick one of each, then Preview.";
        }
        catch (Exception ex)
        {
            Status = "Could not load teams and queues - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SelectionChanged()
    {
        _previewCts?.Cancel();
        _planFor = null;
        Rows.Clear();
        Warnings = string.Empty;
        Recount();

        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(PlanHeading));
        PreviewCommand.RaiseCanExecuteChanged();
    }

    private async Task PreviewAsync()
    {
        if (SelectedTeam is not { } team || SelectedQueue is not { } queue) return;

        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();

        IsBusy = true;
        Status = $"Reading the members of {team.Name} and {queue.Name}...";

        try
        {
            var teamTask = _client.GetTeamMembersAsync(team.TeamId, cts.Token);
            var queueTask = _client.GetQueueMembersAsync(queue.QueueId, cts.Token);
            var teamMembers = await teamTask;
            var queueMembers = await queueTask;

            if (cts.IsCancellationRequested) return;

            Rows.Clear();
            _teamMembers = teamMembers;
            _queueMembers = queueMembers;
            _planFor = (team, queue);
            Replan();

            var warnings = new List<string>();
            if (team.IsEntraGroupTeam)
            {
                warnings.Add("This is an Entra group team: Dataverse only lists group members who have signed in since joining, " +
                             "so the team - and this plan - can lag behind the group. Use Entra team sync first if in doubt.");
            }

            if (team.IsDefault)
            {
                warnings.Add("This is a business unit's default team - everyone in the business unit is a member.");
            }

            Warnings = string.Join("  ", warnings);

            Status = $"Previewed at {DateTime.Now:T}: team {teamMembers.Count:N0}, queue {queueMembers.Count:N0} members. " +
                     (CountAdd + CountRemove == 0
                         ? AdditiveOnly ? "Every team member is already in the queue." : "The queue already matches the team."
                         : "Nothing is changed until you apply and confirm.");

            OnPropertyChanged(nameof(HasPlan));
            OnPropertyChanged(nameof(PlanHeading));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = "Could not preview - " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(cts, _previewCts)) IsBusy = false;
        }
    }

    private async Task ConfirmAsync()
    {
        if (_planFor is not { } plan) return;
        var (team, queue) = plan;

        WritePermission permission;
        try
        {
            IsBusy = true;
            Status = "Checking what kind of environment this is...";
            permission = await Session.EvaluateWritePermissionAsync();
        }
        catch (Exception ex)
        {
            Status = "Could not prepare the changes - " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // Fixed at the moment of confirming, so the run cannot change character part-way through.
        var additive = AdditiveOnly;

        var changes = Rows
            .Where(r => r.Status is QueuePlanStatus.Add || (r.Status is QueuePlanStatus.Remove && !additive))
            .Select(r => new MembershipChange(
                r.Status == QueuePlanStatus.Add ? MembershipChangeKind.Add : MembershipChangeKind.Remove,
                r.Row.User.SystemUserId, r.Name, r.Upn, r.Detail, r.Row.IncludedByDefault))
            .ToList();

        var viewModel = new MembershipApplyViewModel(new MembershipApplyRequest
        {
            Operation = AdditiveOnly ? "Add team members to queue" : "Sync queue members from team",
            TargetKind = "queue",
            TargetName = queue.Name,
            SourceKind = "team",
            SourceName = team.Name,
            EnvironmentName = Session.Title,
            EnvironmentHost = Session.EnvironmentHost,
            Account = Session.AccountName,
            Permission = permission,
            Changes = changes,
            ApplyEach = (change, ct) => change.Kind == MembershipChangeKind.Add
                ? _client.AddQueueMemberAsync(queue.QueueId, change.SystemUserId, ct)
                : additive
                    ? throw new InvalidOperationException("This sync is additive only; nobody is removed.")
                    : _client.RemoveQueueMemberAsync(queue.QueueId, change.SystemUserId, ct),
            ReadMemberIds = async ct => (await _client.GetQueueMembersAsync(queue.QueueId, ct)).Select(u => u.SystemUserId).ToHashSet(),
            Note = string.Join("  ", new[]
            {
                AdditiveOnly && CountKeep > 0
                    ? $"Additive only: {CountKeep:N0} queue member(s) not in the team are kept - nobody is removed."
                    : null,
                Warnings.Length > 0 ? Warnings : null
            }.Where(n => n is not null)) is { Length: > 0 } note ? note : null
        });

        var window = new Views.MembershipApplyWindow { DataContext = viewModel, Owner = OwnerWindow() };
        window.ShowDialog();
        var changed = viewModel.AnyWritesAttempted;

        if (!changed)
        {
            Status = permission.Allowed ? "Nothing was changed." : permission.Reason;
            return;
        }

        // Show what is true now, not the plan that has just been carried out.
        var outcome = viewModel.Status;
        await PreviewAsync();
        Status = outcome + " The preview has been read again.";
    }

    private void Replan()
    {
        Rows.Clear();
        foreach (var row in MembershipPlanner.PlanQueueSync(_teamMembers, _queueMembers, AdditiveOnly))
        {
            Rows.Add(new QueuePlanRowViewModel(row));
        }

        Recount();
    }

    private void Recount()
    {
        CountAll = Rows.Count;
        CountAdd = Rows.Count(r => r.Status == QueuePlanStatus.Add);
        CountRemove = Rows.Count(r => r.Status == QueuePlanStatus.Remove);
        CountInBoth = Rows.Count(r => r.Status == QueuePlanStatus.InBoth);
        CountSkip = Rows.Count(r => r.Status == QueuePlanStatus.Skip);
        CountKeep = Rows.Count(r => r.Status == QueuePlanStatus.Keep);
        RowsView.Refresh();
        ApplyCommand.RaiseCanExecuteChanged();
    }

    private bool FilterRow(object o)
    {
        if (o is not QueuePlanRowViewModel row) return false;

        var statusOk = Filter switch
        {
            QueuePlanFilter.Add => row.Status == QueuePlanStatus.Add,
            QueuePlanFilter.Remove => row.Status == QueuePlanStatus.Remove,
            QueuePlanFilter.InBoth => row.Status == QueuePlanStatus.InBoth,
            QueuePlanFilter.Skip => row.Status == QueuePlanStatus.Skip,
            QueuePlanFilter.Keep => row.Status == QueuePlanStatus.Keep,
            _ => true
        };

        return statusOk && EntraTeamSyncViewModel.Matches($"{row.Name} {row.Upn} {row.Email}", SearchText);
    }

    private Window? OwnerWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => ReferenceEquals(w.DataContext, this))
        ?? Application.Current?.MainWindow;
}
