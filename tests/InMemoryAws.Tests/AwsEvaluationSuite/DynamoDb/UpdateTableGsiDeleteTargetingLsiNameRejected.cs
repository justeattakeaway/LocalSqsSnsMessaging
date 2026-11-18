// Translated from behavioral/dynamodb/check_update_table_gsi_delete_targeting_lsi_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-gsi-delete-targeting-lsi-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableGsiDeleteTargetingLsiNameRejected
{
    /// <summary>
    /// UpdateTable's GlobalSecondaryIndexUpdates Delete rejects an IndexName that names a
    /// local secondary index instead of a global one, leaving every index untouched, and a
    /// Delete naming the real GSI then succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-gsi-delete-targeting-lsi-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsidel-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("shared_attr", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi-main",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("shared_attr", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi-main",
                    KeySchema = [new KeySchemaElement("shared_attr", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitTableSettled(ddb, table);

        var (code, message) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = [new GlobalSecondaryIndexUpdate { Delete = new DeleteGlobalSecondaryIndexAction { IndexName = "lsi-main" } }],
        }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {code}: {Py.Repr(message)}");
        const string expectedMessage = "Cannot delete an index that is not a GlobalSecondaryIndex: lsi-main";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        // The rejected call must not have touched either index.
        var afterReject = (await ddb.DescribeTableAsync(table)).Table;
        var gsiNames = (afterReject.GlobalSecondaryIndexes ?? []).Select(g => g.IndexName).ToList();
        var lsiNames = (afterReject.LocalSecondaryIndexes ?? []).Select(i => i.IndexName).ToList();
        Ensure(gsiNames.SequenceEqual(["gsi-main"]), $"rejected delete must leave the GSI alone, got {Py.Repr(gsiNames)}");
        Ensure(lsiNames.SequenceEqual(["lsi-main"]), $"rejected delete must leave the LSI alone, got {Py.Repr(lsiNames)}");

        // Positive control: naming the real GSI succeeds.
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = [new GlobalSecondaryIndexUpdate { Delete = new DeleteGlobalSecondaryIndexAction { IndexName = "gsi-main" } }],
        });
        var settled = await WaitTableSettled(ddb, table);
        Ensure(
            settled.GlobalSecondaryIndexes is null || settled.GlobalSecondaryIndexes.Count == 0,
            $"expected gsi-main gone after the real delete, got {Py.Repr(settled.GlobalSecondaryIndexes?.Select(g => g.IndexName).ToList())}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Poll DescribeTable until the table and every index are ACTIVE.</summary>
    private static async Task<TableDescription> WaitTableSettled(IAmazonDynamoDB ddb, string name, double timeoutS = 1500.0, double intervalS = 5.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? table = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            table = (await ddb.DescribeTableAsync(name)).Table;
            var busy = (table.GlobalSecondaryIndexes ?? []).Where(i => i.IndexStatus != IndexStatus.ACTIVE).ToList();
            if (table.TableStatus == TableStatus.ACTIVE && busy.Count == 0)
            {
                return table;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{name} never settled to ACTIVE within {timeoutS}s; last={table?.TableStatus?.Value}");
    }
}
