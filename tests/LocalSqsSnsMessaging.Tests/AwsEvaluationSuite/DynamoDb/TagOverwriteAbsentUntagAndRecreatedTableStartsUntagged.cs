// Translated from behavioral/dynamodb/check_tag_overwrite_absent_untag_and_recreated_table_starts_untagged.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.tag-overwrite-absent-untag-and-recreated-table-starts-untagged</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagOverwriteAbsentUntagAndRecreatedTableStartsUntagged
{
    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || error.Code == notFound)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary><c>max(0.0, (ctx.time_left() or 300.0) - 30.0)</c>: seconds until the deadline.</summary>
    private static double DeadlineSeconds(FidelityContext ctx)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        return Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
    }

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var deadline = DeadlineSeconds(ctx);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE
                && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc}");
    }

    /// <summary>fn(), retried every 2 s while tagging is rate limited.</summary>
    private static async Task TagCall(FidelityContext ctx, Func<Task> fn)
    {
        var deadline = DeadlineSeconds(ctx);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(fn);
            if (error is null)
            {
                return;
            }

            if (error.Code != "LimitExceededException" || clock.Elapsed.TotalSeconds >= deadline)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task<Dictionary<string, string>> Tags(IAmazonDynamoDB ddb, string arn)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        string? token = null;
        while (true)
        {
            var request = new ListTagsOfResourceRequest { ResourceArn = arn };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var resp = await ddb.ListTagsOfResourceAsync(request);
            foreach (var t in resp.Tags ?? [])
            {
                output[t.Key] = t.Value;
            }

            token = resp.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return output;
            }
        }
    }

    private static async Task<Dictionary<string, string>> TagsUntil(FidelityContext ctx, IAmazonDynamoDB ddb, string arn, Dictionary<string, string> want)
    {
        var deadline = DeadlineSeconds(ctx);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var got = await Tags(ddb, arn);
            if (Py.Equal(got, want) || clock.Elapsed.TotalSeconds >= deadline)
            {
                return got;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task<string> Create(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        return (await WaitActive(ddb, ctx, table)).TableArn;
    }

    private static Dictionary<string, string> Dict(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    /// <summary>
    /// TagResource on an existing key replaces its value, UntagResource of an absent key succeeds and
    /// changes nothing, and a table re-created under the same name and ARN carries none of the old tags.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.tag-overwrite-absent-untag-and-recreated-table-starts-untagged")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-tags-");
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        var arn = await Create(ddb, ctx, table);

        await TagCall(ctx, () => ddb.TagResourceAsync(new TagResourceRequest
        {
            ResourceArn = arn,
            Tags = [new Tag { Key = "env", Value = "one" }, new Tag { Key = "team", Value = "a" }],
        }));
        var got = await TagsUntil(ctx, ddb, arn, Dict(("env", "one"), ("team", "a")));
        Ensure(Py.Equal(got, Dict(("env", "one"), ("team", "a"))), $"after the first TagResource the table carries {Py.Repr(got)}");
        await TagCall(ctx, () => ddb.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = [new Tag { Key = "env", Value = "two" }] }));
        got = await TagsUntil(ctx, ddb, arn, Dict(("env", "two"), ("team", "a")));
        Ensure(Py.Equal(got, Dict(("env", "two"), ("team", "a"))), $"re-tagging 'env' left the table carrying {Py.Repr(got)}");

        await TagCall(ctx, () => ddb.UntagResourceAsync(new UntagResourceRequest { ResourceArn = arn, TagKeys = ["absent"] }));
        got = await TagsUntil(ctx, ddb, arn, Dict(("env", "two"), ("team", "a")));
        Ensure(Py.Equal(got, Dict(("env", "two"), ("team", "a"))), $"untagging an absent key left the table carrying {Py.Repr(got)}");
        await TagCall(ctx, () => ddb.UntagResourceAsync(new UntagResourceRequest { ResourceArn = arn, TagKeys = ["team"] }));
        got = await TagsUntil(ctx, ddb, arn, Dict(("env", "two")));
        Ensure(Py.Equal(got, Dict(("env", "two"))), $"untagging an absent key and 'team' left the table carrying {Py.Repr(got)}");

        await ddb.DeleteTableAsync(table);
        var deadline = DeadlineSeconds(ctx);
        var clock = FidelityTime.StartNew();
        var gone = false;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(table));
            if (error is not null)
            {
                if (error.Code != "ResourceNotFoundException")
                {
                    throw error.Exception;
                }

                gone = true;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        if (!gone)
        {
            throw Fail($"table {table} still describes after DeleteTable");
        }

        var again = await Create(ddb, ctx, table);
        Ensure(again == arn, $"the re-created table has ARN {Py.Repr(again)}, expected {Py.Repr(arn)}");
        await TagCall(ctx, () => ddb.TagResourceAsync(new TagResourceRequest { ResourceArn = again, Tags = [new Tag { Key = "fresh", Value = "yes" }] }));
        got = await TagsUntil(ctx, ddb, again, Dict(("fresh", "yes")));
        Ensure(Py.Equal(got, Dict(("fresh", "yes"))), $"the re-created table carries {Py.Repr(got)}; the old 'env' tag must not return");
    }
}
