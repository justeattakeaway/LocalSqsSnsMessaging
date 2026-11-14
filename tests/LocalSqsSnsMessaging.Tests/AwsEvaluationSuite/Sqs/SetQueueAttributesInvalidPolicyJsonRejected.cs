// Translated from behavioral/sqs/check_set_queue_attributes_invalid_policy_json_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.set-queue-attributes-invalid-policy-json-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetQueueAttributesInvalidPolicyJsonRejected
{
    private const string Check = "set-queue-attributes-invalid-policy-json-rejected";

    /// <summary>
    /// SetQueueAttributes rejects a syntactically invalid Policy JSON document instead of
    /// storing it verbatim, while a well-formed policy document still persists.
    /// </summary>
    [Test]
    [DisplayName("sqs.set-queue-attributes-invalid-policy-json-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var accountId = ctx.Account;

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-badpolicy-"),
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];

        const string garbage = "{this is not json";
        var (code, message) = await Error(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["Policy"] = garbage },
        }));
        Ensure(code == "InvalidAttributeValue", $"expected InvalidAttributeValue, got {code}");
        Ensure(message == "Invalid value for the parameter Policy.", $"unexpected message '{message}'");

        // effect absent: a queue with no policy ever set must still report none - the
        // garbage was rejected outright, not stored verbatim or in some transformed shape
        var policyAfter = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["Policy"],
        })).Attributes?.GetValueOrDefault("Policy");
        Ensure(policyAfter is null, $"expected no Policy attribute, got '{policyAfter}'");

        // positive control: a well-formed policy document is accepted and persisted
        var validPolicy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Id"] = $"{queueArn}/SIM2REAL",
            ["Statement"] = new JsonArray
            {
                new JsonObject
                {
                    ["Sid"] = "AllowOwnerSend",
                    ["Effect"] = "Allow",
                    ["Principal"] = new JsonObject { ["AWS"] = accountId },
                    ["Action"] = "SQS:SendMessage",
                    ["Resource"] = queueArn,
                },
            },
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["Policy"] = validPolicy.ToJsonString() },
        });
        var stored = JsonNode.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["Policy"],
        })).Attributes!["Policy"])!;
        Ensure((string?)stored["Statement"]![0]!["Sid"] == "AllowOwnerSend",
            $"expected the persisted policy Sid to be AllowOwnerSend, got {stored.ToJsonString()}");
    }

    /// <summary>Return (Error.Code, Error.Message) for the ClientError fn raises; a success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<SetQueueAttributesResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected a rejection, but the call SUCCEEDED and returned {{'HTTPStatusCode': {(int)result!.HttpStatusCode}}}");
    }
}
