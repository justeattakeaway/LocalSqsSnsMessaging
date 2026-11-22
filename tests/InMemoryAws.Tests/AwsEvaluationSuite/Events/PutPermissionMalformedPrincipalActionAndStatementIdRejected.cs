// Translated from behavioral/events/check_put_permission_malformed_principal_action_and_statement_id_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-permission-malformed-principal-action-and-statement-id-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutPermissionMalformedPrincipalActionAndStatementIdRejected
{
    private const string Check = "put-permission-malformed-principal-action-and-statement-id-rejected";
    private const string Principal = "123456789012"; // AWS documentation example account id

    // Fields, not literals: the SDK's analyzer refuses a literal that breaks a member's pattern.
    private static readonly string BadPrincipal = "1234567890ab";
    private static readonly string BadAction = "PutEvents";
    private static readonly string BadStatementId = "bad id!";

    /// <summary>
    /// PutPermission refuses a Principal, Action or StatementId that does not match its documented
    /// pattern, leaving the bus policy unchanged, while a correctly formatted grant is stored.
    /// </summary>
    [Test]
    [DisplayName("events.put-permission-malformed-principal-action-and-statement-id-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        await events.PutPermissionAsync(new PutPermissionRequest
        {
            EventBusName = bus, Action = "events:PutEvents", Principal = Principal, StatementId = "sim2real-ok",
        });
        var baseline = await Policy(events, bus);
        var sids = (baseline["Statement"] as JsonArray ?? []).Select(s => s?["Sid"]?.GetValue<string>()).ToList();
        Ensure(
            sids.SequenceEqual(["sim2real-ok"]),
            $"positive control: a correctly formatted grant must be stored, policy is {baseline.ToJsonString()}");

        (string Label, Func<Task<PutPermissionResponse>> Call, string Member, string Value, string Pattern)[] refusals =
        [
            ("Principal with letters instead of 12 digits",
                () => events.PutPermissionAsync(new PutPermissionRequest
                {
                    EventBusName = bus, Action = "events:PutEvents", Principal = BadPrincipal, StatementId = "sim2real-bad-principal",
                }),
                "principal", BadPrincipal, @"(\d{12}|\*)"),
            ("Action missing the events: prefix",
                () => events.PutPermissionAsync(new PutPermissionRequest
                {
                    EventBusName = bus, Action = BadAction, Principal = Principal, StatementId = "sim2real-bad-action",
                }),
                "action", BadAction, "events:[a-zA-Z]+"),
            ("StatementId carrying a space and an exclamation mark",
                () => events.PutPermissionAsync(new PutPermissionRequest
                {
                    EventBusName = bus, Action = "events:PutEvents", Principal = Principal, StatementId = BadStatementId,
                }),
                "statementId", BadStatementId, "[a-zA-Z0-9-_]+"),
        ];
        foreach (var (label, call, member, value, pattern) in refusals)
        {
            var (code, message) = await Error(call);
            var expected = (
                "ValidationException",
                $"1 validation error detected: Value '{value}' at '{member}' failed to satisfy " +
                $"constraint: Member must satisfy regular expression pattern: {pattern}");
            Ensure((code, message) == expected, $"{label} must be refused exactly, got '{code}' '{message}'");
            var now = await Policy(events, bus);
            Ensure(JsonNode.DeepEquals(now, baseline), $"{label} was refused but changed the bus policy to {now.ToJsonString()}");
        }
    }

    private static async Task<JsonObject> Policy(IAmazonEventBridge events, string bus) =>
        JsonNode.Parse((await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Policy)!.AsObject();

    /// <summary>(code, message) of a call that must fail; an unexpected success raises, naming the result.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}
