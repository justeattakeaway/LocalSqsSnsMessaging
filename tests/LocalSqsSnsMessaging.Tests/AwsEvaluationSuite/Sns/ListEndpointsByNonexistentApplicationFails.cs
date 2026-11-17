// Translated from behavioral/sns/check_list_endpoints_by_nonexistent_application_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-endpoints-by-nonexistent-application-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListEndpointsByNonexistentApplicationFails
{
    /// <summary>ListEndpointsByPlatformApplication on an unknown application must fail with NotFound.</summary>
    [Test]
    [DisplayName("sns.list-endpoints-by-nonexistent-application-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var ghost = BogusPlatformAppArn(ctx);
        var (code, message) = await Error(() =>
            sns.ListEndpointsByPlatformApplicationAsync(new ListEndpointsByPlatformApplicationRequest { PlatformApplicationArn = ghost }));
        Ensure(code == "NotFound", $"ListEndpointsByPlatformApplication: expected NotFound, got {code}");
        const string expected = "PlatformApplication does not exist";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
    }

    /// <summary>
    /// Call fn(); return (code, message) of the ClientError it raises. An unexpected success
    /// raises, so a target that accepts what AWS refuses never reads as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be refused, but it succeeded and returned {result}")
            : (error.Code, error.Message);
    }

    /// <summary>A well-formed but nonexistent PlatformApplicationArn in the caller's account/region.</summary>
    private static string BogusPlatformAppArn(FidelityContext ctx, string prefix = "sim2real-behav-ghost-app-") =>
        $"arn:aws:sns:{ctx.Region}:{ctx.Account}:app/GCM/{prefix}{Guid.NewGuid().ToString("N")[..8]}";
}
