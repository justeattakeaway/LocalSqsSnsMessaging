// Translated from behavioral/events/check_put_and_remove_permission_conflicting_parameters_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-and-remove-permission-conflicting-parameters-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutAndRemovePermissionConflictingParametersRejected
{
    private const string Check = "put-and-remove-permission-conflicting-parameters-rejected";
    private const string Principal = "111122223333"; // AWS documentation example account id

    /// <summary>
    /// PutPermission/RemovePermission refuse requests that mix their two forms or name neither,
    /// and grants for actions other than events:PutEvents; each refusal leaves the bus policy
    /// unchanged.
    /// </summary>
    [Test]
    [DisplayName("events.put-and-remove-permission-conflicting-parameters-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        await events.PutPermissionAsync(new PutPermissionRequest { EventBusName = bus, Action = "events:PutEvents", Principal = Principal, StatementId = "sim2real-keep" });
        var baseline = await Policy(events, bus);
        Ensure(
            (baseline["Statement"]?.AsArray() ?? throw new KeyNotFoundException("'Statement'")).Select(s => (string?)s?["Sid"]).SequenceEqual(["sim2real-keep"]),
            $"positive control: the grant must be stored, policy is {baseline.ToJsonString()}");
        const string emptyPolicy = """{"Version": "2012-10-17", "Statement": []}""";

        (string Label, Func<Task> Call, string Expected)[] refusals =
        [
            ("PutPermission with Policy and StatementId",
             () => events.PutPermissionAsync(new PutPermissionRequest { EventBusName = bus, Policy = emptyPolicy, StatementId = "sim2real-x" }),
             "Action, Principal, StatementId and Condition could not be combined with Policy parameter."),
            ("PutPermission with neither form",
             () => events.PutPermissionAsync(new PutPermissionRequest { EventBusName = bus }),
             "Parameter(s) Action must be specified."),
            ("PutPermission for an unsupported action",
             () => events.PutPermissionAsync(new PutPermissionRequest
             {
                 EventBusName = bus,
                 Action = "events:DeleteRule",
                 Principal = Principal,
                 StatementId = "sim2real-bad-action",
             }),
             "Provided value in parameter 'action' is not supported."),
            ("RemovePermission with RemoveAllPermissions and StatementId",
             () => events.RemovePermissionAsync(new RemovePermissionRequest { EventBusName = bus, RemoveAllPermissions = true, StatementId = "sim2real-keep" }),
             "StatementId could not be combined with All parameter."),
            ("RemovePermission with neither form",
             () => events.RemovePermissionAsync(new RemovePermissionRequest { EventBusName = bus }),
             "Parameter(s) StatementId must be specified."),
        ];
        foreach (var (label, call, expected) in refusals)
        {
            var (code, message) = await Error(call);
            Ensure(
                (code, message) == ("ValidationException", expected),
                $"{label} must be refused with ValidationException '{expected}', got '{code}' '{message}'");
            var now = await Policy(events, bus);
            Ensure(JsonNode.DeepEquals(now, baseline), $"{label} was refused but changed the bus policy to {now.ToJsonString()}");
        }
    }

    private static async Task<JsonNode> Policy(AmazonEventBridgeClient events, string bus) =>
        JsonNode.Parse((await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Policy
            ?? throw new KeyNotFoundException("'Policy'"))!;

    /// <summary>(code, message) of a call that must fail; an unexpected success raises, naming the result.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail("expected the call to be refused, but it succeeded");
    }
}
