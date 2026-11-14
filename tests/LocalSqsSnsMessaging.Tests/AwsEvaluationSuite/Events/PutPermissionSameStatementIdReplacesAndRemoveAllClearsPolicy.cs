// Translated from behavioral/events/check_put_permission_same_statement_id_replaces_and_remove_all_clears_policy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-permission-same-statement-id-replaces-and-remove-all-clears-policy</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutPermissionSameStatementIdReplacesAndRemoveAllClearsPolicy
{
    private const string Check = "put-permission-same-statement-id-replaces-and-remove-all-clears-policy";

    // Documentation account ids (AWS reserves 111122223333 / 444455556666 for examples); never ours.
    private const string FirstPrincipal = "111122223333";
    private const string SecondPrincipal = "444455556666";
    private const string OrgId = "o-sim2realexample"; // well-formed but fictitious organization id

    /// <summary>
    /// PutPermission with an existing StatementId replaces that statement; "*" and account
    /// principals and a Condition are stored as IAM policy forms on the bus ARN; RemoveAllPermissions
    /// drops the whole Policy and is idempotent.
    /// </summary>
    [Test]
    [DisplayName("events.put-permission-same-statement-id-replaces-and-remove-all-clears-policy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgePermissions, "PutPermission is not implemented (NotSupportedException)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        await events.PutPermissionAsync(new PutPermissionRequest { EventBusName = bus, Action = "events:PutEvents", Principal = "*", StatementId = "sim2real-any" });
        await events.PutPermissionAsync(new PutPermissionRequest
        {
            EventBusName = bus,
            Action = "events:PutEvents",
            Principal = "*",
            StatementId = "sim2real-org",
            Condition = new Condition { Type = "StringEquals", Key = "aws:PrincipalOrgID", Value = OrgId },
        });
        await events.PutPermissionAsync(new PutPermissionRequest { EventBusName = bus, Action = "events:PutEvents", Principal = FirstPrincipal, StatementId = "sim2real-acct" });
        await events.PutPermissionAsync(new PutPermissionRequest { EventBusName = bus, Action = "events:PutEvents", Principal = SecondPrincipal, StatementId = "sim2real-acct" });

        var statements = await Statements(events, bus);
        var sids = statements.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(
            sids.SequenceEqual(["sim2real-acct", "sim2real-any", "sim2real-org"]),
            $"re-granting an existing StatementId must replace it, not add one; Sids are [{string.Join(", ", sids.Select(s => $"'{s}'"))}]");
        var acct = statements["sim2real-acct"];
        Ensure(
            JsonNode.DeepEquals(acct["Principal"], new JsonObject { ["AWS"] = SecondPrincipal }),
            $"the replaced statement must carry the second principal, got {acct["Principal"]?.ToJsonString()}");
        var anyone = statements["sim2real-any"];
        Ensure(
            JsonNode.DeepEquals(anyone["Principal"], JsonValue.Create("*")),
            $"a wildcard principal is stored as '*', got {anyone["Principal"]?.ToJsonString()}");
        var org = statements["sim2real-org"];
        Ensure(
            JsonNode.DeepEquals(org["Principal"], JsonValue.Create("*"))
            && JsonNode.DeepEquals(org["Condition"], new JsonObject { ["StringEquals"] = new JsonObject { ["aws:PrincipalOrgID"] = OrgId } }),
            $"a Condition parameter is stored as an IAM condition block, got ({org["Principal"]?.ToJsonString()}, {org["Condition"]?.ToJsonString()})");
        Ensure(!anyone.ContainsKey("Condition"), $"an unconditioned grant must carry no Condition, got {anyone["Condition"]?.ToJsonString()}");
        foreach (var (sid, stmt) in statements)
        {
            Ensure(
                JsonNode.DeepEquals(stmt["Effect"], JsonValue.Create("Allow"))
                && JsonNode.DeepEquals(stmt["Action"], JsonValue.Create("events:PutEvents"))
                && JsonNode.DeepEquals(stmt["Resource"], JsonValue.Create(busArn)),
                $"statement '{sid}' must Allow events:PutEvents on the bus ARN, got " +
                $"({stmt["Effect"]?.ToJsonString()}, {stmt["Action"]?.ToJsonString()}, {stmt["Resource"]?.ToJsonString()})");
        }

        await events.RemovePermissionAsync(new RemovePermissionRequest { EventBusName = bus, RemoveAllPermissions = true });
        var after = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus });
        Ensure(after.Policy is null, $"RemoveAllPermissions must drop the policy entirely, got '{after.Policy}'");

        await events.RemovePermissionAsync(new RemovePermissionRequest { EventBusName = bus, RemoveAllPermissions = true });
        Ensure((await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Policy is null, "a second RemoveAllPermissions must stay a no-op");

        await events.PutPermissionAsync(new PutPermissionRequest { EventBusName = bus, Action = "events:PutEvents", Principal = FirstPrincipal, StatementId = "sim2real-again" });
        Ensure(
            (await Statements(events, bus)).Keys.Order(StringComparer.Ordinal).SequenceEqual(["sim2real-again"]),
            "a grant after remove-all must start a fresh policy holding only the new statement");
    }

    private static async Task<Dictionary<string, JsonObject>> Statements(AmazonEventBridgeClient events, string bus)
    {
        var policy = JsonNode.Parse((await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Policy
            ?? throw new KeyNotFoundException("'Policy'"))!;
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var s in (policy["Statement"] ?? throw new KeyNotFoundException("'Statement'")).AsArray())
        {
            result[(string)s!["Sid"]!] = s.AsObject();
        }

        return result;
    }
}
