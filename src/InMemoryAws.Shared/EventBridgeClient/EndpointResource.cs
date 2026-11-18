namespace InMemoryAws;

/// <summary>
/// An EventBridge global endpoint: a name in its home region, the two event buses it fronts (one
/// in the home region, one in the failover region its routing config names) and whether it
/// replicates events to the second.
/// </summary>
/// <remarks>
/// Route 53 is not emulated, so the primary's health check is stored and echoed but never
/// evaluated: the endpoint is always healthy and never fails over. An endpoint is created,
/// updated and deleted at once, so it is always <c>ACTIVE</c>. Its settings change as a whole
/// (<see cref="Settings"/>), so a reader never sees half an update.
/// </remarks>
internal sealed class EndpointResource
{
    public const string Active = "ACTIVE";
    public const string ReplicationEnabled = "ENABLED";

    public required string Name { get; init; }
    public required string Arn { get; init; }

    /// <summary>The endpoint's subdomain, e.g. <c>abcde12345.veo</c>, which PutEvents names as <c>EndpointId</c>.</summary>
    public required string EndpointId { get; init; }
    public required string EndpointUrl { get; init; }

    /// <summary>The region the endpoint was created in: its primary region.</summary>
    public required string HomeRegion { get; init; }
    public required DateTimeOffset CreationTime { get; init; }

    public object Gate { get; } = new();

    /// <summary>The endpoint's current settings; replaced whole (under <see cref="Gate"/>) by UpdateEndpoint.</summary>
    public required EndpointSettings Settings { get; set; }

    /// <summary>A new endpoint id: ten lowercase letters and digits, a dot, then three letters, as EventBridge assigns them.</summary>
    public static string NewEndpointId()
    {
        const string alphanumerics = "abcdefghijklmnopqrstuvwxyz0123456789";
        var random = Guid.NewGuid().ToByteArray();
        var id = new char[14];
        for (var i = 0; i < 10; i++)
        {
            id[i] = alphanumerics[random[i] % alphanumerics.Length];
        }

        id[10] = '.';
        for (var i = 11; i < 14; i++)
        {
            id[i] = alphanumerics[random[i] % 26];
        }

        return new string(id);
    }
}

/// <summary>What UpdateEndpoint can change on a global endpoint.</summary>
internal sealed record EndpointSettings(
    string? Description,
    string HealthCheck,
    string SecondaryRoute,
    string ReplicationState,
    IReadOnlyList<string> EventBusArns,
    string? RoleArn,
    DateTimeOffset LastModifiedTime);
