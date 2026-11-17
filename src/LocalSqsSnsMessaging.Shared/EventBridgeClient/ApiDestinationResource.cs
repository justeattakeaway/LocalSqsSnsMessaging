namespace LocalSqsSnsMessaging;

/// <summary>
/// An EventBridge API destination: an HTTP endpoint, the method to call it with, the connection
/// whose authorization it sends and how many invocations a second it allows.
/// </summary>
/// <remarks>
/// Its settings change as a whole (<see cref="Settings"/>). Its state is not stored: it is
/// <c>ACTIVE</c> while its connection is <c>AUTHORIZED</c>, <c>INACTIVE</c> while the connection is
/// in any other state, and stays <c>ACTIVE</c> when the connection is deleted from under it, as
/// EventBridge does not reference-count connections.
/// </remarks>
internal sealed class ApiDestinationResource
{
    public const string Active = "ACTIVE";
    public const string Inactive = "INACTIVE";

    /// <summary>EventBridge's limit when CreateApiDestination names none, and the most it allows.</summary>
    public const int MaxInvocationRateLimitPerSecond = 300;

    private readonly object _rateGate = new();
    private DateTimeOffset _nextInvocation = DateTimeOffset.MinValue;

    public required string Name { get; init; }
    public required string Arn { get; init; }
    public required DateTimeOffset CreationTime { get; init; }

    public object Gate { get; } = new();

    /// <summary>The destination's current settings; replaced whole (under <see cref="Gate"/>) by UpdateApiDestination.</summary>
    public required ApiDestinationSettings Settings { get; set; }

    /// <summary>
    /// Takes the next invocation slot the rate limit allows, and says how long from
    /// <paramref name="now"/> it is: zero while the destination is under its limit.
    /// </summary>
    public TimeSpan TakeInvocationSlot(DateTimeOffset now)
    {
        var interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / Math.Max(1, Settings.InvocationRateLimitPerSecond));
        lock (_rateGate)
        {
            var slot = _nextInvocation > now ? _nextInvocation : now;
            _nextInvocation = slot + interval;
            return slot - now;
        }
    }
}

/// <summary>What UpdateApiDestination can change on an API destination.</summary>
internal sealed record ApiDestinationSettings(
    string? Description,
    string ConnectionArn,
    string InvocationEndpoint,
    string HttpMethod,
    int InvocationRateLimitPerSecond,
    DateTimeOffset LastModifiedTime);
