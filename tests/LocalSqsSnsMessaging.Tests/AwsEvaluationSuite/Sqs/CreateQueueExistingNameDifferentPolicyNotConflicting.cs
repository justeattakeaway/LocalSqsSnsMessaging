// Translated from behavioral/sqs/check_create_queue_existing_name_different_policy_not_conflicting.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.create-queue-existing-name-different-policy-not-conflicting</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateQueueExistingNameDifferentPolicyNotConflicting
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real"] = "create-queue-existing-name-different-policy-not-conflicting" };

    /// <summary>
    /// CreateQueue on an existing name ignores a Policy Sid difference (same URL, Sid not updated)
    /// but refuses a different Action, Resource, Principal or VisibilityTimeout (QueueAlreadyExists).
    /// </summary>
    [Test]
    [DisplayName("sqs.create-queue-existing-name-different-policy-not-conflicting")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.IdempotentCreate, "CreateQueue on an existing name with a different Policy Action succeeds instead of QueueAlreadyExists")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-cqpolicy-");
        JsonObject BaseStatement() => new()
        {
            ["Sid"] = "sim2real-original", ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["AWS"] = $"arn:aws:iam::{ctx.Account}:root" },
            ["Action"] = "sqs:SendMessage", ["Resource"] = "*",
        };
        JsonObject PolicyWith(string? field = null, JsonNode? value = null)
        {
            var statement = BaseStatement();
            if (field is not null)
            {
                statement[field] = value;
            }

            return new JsonObject { ["Version"] = "2012-10-17", ["Statement"] = new JsonArray(statement) };
        }

        var original = PolicyWith();
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["VisibilityTimeout"] = "40", ["Policy"] = original.ToJsonString() },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var storedBefore = await Policy(sqs, url);
        Ensure(storedBefore is not null && (string?)storedBefore["Statement"]![0]!["Sid"] == "sim2real-original",
            $"positive control: the originally-set Policy did not read back intact: {storedBefore?.ToJsonString() ?? "None"}");

        var sidOnly = PolicyWith("Sid", "sim2real-renamed");
        var reissued = await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["VisibilityTimeout"] = "40", ["Policy"] = sidOnly.ToJsonString() },
            Tags = new(Tags),
        });
        Ensure(reissued.QueueUrl == url,
            $"re-create with only the Sid changed returned '{reissued.QueueUrl}', expected the " +
            $"same queue '{url}' rather than a conflict or a new queue");
        var storedAfterSid = await Policy(sqs, url);
        Ensure(storedAfterSid is not null && (string?)storedAfterSid["Statement"]![0]!["Sid"] == "sim2real-original",
            "a Sid-only difference must not be APPLIED either, since it was accepted as identical; " +
            $"read back {storedAfterSid?.ToJsonString() ?? "None"}");

        foreach (var (field, value, label) in new (string, JsonNode, string)[]
        {
            ("Action", "sqs:GetQueueAttributes", "Action"),
            ("Resource", $"arn:aws:sqs:{ctx.Region}:{ctx.Account}:sim2real-other-queue", "Resource"),
            // Refused, so never applied.
            ("Principal", "*", "Principal"),
        })
        {
            var different = PolicyWith(field, value);
            var (code, msg) = await Err(() => sqs.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = name,
                Attributes = new() { ["VisibilityTimeout"] = "40", ["Policy"] = different.ToJsonString() },
                Tags = new(Tags),
            }));
            Ensure(code == "QueueAlreadyExists", $"re-create with a different {label} gave code '{code}', expected QueueAlreadyExists");
            Ensure(msg == "A queue already exists with the same name and a different value for attribute Policy",
                $"re-create with a different {label} gave message '{msg}'");
            var stored = await Policy(sqs, url);
            Ensure(JsonNode.DeepEquals(stored, original), $"the refused {label} conflict changed the stored Policy to {stored?.ToJsonString() ?? "None"}");
        }

        var (code2, msg2) = await Err(() => sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Attributes = new() { ["VisibilityTimeout"] = "41", ["Policy"] = original.ToJsonString() },
        }));
        Ensure(code2 == "QueueAlreadyExists",
            $"positive control: re-create with a different VisibilityTimeout gave code '{code2}', " +
            "expected QueueAlreadyExists — the general conflict mechanism must still be active");
        Ensure(msg2 == "A queue already exists with the same name and a different value for attribute VisibilityTimeout",
            $"positive control gave message '{msg2}'");
        var vt = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes!;
        Ensure(vt["VisibilityTimeout"] == "40", $"the refused VisibilityTimeout conflict changed it to '{vt["VisibilityTimeout"]}'");
    }

    /// <summary>(Code, Message) of the ClientError <c>fn</c> raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    private static async Task<JsonNode?> Policy(AmazonSQSClient sqs, string url)
    {
        var raw = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] }))
            .Attributes?.GetValueOrDefault("Policy");
        return string.IsNullOrEmpty(raw) ? null : JsonNode.Parse(raw);
    }
}
