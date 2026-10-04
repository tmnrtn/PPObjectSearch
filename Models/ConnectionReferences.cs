namespace PPObjectSearch.Models;

/// <summary>A connection reference row: which connector it is for, and which connection it is bound to.</summary>
public sealed class ConnectionReferenceInfo
{
    public required Guid Id { get; init; }
    public required string LogicalName { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>e.g. "/providers/Microsoft.PowerApps/apis/shared_office365".</summary>
    public string? ConnectorId { get; init; }

    /// <summary>The bound connection's name (an id); empty when nothing is bound.</summary>
    public string? ConnectionId { get; init; }

    public bool HasConnection => !string.IsNullOrWhiteSpace(ConnectionId);

    /// <summary>The connector's short name - "shared_office365" - from the end of its id.</summary>
    public string Connector => ConnectorId is { Length: > 0 } id ? id[(id.LastIndexOf('/') + 1)..] : string.Empty;

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? LogicalName : DisplayName!;
}

/// <summary>A connection as Power Automate reports it.</summary>
public sealed class ConnectionInfo
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? ConnectorId { get; init; }

    /// <summary>"Connected", "Error", ... - the first status Power Automate lists.</summary>
    public string? Status { get; init; }

    public string? StatusMessage { get; init; }
    public string? Owner { get; init; }

    public bool IsConnected => string.Equals(Status, "Connected", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One line of the Connections tab: a reference, what it is bound to, and whether that works.</summary>
public sealed class ConnectionReferenceRow
{
    public required ConnectionReferenceInfo Reference { get; init; }
    public ConnectionInfo? Connection { get; init; }

    /// <summary>False when the connection list could not be read, so its absence means nothing.</summary>
    public bool ConnectionsKnown { get; init; }

    public string Label => Reference.Label;
    public string LogicalName => Reference.LogicalName;
    public string Connector => Reference.Connector;
    public string? ConnectionId => Reference.ConnectionId;
    public string? ConnectionName => Connection?.DisplayName;
    public string? Owner => Connection?.Owner;

    public ConnectionHealth Health =>
        !Reference.HasConnection ? ConnectionHealth.NoConnection
        : Connection is { IsConnected: true } ? ConnectionHealth.Connected
        : Connection is not null ? ConnectionHealth.Error
        : ConnectionsKnown ? ConnectionHealth.NotVisible
        : ConnectionHealth.Unknown;

    public string HealthLabel => Health switch
    {
        ConnectionHealth.NoConnection => "No connection",
        ConnectionHealth.Connected => "Connected",
        ConnectionHealth.Error => string.IsNullOrWhiteSpace(Connection?.StatusMessage)
            ? $"{Connection?.Status ?? "Error"}"
            : $"{Connection?.Status ?? "Error"} - {Connection!.StatusMessage}",
        ConnectionHealth.NotVisible => "Bound to a connection not shared with you",
        _ => "Bound - status not read"
    };

    public bool IsBroken => Health is ConnectionHealth.NoConnection or ConnectionHealth.Error;
}

public enum ConnectionHealth
{
    Unknown,
    Connected,
    NotVisible,
    Error,
    NoConnection
}
