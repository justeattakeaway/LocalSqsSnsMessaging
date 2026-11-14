// Translated from behavioral/sqs/check_queue_policy_attribute_normalizes_principal_and_clears.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-policy-attribute-normalizes-principal-and-clears</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueuePolicyAttributeNormalizesPrincipalAndClears
{
    /// <summary>
    /// A stored queue policy reads back with a bare account-id principal normalized to the root ARN;
    /// setting Policy to "" removes the attribute, and a later AddPermission builds a fresh policy
    /// holding only its own statement.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-policy-attribute-normalizes-principal-and-clears")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-qpol-"),
            Tags = new() { ["sim2real"] = "queue-policy-attribute-normalizes-principal-and-clears" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];
        var root = $"arn:aws:iam::{ctx.Account}:root";

        var written = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "sim2real-send",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["AWS"] = ctx.Account },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["Policy"] = written.ToJsonString() },
        });
        var stored = await Policy(sqs, url);
        Ensure(stored is not null, "the written Policy did not read back");
        Ensure(Str(stored["Version"]) == "2012-10-17", $"Version read back as {stored["Version"]?.ToJsonString()}");
        var stmts = stored["Statement"]?.AsArray() ?? [];
        Ensure(stmts.Count == 1, $"the stored policy has {stmts.Count} statements, expected 1");
        var st = stmts[0]!.AsObject();
        Ensure((Str(st["Sid"]), Str(st["Effect"]), Str(st["Action"])) == ("sim2real-send", "Allow", "sqs:SendMessage"),
            $"the statement read back as Sid={st["Sid"]?.ToJsonString()} Effect={st["Effect"]?.ToJsonString()} "
            + $"Action={st["Action"]?.ToJsonString()}");
        Ensure(Str(st["Resource"]) == arn, "the statement's Resource is not the queue ARN");
        var principal = st["Principal"]?["AWS"];
        Ensure(Str(principal) == root,
            "a bare account-id principal must read back as the account root ARN; got a "
            + $"{(Str(principal) == ctx.Account ? "bare account id" : "different value")} "
            + $"of length {PyStr(principal).Length}");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["Policy"] = "" },
        });
        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["Policy"],
        })).Attributes ?? [];
        Ensure(!attrs.ContainsKey("Policy"),
            $"after setting Policy to '' the attribute still reads '{attrs.GetValueOrDefault("Policy")}'");

        var label = FidelityContext.UniqueName("sim2real-perm");
        await sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = url,
            Label = label,
            AWSAccountIds = [ctx.Account],
            Actions = ["ReceiveMessage"],
        });
        var rebuilt = await Policy(sqs, url);
        Ensure(rebuilt is not null, "AddPermission on a cleared queue produced no Policy");
        var sids = (rebuilt["Statement"]?.AsArray() ?? []).Select(s => Str(s?["Sid"])).ToList();
        Ensure(sids.SequenceEqual([label]),
            $"after clear + AddPermission the policy holds {sids.Count} statements; "
            + $"cleared statement {(sids.Contains("sim2real-send") ? "resurrected" : "absent")}, "
            + $"new label {(sids.Contains(label) ? "present" : "absent")}");
        var action = rebuilt["Statement"]![0]!["Action"];
        Ensure(Str(action) == "SQS:ReceiveMessage",
            $"AddPermission's statement carries Action {action?.ToJsonString()}, expected 'SQS:ReceiveMessage'");
    }

    private static async Task<JsonObject?> Policy(IAmazonSQS sqs, string url)
    {
        var raw = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["Policy"],
        })).Attributes?.GetValueOrDefault("Policy");
        return !string.IsNullOrEmpty(raw) ? JsonNode.Parse(raw)!.AsObject() : null;
    }

    /// <summary>The node's value when it is a JSON string, else null (Python compares a non-str unequal).</summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Python's <c>str(value)</c> of a decoded JSON node.</summary>
    private static string PyStr(JsonNode? node) => node is null ? "None" : Str(node) ?? node.ToJsonString();
}
