using System.Diagnostics.CodeAnalysis;

namespace LocalSqsSnsMessaging;

/// <summary>An AWS account id: twelve decimal digits.</summary>
internal static class AwsAccountId
{
    /// <summary>
    /// Whether <paramref name="value"/> has the shape of an account id. It is how a policy's
    /// <c>AWS</c> principal names a whole account, and how an access key that is an account id
    /// picks that account.
    /// </summary>
    public static bool IsValid([NotNullWhen(true)] string? value) =>
        value is { Length: 12 } && value.All(c => c is >= '0' and <= '9');
}
