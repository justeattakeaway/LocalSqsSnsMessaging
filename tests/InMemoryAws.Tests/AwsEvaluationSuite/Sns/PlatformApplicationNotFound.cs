// Translated from behavioral/sns/check_platform_application_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.platform-application-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class PlatformApplicationNotFound
{
    /// <summary>GetPlatformApplicationAttributes on a well-formed but nonexistent app ARN returns NotFound.</summary>
    [Test]
    [DisplayName("sns.platform-application-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var appArn = FakePlatformApplicationArn(ctx, "nonexistent-fidelity-app");
        var (code, message) = await Error(() => ev.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest { PlatformApplicationArn = appArn }));
        Ensure(code == "NotFound",
            $"expected NotFound for nonexistent platform application, got {code}");
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

    private static string FakePlatformApplicationArn(FidelityContext ctx, string name, string platform = "GCM") =>
        $"arn:aws:sns:{ctx.Region}:{ctx.Account}:app/{platform}/{name}";
}
