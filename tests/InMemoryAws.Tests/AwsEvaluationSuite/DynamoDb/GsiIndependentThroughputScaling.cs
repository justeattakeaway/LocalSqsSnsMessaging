// Translated from behavioral/dynamodb/check_gsi_independent_throughput_scaling.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-independent-throughput-scaling</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiIndependentThroughputScaling
{
    /// <summary>
    /// UpdateTable scaling a GSI's read capacity from 5 to 15 leaves the table at 5, and scaling the table to 20
    /// leaves the GSI at 15: the two throughputs change independently.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-independent-throughput-scaling")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("gsi_pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "scaled-gsi",
                    KeySchema = [new KeySchemaElement("gsi_pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
                },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await WaitTableActive(ddb, t, timeoutSeconds: 120);

        var baseline = (await ddb.DescribeTableAsync(t)).Table;
        var baselineGsi = baseline.GlobalSecondaryIndexes.First(g => g.IndexName == "scaled-gsi");
        Ensure(baselineGsi.ProvisionedThroughput.ReadCapacityUnits == 5, Describe(baselineGsi));
        Ensure(baseline.ProvisionedThroughput.ReadCapacityUnits == 5, Describe(baseline));

        // Scale ONLY the GSI's throughput up.
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = t,
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Update = new UpdateGlobalSecondaryIndexAction
                    {
                        IndexName = "scaled-gsi",
                        ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 15, WriteCapacityUnits = 15 },
                    },
                },
            ],
        });
        var afterGsiScale = await WaitTableActive(ddb, t, timeoutSeconds: 180);
        var gsiAfter = afterGsiScale.GlobalSecondaryIndexes.First(g => g.IndexName == "scaled-gsi");
        Ensure(gsiAfter.ProvisionedThroughput.ReadCapacityUnits == 15, Describe(gsiAfter));
        Ensure(afterGsiScale.ProvisionedThroughput.ReadCapacityUnits == 5, "scaling the GSI must not affect the base table's own throughput");

        // Now scale ONLY the table's throughput up; the GSI's must be unaffected.
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = t,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 20, WriteCapacityUnits = 20 },
        });
        var afterTableScale = await WaitTableActive(ddb, t, timeoutSeconds: 180);
        Ensure(afterTableScale.ProvisionedThroughput.ReadCapacityUnits == 20, Describe(afterTableScale));
        var gsiFinal = afterTableScale.GlobalSecondaryIndexes.First(g => g.IndexName == "scaled-gsi");
        Ensure(gsiFinal.ProvisionedThroughput.ReadCapacityUnits == 15, "scaling the base table must not affect the GSI's independently-set throughput");
    }

    private static string Describe(GlobalSecondaryIndexDescription g) =>
        Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["IndexName"] = g.IndexName,
            ["IndexStatus"] = g.IndexStatus?.Value,
            ["ProvisionedThroughput"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ReadCapacityUnits"] = g.ProvisionedThroughput?.ReadCapacityUnits,
                ["WriteCapacityUnits"] = g.ProvisionedThroughput?.WriteCapacityUnits,
            },
        });

    private static string Describe(TableDescription t) =>
        Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["TableName"] = t.TableName,
            ["TableStatus"] = t.TableStatus?.Value,
            ["ProvisionedThroughput"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ReadCapacityUnits"] = t.ProvisionedThroughput?.ReadCapacityUnits,
                ["WriteCapacityUnits"] = t.ProvisionedThroughput?.WriteCapacityUnits,
            },
        });

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds = 60, double intervalSeconds = 1.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{description} not met within {timeoutSeconds}s. Last result: {Py.Repr(last)}");
    }

    private static Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string name, double timeoutSeconds = 90) =>
        PollUntil(
            async () => (await ddb.DescribeTableAsync(name)).Table,
            t => t.TableStatus == TableStatus.ACTIVE && (t.GlobalSecondaryIndexes ?? []).All(idx => (idx.IndexStatus?.Value ?? "ACTIVE") == "ACTIVE"),
            timeoutSeconds: timeoutSeconds,
            intervalSeconds: 1.0,
            description: $"table {name} ACTIVE");
}
