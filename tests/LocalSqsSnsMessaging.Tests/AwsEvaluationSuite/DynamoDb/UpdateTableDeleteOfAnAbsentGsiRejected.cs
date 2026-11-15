// Translated from behavioral/dynamodb/check_update_table_delete_of_an_absent_gsi_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-delete-of-an-absent-gsi-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableDeleteOfAnAbsentGsiRejected
{
    private const string Kept = "by-g";
    private const string Absent = "by-nothing";

    /// <summary>
    /// UpdateTable deleting a GSI the table does not have is ResourceNotFoundException and leaves the
    /// existing GSI in place; deleting the existing GSI then removes it.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateTable.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-delete-of-an-absent-gsi-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsidel-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("g", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Kept,
                    KeySchema = [new KeySchemaElement("g", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        var desc = await WaitActive(ddb, ctx, table);
        Ensure(Indexes(desc).SequenceEqual([Kept]), $"the new table lists GSIs {Py.Repr(Indexes(desc))}");

        var (result, error) = await FidelityContext.Capture(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = [new GlobalSecondaryIndexUpdate { Delete = new DeleteGlobalSecondaryIndexAction { IndexName = Absent } }],
        }));
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result?.TableDescription?.TableName)}");
        }

        var (code, msg) = (error.Code, error.Message);
        var want = ("ResourceNotFoundException", $"Requested resource not found: Index {Absent} for table {table}");
        Ensure((code, msg) == want, $"deleting the absent GSI {Py.Repr(Absent)} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        desc = await WaitActive(ddb, ctx, table);
        Ensure(Indexes(desc).SequenceEqual([Kept]), $"after the refused delete the table lists GSIs {Py.Repr(Indexes(desc))}");

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = [new GlobalSecondaryIndexUpdate { Delete = new DeleteGlobalSecondaryIndexAction { IndexName = Kept } }],
        });
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (timeLeft > 0 ? timeLeft : 600.0) - 30.0));
        var clock = FidelityTime.StartNew();
        while (true)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (Indexes(desc).Count == 0 || clock.Elapsed >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(Indexes(desc).Count == 0, $"deleting the existing GSI left {Py.Repr(Indexes(desc))}");
    }

    private static List<string> Indexes(TableDescription desc) =>
        (desc.GlobalSecondaryIndexes ?? []).Select(g => g.IndexName).Order(StringComparer.Ordinal).ToList();

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await undo();
                return;
            }
            catch (AmazonDynamoDBException ex)
            {
                if (notFound.Contains(ex.ErrorCode, StringComparer.Ordinal))
                {
                    return;
                }

                if (ex.ErrorCode != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(300))
                {
                    throw;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0));
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }
}
