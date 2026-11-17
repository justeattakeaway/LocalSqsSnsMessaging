// Translated from behavioral/events/check_create_connection_missing_auth_parameters_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-connection-missing-auth-parameters-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateConnectionMissingAuthParametersRejected
{
    /// <summary>
    /// OVERLY-PERMISSIVE: CreateConnection with AuthorizationType=API_KEY but no ApiKeyAuthParameters must be rejected as a validation error, not accepted with empty credentials.
    /// A positive control proves the account accepts a well-formed API_KEY connection at all, so
    /// the rejection below cannot be "every CreateConnection fails" wearing a validation-shaped mask.
    /// </summary>
    [Test]
    [DisplayName("events.create-connection-missing-auth-parameters-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var controlName = FidelityContext.UniqueName("sim2real-behav-conn-ok-");
        var connName = controlName;
        await ev.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = connName,
            AuthorizationType = ConnectionAuthorizationType.API_KEY,
            AuthParameters = new CreateConnectionAuthRequestParameters
            {
                ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "x-api-key", ApiKeyValue = "secret-value" },
            },
        });
        ctx.OnCleanup(() => CleanupConnection(ev, controlName));
        var controlDesc = await PollConnectionSettled(ev, controlName);
        Ensure(
            controlDesc.ConnectionState?.Value == "AUTHORIZED",
            $"positive control: expected ConnectionState AUTHORIZED, got '{controlDesc.ConnectionState?.Value}'");

        var name = FidelityContext.UniqueName("sim2real-behav-conn-");
        var err = await Error(() => ev.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = name,
            AuthorizationType = ConnectionAuthorizationType.API_KEY,
            AuthParameters = new CreateConnectionAuthRequestParameters(),
        }));
        (string?, string?) expected = (
            "ValidationException",
            "Failed to create the connection(s). Parameter AuthParameters is not valid. Reason: " +
            "The value for AuthParameters must include exactly one authorization type.");
        Ensure(err == expected, $"expected {expected}, got {err}");

        var describeErr = await Error(() => ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name }));
        (string?, string?) expectedAbsent = (
            "ResourceNotFoundException",
            $"Failed to describe the connection(s). Connection '{name}' does not exist.");
        Ensure(
            describeErr == expectedAbsent,
            $"rejected CreateConnection must not leave a connection behind, got {describeErr}");
    }

    /// <summary>
    /// Call fn(); return its ClientError (code, message). An unexpected SUCCESS raises, because an
    /// emulator ACCEPTING a call real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected this call to fail, but it SUCCEEDED");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Best-effort DeleteConnection (asynchronous; the connection disappears later).</summary>
    private static async Task CleanupConnection(IAmazonEventBridge ev, string name)
    {
        var error = await FidelityContext.CaptureError(() => ev.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name }));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    /// <summary>Poll DescribeConnection until the state is terminal; raise on timeout.</summary>
    private static async Task<DescribeConnectionResponse> PollConnectionSettled(IAmazonEventBridge ev, string name, double timeoutS = 60.0)
    {
        var clock = FidelityTime.StartNew();
        DescribeConnectionResponse? desc = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            desc = await ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name });
            if (desc.ConnectionState?.Value is "AUTHORIZED" or "DEAUTHORIZED" or "CREATE_FAILED")
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        throw Fail($"connection {name} did not settle within {timeoutS}s: {desc?.ConnectionState?.Value}");
    }
}
