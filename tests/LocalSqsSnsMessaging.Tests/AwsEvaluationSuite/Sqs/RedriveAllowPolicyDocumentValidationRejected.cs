// Translated from behavioral/sqs/check_redrive_allow_policy_document_validation_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.redrive-allow-policy-document-validation-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RedriveAllowPolicyDocumentValidationRejected
{
    private const string Prefix = "Amazon SQS can't create the redrive allow policy.";

    /// <summary>
    /// SetQueueAttributes refuses a RedriveAllowPolicy whose byQueue lists 11 or no source ARNs,
    /// whose allowAll carries source ARNs, or whose redrivePermission is unknown, and each
    /// refusal leaves the stored 10-ARN byQueue policy exactly as accepted.
    /// </summary>
    [Test]
    [DisplayName("sqs.redrive-allow-policy-document-validation-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var baseName = FidelityContext.UniqueName("sim2real-behav-rap-");
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = baseName + "-dlq",
            Tags = new() { ["sim2real"] = "redrive-allow-policy-document-validation-rejected" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];
        var arnRoot = dlqArn[..dlqArn.LastIndexOf(':')];
        var sources = Enumerable.Range(0, 11).Select(i => $"{arnRoot}:{baseName}-s{i}").ToList();

        JsonArray Arns(IEnumerable<string> arns) => new(arns.Select(a => (JsonNode)JsonValue.Create(a)).ToArray());

        var good = new JsonObject { ["redrivePermission"] = "byQueue", ["sourceQueueArns"] = Arns(sources.Take(10)) };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            Attributes = new() { ["RedriveAllowPolicy"] = PyDumps(good) },
        });
        var stored = await Rap(sqs, dlqUrl);
        Ensure(JsonNode.DeepEquals(stored, good),
            "positive control: the 10-ARN byQueue policy read back as permission "
            + $"{stored?["redrivePermission"]?.ToJsonString() ?? "None"} with "
            + $"{(stored?["sourceQueueArns"] as JsonArray)?.Count ?? 0} ARNs");

        var cases = new (JsonObject Doc, string Reason)[]
        {
            (new JsonObject { ["redrivePermission"] = "byQueue", ["sourceQueueArns"] = Arns(sources) },
                "The maximum number of source queue ARNs you can specify is 10."),
            (new JsonObject { ["redrivePermission"] = "byQueue" },
                "When you specify the byQueue permission type, you must also specify one or more "
                + "sourceQueueArns values."),
            (new JsonObject { ["redrivePermission"] = "allowAll", ["sourceQueueArns"] = Arns(sources.Take(1)) },
                "To specify one or more source queue ARNs, you must set redrivePermission to byQueue."),
        };
        foreach (var (doc, reason) in cases)
        {
            var text = PyDumps(doc);
            var (code, msg) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = dlqUrl,
                Attributes = new() { ["RedriveAllowPolicy"] = text },
            }));
            var label = $"{doc["redrivePermission"]!.GetValue<string>()} with {(doc["sourceQueueArns"] as JsonArray)?.Count ?? 0} ARNs";
            Ensure(code == "InvalidParameterValue", $"{label} gave code '{code}'");
            Ensure(msg == $"Value {text} for parameter RedriveAllowPolicy is invalid. Reason: "
                          + $"{Prefix} {reason}", $"{label} gave message '{msg}'");
            var after = await Rap(sqs, dlqUrl);
            Ensure(JsonNode.DeepEquals(after, good),
                $"the refused {label} policy changed the stored one: now permission "
                + $"{after?["redrivePermission"]?.ToJsonString() ?? "None"} with "
                + $"{(after?["sourceQueueArns"] as JsonArray)?.Count ?? 0} ARNs");
        }

        var (code2, msg2) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            Attributes = new() { ["RedriveAllowPolicy"] = PyDumps(new JsonObject { ["redrivePermission"] = "sometimes" }) },
        }));
        Ensure(code2 == "InvalidAttributeValue", $"an unknown redrivePermission gave code '{code2}'");
        Ensure(msg2 == "Invalid value for the parameter RedriveAllowPolicy. Reason: Amazon SQS can't "
                       + "create the redrive allow policy, as it’s in an unsupported format.",
            $"an unknown redrivePermission gave message '{msg2}'");
        Ensure(JsonNode.DeepEquals(await Rap(sqs, dlqUrl), good), "the refused unknown-permission policy changed the stored one");
    }

    /// <summary>(Code, Message) of the ClientError <paramref name="fn"/> raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<JsonNode?> Rap(IAmazonSQS sqs, string url)
    {
        var raw = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["RedriveAllowPolicy"],
        })).Attributes?.GetValueOrDefault("RedriveAllowPolicy");
        return !string.IsNullOrEmpty(raw) ? JsonNode.Parse(raw) : null;
    }

    /// <summary>
    /// Python's <c>json.dumps</c> with its default separators (<c>", "</c> and <c>": "</c>), so the
    /// document sent, and echoed back in the refusal message, is byte-identical to the check's.
    /// </summary>
    private static string PyDumps(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{JsonValue.Create(kv.Key).ToJsonString()}: {PyDumps(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyDumps)) + "]",
        null => "null",
        _ => node.ToJsonString(),
    };
}
