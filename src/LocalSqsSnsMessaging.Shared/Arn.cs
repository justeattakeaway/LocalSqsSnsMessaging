using System.Diagnostics.CodeAnalysis;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The parts of an ARN, <c>arn:partition:service:region:account:resource</c>. The resource is
/// everything after the fifth colon, so it may hold colons of its own (an SNS subscription ARN).
/// </summary>
/// <remarks>
/// This only splits a value that has the shape of an ARN. What a service accepts as one, and the
/// error it gives when it does not, stays with that service's validation (<c>SnsArn</c>,
/// <c>EventBridgeTargetValidation</c>, <c>SqsQueueValidation</c>). Resources are found by ARN
/// through <see cref="InMemoryAwsBus.TryGetQueueByArn"/> and its siblings, which match the whole ARN.
/// </remarks>
internal readonly record struct Arn(string Value, string Partition, string Service, string Region, string Account, string Resource)
{
    public static bool TryParse([NotNullWhen(true)] string? value, out Arn arn)
    {
        var parts = value?.Split([':'], 6);
        if (parts is not { Length: 6 } || !string.Equals(parts[0], "arn", StringComparison.Ordinal))
        {
            arn = default;
            return false;
        }

        arn = new Arn(value!, parts[1], parts[2], parts[3], parts[4], parts[5]);
        return true;
    }

    /// <summary>The region an ARN names, or <see langword="null"/> when the value is not an ARN naming one.</summary>
    public static string? RegionOf(string? value) =>
        TryParse(value, out var arn) && arn.Region.Length > 0 ? arn.Region : null;

    /// <summary>The account an ARN names, or <see langword="null"/> when the value is not an ARN naming one.</summary>
    public static string? AccountOf(string? value) =>
        TryParse(value, out var arn) && arn.Account.Length > 0 ? arn.Account : null;
}
