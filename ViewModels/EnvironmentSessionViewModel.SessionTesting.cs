using PPObjectSearch.Auth;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>For tests: a tab that signs in and reaches Dataverse without MSAL or the network.</summary>
public sealed partial class EnvironmentSessionViewModel
{
    private readonly Func<EnvironmentAuthContext>? _newAuthContext;
    private readonly Func<EnvironmentAuthContext, string, DataverseClient>? _newClient;

    /// <summary>Every sign-in and Dataverse client the tab makes comes from the given factories.</summary>
    internal EnvironmentSessionViewModel(AuthenticationService auth, AppSettings settings, TabState? state,
        Func<EnvironmentAuthContext> newAuthContext, Func<EnvironmentAuthContext, string, DataverseClient> newClient)
        : this(auth, settings, state)
    {
        _newAuthContext = newAuthContext;
        _newClient = newClient;
        _authContext = newAuthContext();
    }
}
