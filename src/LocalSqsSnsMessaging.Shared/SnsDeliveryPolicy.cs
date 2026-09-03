using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The retry and request settings SNS applies when delivering to an HTTP/S endpoint, resolved
/// from the subscription's <c>DeliveryPolicy</c>, falling back to the topic's, then to AWS defaults.
/// See https://docs.aws.amazon.com/sns/latest/dg/sns-message-delivery-retries.html
/// </summary>
internal sealed class SnsDeliveryPolicy
{
    public const string DefaultContentType = "text/plain; charset=UTF-8";

    public static SnsDeliveryPolicy Default { get; } = new();

    public int NumRetries { get; private set; } = 3;
    public int NumNoDelayRetries { get; private set; }
    public int NumMinDelayRetries { get; private set; }
    public int NumMaxDelayRetries { get; private set; }
    public int MinDelayTargetSeconds { get; private set; } = 20;
    public int MaxDelayTargetSeconds { get; private set; } = 20;
    public string BackoffFunction { get; private set; } = "linear";
    public string ContentType { get; private set; } = DefaultContentType;

    /// <summary>Throws <see cref="InternalInvalidParameterException"/> unless the policy is a JSON object.</summary>
    public static void Validate(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(policy!) is not JsonObject)
            {
                throw new InternalInvalidParameterException("Invalid parameter: DeliveryPolicy: Delivery policy must be a JSON object");
            }
        }
        catch (JsonException)
        {
            throw new InternalInvalidParameterException("Invalid parameter: DeliveryPolicy: Delivery policy is not valid JSON");
        }
    }

    /// <summary>
    /// Resolves the effective policy. Topic-level policies use the AWS topic shape
    /// (<c>{"http": {"defaultHealthyRetryPolicy": ..., "defaultRequestPolicy": ..., "disableSubscriptionOverrides": ...}}</c>)
    /// and set the baseline; the subscription-level policy (<c>{"healthyRetryPolicy": ..., "requestPolicy": ...}</c>)
    /// then overrides it unless the topic disables overrides. With neither, the AWS defaults apply
    /// (3 retries, 20 seconds apart, linear).
    /// </summary>
    public static SnsDeliveryPolicy Resolve(string? subscriptionPolicy, string? topicPolicy)
    {
        var topic = Parse(topicPolicy)?["http"] as JsonObject;
        var subscription = Parse(subscriptionPolicy);
        if (topic is null && subscription is null)
        {
            return Default;
        }

        var policy = new SnsDeliveryPolicy();
        var overridesDisabled = false;
        if (topic is not null)
        {
            policy.ApplyRetryPolicy(topic["defaultHealthyRetryPolicy"] as JsonObject);
            policy.ApplyRequestPolicy(topic["defaultRequestPolicy"] as JsonObject);
            overridesDisabled = topic["disableSubscriptionOverrides"] is JsonValue disable &&
                                (disable.TryGetValue<bool>(out var b) ? b
                                    : disable.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed) && parsed);
        }

        if (subscription is not null && !overridesDisabled)
        {
            policy.ApplyRetryPolicy(subscription["healthyRetryPolicy"] as JsonObject);
            policy.ApplyRequestPolicy(subscription["requestPolicy"] as JsonObject);
        }

        return policy;
    }

    private void ApplyRetryPolicy(JsonObject? retry)
    {
        if (retry is null)
        {
            return;
        }

        NumRetries = Clamp(GetInt(retry, "numRetries") ?? NumRetries, 0, 100);
        NumNoDelayRetries = Math.Max(0, GetInt(retry, "numNoDelayRetries") ?? NumNoDelayRetries);
        NumMinDelayRetries = Math.Max(0, GetInt(retry, "numMinDelayRetries") ?? NumMinDelayRetries);
        NumMaxDelayRetries = Math.Max(0, GetInt(retry, "numMaxDelayRetries") ?? NumMaxDelayRetries);
        MinDelayTargetSeconds = Clamp(GetInt(retry, "minDelayTarget") ?? MinDelayTargetSeconds, 1, 3600);
        MaxDelayTargetSeconds = Clamp(GetInt(retry, "maxDelayTarget") ?? MaxDelayTargetSeconds, MinDelayTargetSeconds, 3600);
        if (retry["backoffFunction"] is JsonValue backoff && backoff.TryGetValue<string>(out var function))
        {
            BackoffFunction = function;
        }
    }

    private void ApplyRequestPolicy(JsonObject? request)
    {
        if (request?["headerContentType"] is JsonValue contentType &&
            contentType.TryGetValue<string>(out var header) &&
            !string.IsNullOrWhiteSpace(header))
        {
            ContentType = header;
        }
    }

    /// <summary>
    /// The delay to wait before each retry, in order. Retries are split into the four AWS phases:
    /// immediate, pre-backoff (min delay), backoff (min to max along the backoff function), and
    /// post-backoff (max delay).
    /// </summary>
    public IReadOnlyList<TimeSpan> GetRetryDelays()
    {
        var delays = new List<TimeSpan>(NumRetries);
        var min = TimeSpan.FromSeconds(MinDelayTargetSeconds);
        var max = TimeSpan.FromSeconds(MaxDelayTargetSeconds);

        var noDelay = Math.Min(NumNoDelayRetries, NumRetries);
        var minDelay = Math.Min(NumMinDelayRetries, NumRetries - noDelay);
        var maxDelay = Math.Min(NumMaxDelayRetries, NumRetries - noDelay - minDelay);
        var backoff = NumRetries - noDelay - minDelay - maxDelay;

        for (var i = 0; i < noDelay; i++)
        {
            delays.Add(TimeSpan.Zero);
        }
        for (var i = 0; i < minDelay; i++)
        {
            delays.Add(min);
        }
        for (var i = 0; i < backoff; i++)
        {
            // Position along the backoff phase, 0 for the first retry and 1 for the last.
            var fraction = backoff == 1 ? 1.0 : (double)i / (backoff - 1);
            delays.Add(min + TimeSpan.FromTicks((long)((max - min).Ticks * Shape(fraction))));
        }
        for (var i = 0; i < maxDelay; i++)
        {
            delays.Add(max);
        }

        return delays;
    }

    // Approximates the AWS backoff curves: linear grows steadily; arithmetic and geometric climb
    // faster; exponential reaches the maximum delay the quickest.
    private double Shape(double fraction)
    {
        if (string.Equals(BackoffFunction, "arithmetic", StringComparison.OrdinalIgnoreCase))
        {
            return 1 - Math.Pow(1 - fraction, 2);
        }
        if (string.Equals(BackoffFunction, "geometric", StringComparison.OrdinalIgnoreCase))
        {
            return 1 - Math.Pow(1 - fraction, 3);
        }
        if (string.Equals(BackoffFunction, "exponential", StringComparison.OrdinalIgnoreCase))
        {
            return 1 - Math.Pow(1 - fraction, 4);
        }
        return fraction;
    }

    private static JsonObject? Parse(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(policy!) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? GetInt(JsonObject obj, string name)
    {
        if (obj[name] is JsonValue value)
        {
            if (value.TryGetValue<int>(out var i))
            {
                return i;
            }
            if (value.TryGetValue<double>(out var d))
            {
                return (int)d;
            }
            if (value.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}
