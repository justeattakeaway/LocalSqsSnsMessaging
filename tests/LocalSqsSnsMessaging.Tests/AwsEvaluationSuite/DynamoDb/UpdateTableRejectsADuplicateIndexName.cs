// Translated from behavioral/dynamodb/check_update_table_rejects_a_duplicate_index_name.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-rejects-a-duplicate-index-name</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableRejectsADuplicateIndexName
{
    /// <summary>
    /// A new index's name must be unique among the table's existing indexes, so adding a second index
    /// under a name already in use is refused and the original is left untouched.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-rejects-a-duplicate-index-name")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("g1", ScalarAttributeType.S)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "dupname",
                    KeySchema = [new KeySchemaElement("g1", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await WaitActive(ddb, table);
        var err = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = table,
                AttributeDefinitions = [new AttributeDefinition("g2", ScalarAttributeType.S)],
                GlobalSecondaryIndexUpdates =
                [
                    new GlobalSecondaryIndexUpdate
                    {
                        Create = new CreateGlobalSecondaryIndexAction
                        {
                            IndexName = "dupname",
                            KeySchema = [new KeySchemaElement("g2", KeyType.HASH)],
                            Projection = new Projection { ProjectionType = ProjectionType.ALL },
                        },
                    },
                ],
            }),
            "adding a second index named `dupname`");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        const string expected = "Attempting to create an index which already exists";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        var idx = (await ddb.DescribeTableAsync(table)).Table.GlobalSecondaryIndexes ?? [];
        Ensure(
            idx.Select(i => i.IndexName).SequenceEqual(["dupname"]),
            $"the original index must survive, got {Py.Repr(idx.Select(i => i.IndexName).ToList())}");
        Ensure(idx[0].KeySchema[0].AttributeName == "g1", "the refused create must not have rewritten the existing index");
    }

    /// <summary>The Error from a call that MUST be refused.</summary>
    private static async Task<AwsError> Error(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call) ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");

    /// <summary>Poll DescribeTable until the table is ACTIVE with no index still mutating.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, string table, double deadlineS = 240)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(deadlineS))
        {
            var t = (await ddb.DescribeTableAsync(table)).Table;
            var busy = (t.GlobalSecondaryIndexes ?? []).Where(i => i.IndexStatus != IndexStatus.ACTIVE).ToList();
            if (t.TableStatus == TableStatus.ACTIVE && busy.Count == 0)
            {
                return t;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"{table} never settled to ACTIVE within {deadlineS}s");
    }
}
