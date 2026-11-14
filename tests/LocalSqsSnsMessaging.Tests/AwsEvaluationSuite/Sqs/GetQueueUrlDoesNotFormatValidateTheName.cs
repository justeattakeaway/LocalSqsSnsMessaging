// Translated from behavioral/sqs/check_get_queue_url_does_not_format_validate_the_name.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.get-queue-url-does-not-format-validate-the-name</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetQueueUrlDoesNotFormatValidateTheName
{
    private const string Gone = "AWS.SimpleQueueService.NonExistentQueue";
    private const string GoneMsg = "The specified queue does not exist.";
    private const string BadFormat = "InvalidParameterValue";
    private const string MsgName = "Can only include alphanumeric characters, hyphens, or underscores. 1 to 80 in length";

    /// <summary>
    /// GetQueueUrl does not format-validate names: a malformed name gets NonExistentQueue.
    ///
    /// CreateQueue refuses the same string with InvalidParameterValue; an existing name resolves.
    /// </summary>
    [Test]
    [DisplayName("sqs.get-queue-url-does-not-format-validate-the-name")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var okName = FidelityContext.UniqueName("sim2real-behav-gquf-");
        var okUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = okName, Tags = new() { ["sim2real"] = "get-queue-url-does-not-format-validate-the-name" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(okUrl));
        var resolved = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = okName })).QueueUrl;
        Ensure(resolved == okUrl, $"positive control: GetQueueUrl('{okName}') returned '{resolved}', expected '{okUrl}'");

        var malformed = FidelityContext.UniqueName("sim2real-behav-gquf-bad!") + "!";
        var errCreate = await Error(() => sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = malformed }));
        Ensure(errCreate.Code == BadFormat,
            $"CreateQueue('{malformed}') (illegal character) gave {errCreate}, " +
            $"expected the positive control '{BadFormat}' — this check needs CreateQueue to " +
            "validate the string it reuses against GetQueueUrl below");
        Ensure(errCreate.Message == MsgName, $"got {errCreate}");

        var errMalformed = await Error(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = malformed }));
        Ensure(errMalformed.Code == Gone,
            $"GetQueueUrl('{malformed}') (illegal character, never created) gave {errMalformed}, " +
            $"expected '{Gone}' — the same code a well-formed absent name gets, not CreateQueue's " +
            $"'{BadFormat}'");
        Ensure(errMalformed.Message == GoneMsg, $"got {errMalformed}");

        var neverCreated = FidelityContext.UniqueName("sim2real-behav-gquf-absent-");
        var errAbsent = await Error(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = neverCreated }));
        Ensure(errAbsent.Code == Gone,
            $"GetQueueUrl('{neverCreated}') (well-formed, never created) gave {errAbsent}, " +
            $"expected '{Gone}'");
        Ensure(errAbsent.Message == GoneMsg, $"got {errAbsent}");
    }

    /// <summary>The botocore Error dict ``fn()`` raises; an unexpected success raises.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }
}
