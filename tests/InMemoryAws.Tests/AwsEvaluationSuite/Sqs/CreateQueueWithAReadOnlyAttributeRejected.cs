// Translated from behavioral/sqs/check_create_queue_with_a_read_only_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.create-queue-with-a-read-only-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateQueueWithAReadOnlyAttributeRejected
{
    private const string Check = "create-queue-with-a-read-only-attribute-rejected";
    private const string Gone = "AWS.SimpleQueueService.NonExistentQueue";

    /// <summary>
    /// CreateQueue naming QueueArn, an attribute GetQueueAttributes returns but no caller sets, is
    /// refused and creates no queue; the same create without it succeeds and AWS mints the ARN.
    /// </summary>
    [Test]
    [DisplayName("sqs.create-queue-with-a-read-only-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-cqro-");
        Dictionary<string, string> Legal() => new() { ["VisibilityTimeout"] = "40" };
        ctx.OnCleanup(DropQueueNamed(sqs, name));
        var ownArn = $"arn:aws:sqs:{ctx.Region}:{ctx.Account}:{name}";

        var withArn = Legal();
        withArn["QueueArn"] = ownArn;
        var err = await Refusal(() => sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Attributes = withArn, Tags = new() { ["sim2real"] = Check },
        }), "InvalidAttributeName");
        Ensure(err.Message == "Unknown Attribute QueueArn.", $"the refusal carried the message '{err.Message}'");

        err = await Refusal(() => sqs.GetQueueUrlAsync(name), Gone);
        Ensure(err.Message == "The specified queue does not exist.", $"the absent queue read '{err.Message}'");

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Attributes = Legal(), Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(DropQueueNamed(sqs, name));
        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = ["QueueArn", "VisibilityTimeout"],
        })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("VisibilityTimeout") == "40",
            $"the accepted create reads {{{string.Join(", ", attrs.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}");
        Ensure(attrs.GetValueOrDefault("QueueArn") == ownArn,
            $"the accepted queue reads a QueueArn of {attrs.GetValueOrDefault("QueueArn", "").Length} characters, not its own ARN");
    }

    /// <summary>
    /// Call <paramref name="fn"/>, which AWS must refuse with <paramref name="code"/>; return the error.
    /// A success fails the check, and a different code fails it naming AWS's own error.
    /// </summary>
    private static async Task<AwsError> Refusal<T>(Func<Task<T>> fn, string code)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected {code}, but the call succeeded: {result}");
        }

        if (error.Code != code)
        {
            throw new FidelityAssertionException($"expected {code}, got {error.Code}", error.Exception);
        }

        return error;
    }

    /// <summary>A cleanup deleting the queue called <paramref name="name"/>, if one exists; it covers a create AWS should refuse.</summary>
    private static Func<Task> DropQueueNamed(AmazonSQSClient sqs, string name) => async () =>
    {
        try
        {
            var url = (await sqs.GetQueueUrlAsync(name)).QueueUrl;
            await sqs.DeleteQueueAsync(url);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == Gone)
        {
        }
    };
}
