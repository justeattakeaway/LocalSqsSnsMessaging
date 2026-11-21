// Translated from behavioral/dynamodb/check_untag_resource_of_fresh_keys_restores_the_table_tags_listed_before.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.untag-resource-of-fresh-keys-restores-the-table-tags-listed-before</summary>
[Category("AwsEvaluationSuite")]
public sealed class UntagResourceOfFreshKeysRestoresTheTableTagsListedBefore
{
    private const string CheckId = "untag-resource-of-fresh-keys-restores-the-table-tags-listed-before";
    private const int PageCap = 200; // a walk whose token never ends fails instead of spinning

    /// <summary>cap_s, shortened when the runner stops the check sooner, keeping 5 s for the failure.</summary>
    private static double WaitCap(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>A cleanup deleting the table, backing off while a create or tag update holds it; a failure raises.</summary>
    private static Func<Task> DropTable(IAmazonDynamoDB ddb, string name)
    {
        async Task<string> Status()
        {
            try
            {
                return (await ddb.DescribeTableAsync(name)).Table.TableStatus.Value;
            }
            catch (ResourceNotFoundException)
            {
                return "GONE";
            }
        }

        return async () =>
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                // A table just created can still read not-found, so GONE still sends the delete.
                if (await Status() == "DELETING")
                {
                    return;
                }

                try
                {
                    await ddb.DeleteTableAsync(name);
                    return;
                }
                catch (ResourceNotFoundException)
                {
                    return;
                }
                catch (ResourceInUseException) when (attempt != 7)
                {
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Min(3 * Math.Pow(2, attempt), 60)));
            }
        };
    }

    /// <summary>CreateTable, its delete registered at once, then DescribeTable polled to ACTIVE.</summary>
    private static async Task<TableDescription> NewTable(FidelityContext ctx, IAmazonDynamoDB ddb, CreateTableRequest request)
    {
        await ddb.CreateTableAsync(request);
        ctx.OnCleanup(DropTable(ddb, request.TableName));
        var clock = FidelityTime.StartNew();
        var deadline = WaitCap(ctx, 240.0);
        TableDescription table;
        while (true)
        {
            try
            {
                table = (await ddb.DescribeTableAsync(request.TableName)).Table;
            }
            catch (ResourceNotFoundException) when (clock.Elapsed.TotalSeconds < deadline)
            {
                // DescribeTable can read not-found for a moment after CreateTable.
                table = new TableDescription { TableStatus = TableStatus.CREATING };
            }

            if (table.TableStatus == TableStatus.ACTIVE || clock.Elapsed.TotalSeconds >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(table.TableStatus == TableStatus.ACTIVE, $"table {request.TableName} settled at {Py.Repr(table.TableStatus?.Value)}");
        return table;
    }

    /// <summary>call(), repeated while DynamoDB answers LimitExceededException for tagging on the account.</summary>
    private static async Task RateLimited(FidelityContext ctx, Func<Task> call)
    {
        var clock = FidelityTime.StartNew();
        var deadline = WaitCap(ctx, 60.0);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await call();
                return;
            }
            catch (LimitExceededException) when (attempt != 9 && clock.Elapsed.TotalSeconds < deadline)
            {
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 16)));
        }
    }

    /// <summary>The table's tags over every ListTagsOfResource page.</summary>
    private static async Task<Dictionary<string, string>> TagMap(IAmazonDynamoDB ddb, string arn)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var request = new ListTagsOfResourceRequest { ResourceArn = arn };
        for (var i = 0; i < PageCap; i++)
        {
            var page = await ddb.ListTagsOfResourceAsync(request);
            foreach (var tag in page.Tags ?? [])
            {
                found[tag.Key] = tag.Value;
            }

            if (string.IsNullOrEmpty(page.NextToken))
            {
                return found;
            }

            request.NextToken = page.NextToken;
        }

        throw Fail($"ListTagsOfResource still returned a NextToken after {PageCap} pages");
    }

    /// <summary>The tag map read until it equals want or the wait runs out; tag writes settle after the call.</summary>
    private static async Task<Dictionary<string, string>> TagsUntil(FidelityContext ctx, IAmazonDynamoDB ddb, string arn, Dictionary<string, string> want)
    {
        var clock = FidelityTime.StartNew();
        var deadline = WaitCap(ctx, 60.0);
        while (true)
        {
            var got = await TagMap(ddb, arn);
            if (Py.Equal(got, want) || clock.Elapsed.TotalSeconds >= deadline)
            {
                return got;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static List<Tag> Pairs(Dictionary<string, string> mapping) => [.. mapping.Select(p => new Tag { Key = p.Key, Value = p.Value })];

    /// <summary>
    /// TagResource of two keys the table did not carry, then UntagResource of those keys, settles
    /// ListTagsOfResource on exactly the tags it listed before, creation tags included.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.untag-resource-of-fresh-keys-restores-the-table-tags-listed-before")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var created = new Dictionary<string, string> { ["sim2real"] = CheckId, ["owner"] = "lab" };
        var table = await NewTable(ctx, ddb, new CreateTableRequest
        {
            TableName = FidelityContext.UniqueName("sim2real-dtuf-"),
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = Pairs(created),
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
        });
        var arn = table.TableArn;

        var before = await TagsUntil(ctx, ddb, arn, created);
        Ensure(Py.Equal(before, created), $"a table created with tags {Py.Repr(created)} lists {Py.Repr(before)}");

        var fresh = new Dictionary<string, string> { ["extra-a"] = "one", ["extra-b"] = "two" };
        List<string> freshKeys = [.. fresh.Keys.Order(StringComparer.Ordinal)];
        await RateLimited(ctx, () => ddb.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = Pairs(fresh) }));
        var tagged = new Dictionary<string, string>(before);
        foreach (var (key, value) in fresh)
        {
            tagged[key] = value;
        }

        var got = await TagsUntil(ctx, ddb, arn, tagged);
        Ensure(Py.Equal(got, tagged), $"after tagging {Py.Repr(freshKeys)} the table lists {Py.Repr(got)}, expected {Py.Repr(tagged)}");

        await RateLimited(ctx, () => ddb.UntagResourceAsync(new UntagResourceRequest { ResourceArn = arn, TagKeys = freshKeys }));
        got = await TagsUntil(ctx, ddb, arn, before);
        Ensure(Py.Equal(got, before), $"untagging {Py.Repr(freshKeys)} left {Py.Repr(got)}, expected the earlier set {Py.Repr(before)}");
    }
}
