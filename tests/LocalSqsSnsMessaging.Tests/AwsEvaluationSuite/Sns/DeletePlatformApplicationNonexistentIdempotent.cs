// Translated from behavioral/sns/check_delete_platform_application_nonexistent_idempotent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.delete-platform-application-nonexistent-idempotent</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeletePlatformApplicationNonexistentIdempotent
{
    /// <summary>
    /// DeletePlatformApplication is idempotent: repeating it on a just-deleted app and calling it
    /// on a never-created ARN are both no-ops, and neither app is readable afterwards.
    /// </summary>
    [Test]
    [DisplayName("sns.delete-platform-application-nonexistent-idempotent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        // BAIDU accepts an arbitrary API-key/secret-key pair with no live credential validation.

        var appArn = (await sns.CreatePlatformApplicationAsync(new CreatePlatformApplicationRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-platapp-"),
            Platform = "BAIDU",
            Attributes = new()
            {
                ["PlatformPrincipal"] = FidelityContext.UniqueName("baidu-api-key-"),
                ["PlatformCredential"] = FidelityContext.UniqueName("baidu-secret-key-"),
            },
        })).PlatformApplicationArn;
        ctx.OnCleanup(() => sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = appArn }));

        var attrs = (await sns.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest { PlatformApplicationArn = appArn })).Attributes ?? [];
        Ensure(attrs.Count > 0, $"expected a freshly-created platform application to be readable, got {Py.Repr(attrs)}");

        await sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = appArn });

        // Read-after-delete is eventually consistent: Get can still succeed for a few seconds.
        var (code, message) = await PollUntilNotFound(
            () => sns.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest { PlatformApplicationArn = appArn }),
            description: "the deleted app to read back NotFound");
        Ensure(code == "NotFound", $"expected the deleted app to read back NotFound, got {code}");
        Ensure(message == "PlatformApplication does not exist",
            $"unexpected NotFound message reading the deleted app: {Py.Repr(message)}");

        await sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = appArn });

        var ghost = BogusPlatformAppArn(ctx);
        await sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = ghost });

        (code, message) = await Error(() => sns.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest { PlatformApplicationArn = ghost }));
        Ensure(code == "NotFound",
            $"deleting a never-created app should not create it as a side effect, get returned {code}");
        Ensure(message == "PlatformApplication does not exist",
            $"unexpected NotFound message reading a never-created app: {Py.Repr(message)}");
    }

    /// <summary>(Code, Message) of the ClientError fn() raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}")
            : (error.Code, error.Message);
    }

    /// <summary>Poll fn() until it raises NotFound; raise on timeout.</summary>
    private static async Task<(string? Code, string? Message)> PollUntilNotFound<T>(Func<Task<T>> fn, double deadlineS = 30.0, double pollS = 2.0, string description = "")
        where T : class
    {
        var deadline = FidelityTime.StartNew();
        string last;
        while (true)
        {
            var (_, error) = await FidelityContext.Capture(fn);
            if (error is null)
            {
                last = "'success'";
            }
            else
            {
                last = Py.Repr(new object?[] { error.Code, error.Message });
                if (error.Code == "NotFound")
                {
                    return (error.Code, error.Message);
                }
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(deadlineS))
            {
                throw Fail($"timed out after {deadlineS}s waiting for {description}, last saw {last}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(pollS));
        }
    }

    /// <summary>A well-formed but nonexistent PlatformApplicationArn in the caller's account/region.</summary>
    private static string BogusPlatformAppArn(FidelityContext ctx, string prefix = "sim2real-behav-ghost-app-") =>
        $"arn:aws:sns:{ctx.Region}:{ctx.Account}:app/GCM/{prefix}{Guid.NewGuid().ToString("N")[..8]}";
}
