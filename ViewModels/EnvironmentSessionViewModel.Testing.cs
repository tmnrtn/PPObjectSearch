using PPObjectSearch.Dataverse;

namespace PPObjectSearch.ViewModels;

/// <summary>A seam for the tests of windows that work across sessions.</summary>
public sealed partial class EnvironmentSessionViewModel
{
    /// <summary>
    /// Stands in for a completed sign-in: the session reports itself connected through the given
    /// client. Signing in for real needs an interactive account, which tests do not have.
    /// </summary>
    internal void UseConnectedClient(DataverseClient client)
    {
        _client = client;
        _isConnected = true;
    }
}
