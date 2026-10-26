using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.PowerAutomate;

/// <summary>
/// Why a cloud flow run failed, step by step. The run record says only that an action failed; the
/// run's steps say which one, and - for a step whose own error is just a code - its outputs say why.
/// </summary>
public static class FailedStepReader
{
    /// <summary>Failed steps whose outputs are read for a reason; past this they keep their code.</summary>
    private const int MaxStepOutputsRead = 3;

    /// <summary>A run's failed steps, with what their outputs say.</summary>
    public static async Task<IReadOnlyList<FlowActionResult>> ReadAsync(
        PowerAutomateClient client, string environmentId, string flowId, string runName, CancellationToken ct)
    {
        var run = await client.GetRunAsync(environmentId, flowId, runName, ct).ConfigureAwait(false);
        return await ExplainAsync(client, run.FailedSteps, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// An HTTP or connector step's error is often only a code - NotFound, BadRequest - with the
    /// reason in the response it got. Reads that from the first few such steps' outputs; a step
    /// whose outputs cannot be read keeps its code.
    /// </summary>
    public static async Task<IReadOnlyList<FlowActionResult>> ExplainAsync(
        PowerAutomateClient client, IReadOnlyList<FlowActionResult> steps, CancellationToken ct)
    {
        var explained = new List<FlowActionResult>(steps.Count);
        var read = 0;

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.ErrorMessage) && step.OutputsLink is { } link && read++ < MaxStepOutputsRead)
            {
                try
                {
                    if (FlowStepError.FromOutputs(await client.GetContentAsync(link, ct).ConfigureAwait(false)) is { } reason)
                    {
                        explained.Add(step with { ErrorMessage = reason });
                        continue;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Info($"Could not read the outputs of failed step {step.Name}: {ex.Message}");
                }
            }

            explained.Add(step);
        }

        return explained;
    }
}
