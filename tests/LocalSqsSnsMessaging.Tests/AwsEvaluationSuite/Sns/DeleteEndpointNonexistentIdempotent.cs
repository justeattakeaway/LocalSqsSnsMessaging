// Translated from behavioral/sns/check_delete_endpoint_nonexistent_idempotent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.delete-endpoint-nonexistent-idempotent</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteEndpointNonexistentIdempotent
{
    /// <summary>DeleteEndpoint on a nonexistent endpoint is idempotent (documented as idempotent).</summary>
    [Test]
    [DisplayName("sns.delete-endpoint-nonexistent-idempotent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var ghost = BogusEndpointArn(ctx);
        // A no-op: DeleteEndpoint on a missing endpoint returns success (a ClientError fails the check).
        await sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = ghost });

        // Independent read at the destination: the endpoint must still be genuinely
        // absent, not silently materialized by the "successful" delete.
        var (readCode, readMessage) = await Error(() => sns.GetEndpointAttributesAsync(new GetEndpointAttributesRequest { EndpointArn = ghost }));
        Ensure(readCode == "NotFound",
            $"GetEndpointAttributes after a no-op DeleteEndpoint: expected NotFound, got {readCode}");
        Ensure(readMessage == "Endpoint does not exist",
            $"GetEndpointAttributes after a no-op DeleteEndpoint: unexpected message {Py.Repr(readMessage)}");

        // Idempotent means repeatable: the identical delete call must still be a no-op.
        await sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = ghost });
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

    /// <summary>A well-formed but nonexistent endpoint ARN in the caller's account/region.</summary>
    private static string BogusEndpointArn(FidelityContext ctx, string prefix = "sim2real-behav-ghost-ep-") =>
        $"arn:aws:sns:{ctx.Region}:{ctx.Account}:endpoint/GCM/" +
        $"{prefix}{Guid.NewGuid().ToString("N")[..8]}/{Guid.NewGuid()}";
}
