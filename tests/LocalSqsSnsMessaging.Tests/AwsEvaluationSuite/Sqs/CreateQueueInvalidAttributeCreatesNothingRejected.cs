// Translated from behavioral/sqs/check_create_queue_invalid_attribute_creates_nothing_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.create-queue-invalid-attribute-creates-nothing-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateQueueInvalidAttributeCreatesNothingRejected
{
    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real"] = "create-queue-invalid-attribute-creates-nothing-rejected",
    };

    /// <summary>
    /// CreateQueue with an out-of-range VisibilityTimeout is refused with InvalidAttributeValue and
    /// creates no queue; the same name can then be created at once with valid attributes.
    /// </summary>
    [Test]
    [DisplayName("sqs.create-queue-invalid-attribute-creates-nothing-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.IdempotentCreate, "CreateQueue with VisibilityTimeout=43201 succeeded instead of being refused with InvalidAttributeValue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-cqi-");

        var created = new List<string>();

        ctx.OnCleanup(async () =>
        {
            foreach (var u in created)
            {
                await sqs.DeleteQueueAsync(u);
            }
        });

        async Task<CreateQueueResponse> CreateBad()
        {
            var output = await sqs.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = name,
                Attributes = new() { ["VisibilityTimeout"] = "43201" },
                Tags = new(Tags),
            });
            created.Add(output.QueueUrl);
            return output;
        }

        var (code, msg) = await Err(CreateBad);
        Ensure(code == "InvalidAttributeValue", $"VisibilityTimeout=43201 gave code {code}");
        Ensure(msg == "Invalid value for the parameter VisibilityTimeout.",
            $"VisibilityTimeout=43201 gave message {msg}");

        (code, msg) = await Err(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name }));
        Ensure((code, msg) == ("AWS.SimpleQueueService.NonExistentQueue", "The specified queue does not exist."),
            $"the refused CreateQueue left a resolvable queue behind: GetQueueUrl gave ({code}, {msg})");

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["VisibilityTimeout"] = "43200" },
            Tags = new(Tags),
        })).QueueUrl;
        created.Add(url);
        Ensure((await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })).QueueUrl == url,
            "positive control: the valid CreateQueue's URL does not resolve by name");
        var vt = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] }))
            .Attributes!["VisibilityTimeout"];
        Ensure(vt == "43200", $"positive control: the boundary VisibilityTimeout reads back as {vt}");
    }

    /// <summary>(Code, Message) of the ClientError <paramref name="fn"/> raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Repr(result)}");
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
