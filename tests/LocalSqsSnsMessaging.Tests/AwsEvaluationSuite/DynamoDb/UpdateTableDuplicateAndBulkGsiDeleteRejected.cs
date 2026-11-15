// Translated from behavioral/dynamodb/check_update_table_duplicate_and_bulk_gsi_delete_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-duplicate-and-bulk-gsi-delete-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableDuplicateAndBulkGsiDeleteRejected
{
    private const string Check = "update-table-duplicate-and-bulk-gsi-delete-rejected";

    /// <summary>Six indexes the table declares at create time, each on its own hash-key attribute.</summary>
    private static readonly string[] IndexNames = ["idx-a", "idx-b", "idx-c", "idx-d", "idx-e", "idx-f"];
    private static readonly string[] HashAttrs = ["ga", "gb", "gc", "gd", "ge", "gf"];

    /// <summary>
    /// UpdateTable naming one index in two Delete actions is refused for carrying more than one
    /// update for that index, and deleting six different indexes in one call hits the one-online-
    /// index-change limit instead; neither rejected call changes the table's indexes.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-duplicate-and-bulk-gsi-delete-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsidup-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                .. HashAttrs.Select(attr => new AttributeDefinition(attr, ScalarAttributeType.S)),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                .. IndexNames.Zip(HashAttrs).Select(p => new GlobalSecondaryIndex
                {
                    IndexName = p.First,
                    KeySchema = [new KeySchemaElement(p.Second, KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                }),
            ],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        var sortedNames = IndexNames.Order(StringComparer.Ordinal).ToList();
        var desc = await WaitSettled(ddb, table);
        Ensure(
            IndexNamesOf(desc).SequenceEqual(sortedNames),
            $"the new table lists GSIs {Py.Repr(IndexNamesOf(desc))}, expected all six");

        // Break exactly one thing: name the SAME existing index in two Delete actions.
        var (code, msg) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = [Delete("idx-a"), Delete("idx-a")],
        }));
        var expectedDup = (
            "ValidationException",
            "One or more parameter values were invalid: Only one global secondary index update " +
            "per index is allowed simultaneously. Index: idx-a");
        Ensure(
            (code, msg) == expectedDup,
            $"two Deletes of the same index idx-a gave {Py.Repr((code, msg))}, expected {Py.Repr(expectedDup)}");
        desc = await WaitSettled(ddb, table);
        Ensure(
            IndexNamesOf(desc).SequenceEqual(sortedNames),
            $"after the refused duplicate-delete the table lists GSIs {Py.Repr(IndexNamesOf(desc))}");

        // Break the other thing: six Deletes, each naming a different existing index.
        (code, msg) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = [.. IndexNames.Select(Delete)],
        }));
        var expectedBulk = (
            "LimitExceededException",
            "Subscriber limit exceeded: Only 1 online index can be created or deleted " +
            "simultaneously per table");
        Ensure(
            (code, msg) == expectedBulk,
            $"six Deletes of six different indexes gave {Py.Repr((code, msg))}, expected {Py.Repr(expectedBulk)}");
        desc = await WaitSettled(ddb, table);
        Ensure(
            IndexNamesOf(desc).SequenceEqual(sortedNames),
            $"after the refused bulk-delete the table lists GSIs {Py.Repr(IndexNamesOf(desc))}");

        // Positive control: deleting ONE of the six in its own call is accepted and takes effect.
        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = table, GlobalSecondaryIndexUpdates = [Delete("idx-a")] });
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (timeLeft > 0 ? timeLeft : 600.0) - 30.0));
        var clock = FidelityTime.StartNew();
        desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (!IndexNamesOf(desc).Contains("idx-a"))
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(
            desc is not null && IndexNamesOf(desc).SequenceEqual(sortedNames.Where(n => n != "idx-a")),
            $"deleting idx-a alone left GSIs {Py.Repr(desc is null ? null : IndexNamesOf(desc))}");
    }

    private static GlobalSecondaryIndexUpdate Delete(string name) =>
        new() { Delete = new DeleteGlobalSecondaryIndexAction { IndexName = name } };

    /// <summary>A cleanup running undo(); only the resource's own not-found codes mean it is gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        try
        {
            await undo();
        }
        catch (AmazonDynamoDBException ex) when (notFound.Contains(ex.ErrorCode, StringComparer.Ordinal))
        {
        }
    };

    /// <summary>The (Code, Message) fn() raises. An unexpected success raises, naming what came back.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<UpdateTableResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result?.TableDescription?.TableName)}");
    }

    /// <summary>Poll DescribeTable until ACTIVE with every index ACTIVE.</summary>
    private static async Task<TableDescription> WaitSettled(IAmazonDynamoDB ddb, string table, int deadlineS = 240)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(deadlineS))
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            var busy = (desc.GlobalSecondaryIndexes ?? []).Where(i => i.IndexStatus != IndexStatus.ACTIVE).ToList();
            if (desc.TableStatus == TableStatus.ACTIVE && busy.Count == 0)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"{table} never settled to ACTIVE within {deadlineS}s: {Py.Repr(desc?.TableStatus)}");
    }

    private static List<string> IndexNamesOf(TableDescription desc) =>
        (desc.GlobalSecondaryIndexes ?? []).Select(i => i.IndexName).Order(StringComparer.Ordinal).ToList();
}
