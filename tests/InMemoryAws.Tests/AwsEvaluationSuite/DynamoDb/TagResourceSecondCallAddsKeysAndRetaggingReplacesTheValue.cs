// Translated from behavioral/dynamodb/check_tag_resource_second_call_adds_keys_and_retagging_replaces_the_value.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.tag-resource-second-call-adds-keys-and-retagging-replaces-the-value</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagResourceSecondCallAddsKeysAndRetaggingReplacesTheValue
{
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
    private static async Task Tag(FidelityContext ctx, IAmazonDynamoDB ddb, string arn, Dictionary<string, string> pairs)
    {
        var clock = FidelityTime.StartNew();
        var deadline = BudgetS(ctx, 60.0);
        while (true)
        {
            try
            {
                await ddb.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = [.. pairs.Select(p => new Tag { Key = p.Key, Value = p.Value })] });
                return;
            }
            catch (LimitExceededException) when (clock.Elapsed.TotalSeconds < deadline)
            {
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
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
    /// A second TagResource adds its key beside the keys already set, and re-tagging a present key
    /// replaces its value; ListTagsOfResource settles on exactly the merged map each time.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Tagging.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.tag-resource-second-call-adds-keys-and-retagging-replaces-the-value")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-trk-");
        var arn = (await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        })).TableDescription.TableArn;
        ctx.OnCleanup(DeleteTable(ddb, name));
        await WaitActive(ctx, ddb, name);

        var first = new Dictionary<string, string> { ["team"] = "blue", ["stage"] = "test" };
        await Tag(ctx, ddb, arn, first);
        var got = await Settled(ctx, ddb, arn, first, "ListTagsOfResource showed the first two tags");
        Ensure(Py.Equal(got, first), $"after tagging team and stage the table lists {Py.Repr(got)}, expected {Py.Repr(first)}");

        await Tag(ctx, ddb, arn, new Dictionary<string, string> { ["cost"] = "lab" });
        var merged = new Dictionary<string, string>(first) { ["cost"] = "lab" };
        got = await Settled(ctx, ddb, arn, merged, "ListTagsOfResource showed the merged tags");
        Ensure(Py.Equal(got, merged), $"a second TagResource left the table listing {Py.Repr(got)}, expected {Py.Repr(merged)}");

        await Tag(ctx, ddb, arn, new Dictionary<string, string> { ["team"] = "green" });
        var replaced = new Dictionary<string, string>(merged) { ["team"] = "green" };
        got = await Settled(ctx, ddb, arn, replaced, "ListTagsOfResource showed the replaced value");
        Ensure(Py.Equal(got, replaced), $"re-tagging team left the table listing {Py.Repr(got)}, expected {Py.Repr(replaced)}");
    }
}
