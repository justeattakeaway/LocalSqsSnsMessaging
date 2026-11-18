// Translated from behavioral/dynamodb/check_update_table_sets_and_clears_the_on_demand_read_ceiling.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-sets-and-clears-the-on-demand-read-ceiling</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableSetsAndClearsTheOnDemandReadCeiling
{
    /// <summary>
    /// MaxReadRequestUnits caps an on-demand table's read throughput, and -1 REMOVES the cap rather
    /// than setting it to minus one. A target storing the sentinel literally reports -1 back.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-sets-and-clears-the-on-demand-read-ceiling")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => DeleteTable(ddb, table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            OnDemandThroughput = new OnDemandThroughput { MaxReadRequestUnits = 100, MaxWriteRequestUnits = 200 },
        });
        var t = await WaitActive(ddb, table);
        Ensure(
            t.OnDemandThroughput?.MaxReadRequestUnits == 100,
            $"expected a read ceiling of 100, got {Describe(t.OnDemandThroughput)}");
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            OnDemandThroughput = new OnDemandThroughput { MaxReadRequestUnits = -1 },
        });
        t = await WaitActive(ddb, table);
        var got = t.OnDemandThroughput?.MaxReadRequestUnits;
        Ensure(got != 100, "the ceiling must change when it is removed");
        Ensure(got is -1 or null, $"-1 removes the ceiling rather than becoming the value; got {(got is null ? "None" : got.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
    }

    private static string Describe(OnDemandThroughput? throughput) =>
        throughput is null
            ? "None"
            : $"{{'MaxReadRequestUnits': {throughput.MaxReadRequestUnits}, 'MaxWriteRequestUnits': {throughput.MaxWriteRequestUnits}}}";

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

    /// <summary>
    /// Teardown: DeleteTable is refused while the last throughput update is still settling;
    /// retry to a deadline.
    /// </summary>
    private static async Task DeleteTable(IAmazonDynamoDB ddb, string table, double timeoutS = 300)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
