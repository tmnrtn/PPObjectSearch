using PPObjectSearch.Ai;
using PPObjectSearch.Core;

namespace PPObjectSearch.ViewModels;

/// <summary>Whether the AI assistant may read this environment: allowed unless turned off here.</summary>
public sealed partial class EnvironmentSessionViewModel : IAiSelection
{
    /// <summary>The object selected in the list, for Ask AI's suggestion.</summary>
    public string? SelectedNameForAi => SelectedItem?.PrimaryLabel;

    public bool IsAiAllowed => (_settings.Ai ?? new AiSettings()).IsEnvironmentAllowed(EnvironmentUrl, EnvironmentSku);

    /// <summary>The environment menu's check item: turns the assistant's reading of this environment on or off.</summary>
    public RelayCommand ToggleAiAllowedCommand => _toggleAiAllowedCommand ??= new RelayCommand(_ => SetAiAllowed(!IsAiAllowed), _ => HasHost);
    private RelayCommand? _toggleAiAllowedCommand;

    private void SetAiAllowed(bool allowed)
    {
        if (AiService.Current is { } ai)
        {
            // Saves, and tells every tab and conversation.
            ai.SetEnvironmentAllowed(EnvironmentUrl, allowed, EnvironmentSku);
        }
        else
        {
            (_settings.Ai ??= new AiSettings()).SetEnvironmentAllowed(EnvironmentUrl, allowed, EnvironmentSku);
            _settings.Save();
        }

        RaiseAiAllowed();
        Status = allowed
            ? $"The AI assistant can read {EnvironmentHost} again."
            : $"The AI assistant won't read {EnvironmentHost}: screens showing it aren't sent.";
    }

    /// <summary>Re-reads whether the assistant may read this environment, e.g. after another tab changed it.</summary>
    public void RaiseAiAllowed()
    {
        OnPropertyChanged(nameof(IsAiAllowed));
        _toggleAiAllowedCommand?.RaiseCanExecuteChanged();
    }
}
