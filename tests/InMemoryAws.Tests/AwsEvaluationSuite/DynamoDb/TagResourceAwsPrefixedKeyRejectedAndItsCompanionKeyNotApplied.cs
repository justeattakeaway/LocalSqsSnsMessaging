// Translated from behavioral/dynamodb/check_tag_resource_aws_prefixed_key_rejected_and_its_companion_key_not_applied.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.tag-resource-aws-prefixed-key-rejected-and-its-companion-key-not-applied</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagResourceAwsPrefixedKeyRejectedAndItsCompanionKeyNotApplied
{
    private const string ExpectedCode = "ValidationException";
    private const string ReservedKey = "aws:owner";
    private const string ExpectedMessage = $"Tag Key cannot be prefixed with aws:, Key: {ReservedKey}";

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>DescribeTable polled until the table is ACTIVE.</summary>
    private static async Task WaitActive(FidelityContext ctx, IAmazonDynamoDB ddb, string name)
    {
        var clock = FidelityTime.StartNew();
        var deadline = BudgetS(ctx, 240.0);
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(name)).Table.TableStatus?.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= deadline)
            {
                if (BudgetSpent(ctx))
                {
                    throw new TimeoutException($"the run budget ended before table {name} became ACTIVE");
                }

                throw Fail($"table {name} is still {Py.Repr(status)}, not ACTIVE");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>A cleanup deleting the table; a table still being created is retried until it can be deleted.</summary>
    private static Func<Task> DeleteTable(IAmazonDynamoDB ddb, string name) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await ddb.DeleteTableAsync(name);
                return;
            }
            catch (ResourceNotFoundException)
            {
                return;
            }
            catch (ResourceInUseException) when (clock.Elapsed.TotalSeconds < 240.0)
            {
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>TagResource, retried while DynamoDB rate-limits tagging on the account.</summary>
    private static async Task Tag(FidelityContext ctx, IAmazonDynamoDB ddb, string arn, List<Tag> pairs)
    {
        var clock = FidelityTime.StartNew();
        var deadline = BudgetS(ctx, 60.0);
        while (true)
        {
            try
            {
                await ddb.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = pairs });
                return;
            }
            catch (LimitExceededException) when (clock.Elapsed.TotalSeconds < deadline)
            {
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>(code, message) of the error fn() raises; a success raises, naming what came back.</summary>
    private static async Task<(string? Code, string? Message)> Refusal(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail("expected the call to be refused, but it returned None");
    }

    /// <summary>The table's tags across every ListTagsOfResource page.</summary>
    private static async Task<Dictionary<string, string>> Listed(IAmazonDynamoDB ddb, string arn)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        var request = new ListTagsOfResourceRequest { ResourceArn = arn };
        while (true)
        {
            var resp = await ddb.ListTagsOfResourceAsync(request);
            foreach (var tag in resp.Tags ?? [])
            {
                output[tag.Key] = tag.Value;
            }

            if (string.IsNullOrEmpty(resp.NextToken))
            {
                return output;
            }

            request.NextToken = resp.NextToken;
        }
    }

    /// <summary>The tag map polled until it equals want or the wait ends; tagging is eventually consistent.</summary>
    private static async Task<Dictionary<string, string>> Settled(FidelityContext ctx, IAmazonDynamoDB ddb, string arn, Dictionary<string, string> want, string what)
    {
        var clock = FidelityTime.StartNew();
        var deadline = BudgetS(ctx, 60.0);
        while (true)
        {
            var got = await Listed(ddb, arn);
            if (Py.Equal(got, want))
            {
                return got;
            }

            if (clock.Elapsed.TotalSeconds >= deadline)
            {
                if (BudgetSpent(ctx))
                {
                    throw new TimeoutException($"the run budget ended before {what}");
                }

                return got;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// TagResource carrying an aws:-prefixed key beside a valid one is refused and applies neither,
    /// so a later tag lists without them; the valid key sent alone then merges into the tags.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Tagging.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.tag-resource-aws-prefixed-key-rejected-and-its-companion-key-not-applied")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-tpx-");
        var arn = (await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        })).TableDescription.TableArn;
        ctx.OnCleanup(DeleteTable(ddb, name));
        await WaitActive(ctx, ddb, name);

        var held = new Dictionary<string, string> { ["team"] = "blue" };
        await Tag(ctx, ddb, arn, [new Tag { Key = "team", Value = "blue" }]);
        var got = await Settled(ctx, ddb, arn, held, "ListTagsOfResource showed the first tag");
        Ensure(Py.Equal(got, held), $"after tagging team the table lists {Py.Repr(got)}, expected {Py.Repr(held)}");

        List<Tag> mixed = [new Tag { Key = ReservedKey, Value = "lab" }, new Tag { Key = "stage", Value = "test" }];
        var refusal = await Refusal(() => Tag(ctx, ddb, arn, mixed));
        Ensure(
            refusal == (ExpectedCode, ExpectedMessage),
            $"an aws:-prefixed key beside stage gave {Py.Repr(refusal)}, expected {Py.Repr((ExpectedCode, ExpectedMessage))}");

        // The control is ordered after the refusal: once its tag is listed, a stage tag from the
        // refused call would be listed too.
        await Tag(ctx, ddb, arn, [new Tag { Key = "cost", Value = "lab" }]);
        var want = new Dictionary<string, string>(held) { ["cost"] = "lab" };
        got = await Settled(ctx, ddb, arn, want, "ListTagsOfResource showed the control tag");
        Ensure(Py.Equal(got, want), $"after the refused call and the control tag the table lists {Py.Repr(got)}, expected {Py.Repr(want)}");

        await Tag(ctx, ddb, arn, [new Tag { Key = "stage", Value = "test" }]);
        var final = new Dictionary<string, string>(want) { ["stage"] = "test" };
        got = await Settled(ctx, ddb, arn, final, "ListTagsOfResource showed the stage tag sent alone");
        Ensure(Py.Equal(got, final), $"the stage tag sent alone left the table listing {Py.Repr(got)}, expected {Py.Repr(final)}");
    }
}
