// Translated from behavioral/events/check_put_remove_permission_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-remove-permission-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutRemovePermissionRoundtrip
{
    private const string Check = "put-remove-permission-roundtrip";

    /// <summary>PutPermission/RemovePermission round-trip is reflected in the bus policy.</summary>
    [Test]
    [DisplayName("events.put-remove-permission-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real-check", Value = Check }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        const string grantedPrincipal = "111122223333";
        await ev.PutPermissionAsync(new PutPermissionRequest
        {
            EventBusName = bus,
            Action = "events:PutEvents",
            Principal = grantedPrincipal,
            StatementId = "stmt1",
        });
        var desc = await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus });
        var policy = JsonNode.Parse(string.IsNullOrEmpty(desc.Policy) ? "{}" : desc.Policy)!.AsObject();
        var statements = policy["Statement"]?.AsArray() ?? [];
        var stmt = statements.FirstOrDefault(s => (string?)s?["Sid"] == "stmt1");
        Ensure(stmt is not null, $"expected statement 'stmt1' in policy, got: {policy.ToJsonString()}");
        Ensure(
            stmt["Action"] is JsonValue action && action.TryGetValue<string>(out var a) && a == "events:PutEvents",
            $"expected granted statement's Action to be 'events:PutEvents', got: {stmt.ToJsonString()}");
        Ensure(
            (stmt["Principal"]?.ToJsonString() ?? "null").Contains(grantedPrincipal, StringComparison.Ordinal),
            $"expected granted statement's Principal to reference account " +
            $"{grantedPrincipal}, got: {stmt.ToJsonString()}");

        await ev.RemovePermissionAsync(new RemovePermissionRequest { EventBusName = bus, StatementId = "stmt1" });
        var desc2 = await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus });
        var policy2 = JsonNode.Parse(string.IsNullOrEmpty(desc2.Policy) ? "{}" : desc2.Policy)!.AsObject();
        var statements2 = policy2["Statement"]?.AsArray() ?? [];
        Ensure(
            !statements2.Any(s => (string?)s?["Sid"] == "stmt1"),
            $"expected statement 'stmt1' removed from policy, got: {policy2.ToJsonString()}");
    }
}
