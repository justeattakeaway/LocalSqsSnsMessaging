// Translated from behavioral/sqs/check_create_queue_name_conflict_different_attributes_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.create-queue-name-conflict-different-attributes-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateQueueNameConflictDifferentAttributesRejected
{
    private const string Check = "create-queue-name-conflict-different-attributes-rejected";

    /// <summary>
    /// CreateQueue on an existing queue name with DIFFERENT attribute values is rejected
    /// with QueueNameExists and does not mutate the existing queue's attributes; re-issuing
    /// CreateQueue with the SAME name and IDENTICAL attributes succeeds idempotently.
    /// </summary>
    [Test]
    [DisplayName("sqs.create-queue-name-conflict-different-attributes-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-sqs-nameconflict-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["VisibilityTimeout"] = "30" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var (code, message) = await Error(
            () => sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = new() { ["VisibilityTimeout"] = "60" } }));
        // empirically, real AWS returns the wire error code "QueueAlreadyExists" for this
        // condition (the Smithy/API-doc shape name "QueueNameExists" is not the wire code).
        Ensure(code == "QueueAlreadyExists", $"expected QueueAlreadyExists, got {code}");
        const string expectedMessage =
            "A queue already exists with the same name and a different value for attribute " +
            "VisibilityTimeout";
        Ensure(message == expectedMessage, $"expected message {expectedMessage}, got {message}");

        // the conflicting call must not have mutated the existing queue's attributes
        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes!;
        Ensure(attrs["VisibilityTimeout"] == "30",
            $"VisibilityTimeout changed to {attrs["VisibilityTimeout"]} by a rejected CreateQueue call");

        // positive control: identical attributes -> idempotent success, same URL returned
        var idempotent = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = new() { ["VisibilityTimeout"] = "30" } });
        Ensure(idempotent.QueueUrl == url,
            $"idempotent CreateQueue returned {idempotent.QueueUrl}, expected {url}");

        // independent terminal read, through a different operation than the one that mutated
        var lookedUp = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })).QueueUrl;
        Ensure(lookedUp == url, $"GetQueueUrl returned {lookedUp}, expected {url}");
    }

    /// <summary>
    /// Call fn(); return the ClientError's (Code, Message). An unexpected SUCCESS raises, because an
    /// emulator ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Repr(result)}");
    }

    private static string Repr(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object));
        }
        catch (Exception)
        {
            return value?.ToString() ?? "None";
        }
    }
}
