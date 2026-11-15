// Translated from behavioral/dynamodb/check_on_demand_throughput_round_trips_at_create_then_update_changes_both_ceilings.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.on-demand-throughput-round-trips-at-create-then-update-changes-both-ceilings</summary>
[Category("AwsEvaluationSuite")]
public sealed class OnDemandThroughputRoundTripsAtCreateThenUpdateChangesBothCeilings
{
    private const string CheckId = "on-demand-throughput-round-trips-at-create-then-update-changes-both-ceilings";

    /// <summary>Poll DescribeTable until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, string table, int deadlineSeconds = 240)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(deadlineSeconds))
        {
            var t = (await ddb.DescribeTableAsync(table)).Table;
            if (t.TableStatus == TableStatus.ACTIVE)
            {
                return t;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"{table} never settled to ACTIVE within {deadlineSeconds}s");
    }

    /// <summary>
    /// Teardown: DeleteTable is refused while the last throughput update is still settling;
    /// retry to a deadline.
    /// </summary>
    private static async Task DeleteTable(IAmazonDynamoDB ddb, string table, int timeoutSeconds = 300)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await ddb.DeleteTableAsync(table);
                return;
            }
            catch (ResourceNotFoundException)
            {
                return;
            }
            catch (ResourceInUseException) when (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static Dictionary<string, object?>? Repr(OnDemandThroughput? throughput) => throughput is null
        ? null
        : new Dictionary<string, object?> { ["MaxReadRequestUnits"] = throughput.MaxReadRequestUnits, ["MaxWriteRequestUnits"] = throughput.MaxWriteRequestUnits };

    /// <summary>
    /// OnDemandThroughput given at CreateTable is described back unchanged, and UpdateTable
    /// changing both MaxReadRequestUnits and MaxWriteRequestUnits replaces the pair as described.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.on-demand-throughput-round-trips-at-create-then-update-changes-both-ceilings")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            OnDemandThroughput = new OnDemandThroughput { MaxReadRequestUnits = 10, MaxWriteRequestUnits = 5 },
        });
        ctx.OnCleanup(() => DeleteTable(ddb, table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var t = await WaitActive(ddb, table);
        Ensure(
            Py.Equal(Repr(t.OnDemandThroughput), new Dictionary<string, object?> { ["MaxReadRequestUnits"] = 10L, ["MaxWriteRequestUnits"] = 5L }),
            $"CreateTable's OnDemandThroughput was {{'MaxReadRequestUnits': 10, 'MaxWriteRequestUnits': 5}}, DescribeTable returned {Py.Repr(Repr(t.OnDemandThroughput))}");

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            OnDemandThroughput = new OnDemandThroughput { MaxReadRequestUnits = 20, MaxWriteRequestUnits = 15 },
        });
        t = await WaitActive(ddb, table);
        Ensure(
            Py.Equal(Repr(t.OnDemandThroughput), new Dictionary<string, object?> { ["MaxReadRequestUnits"] = 20L, ["MaxWriteRequestUnits"] = 15L }),
            $"UpdateTable set OnDemandThroughput to {{'MaxReadRequestUnits': 20, 'MaxWriteRequestUnits': 15}}, DescribeTable returned {Py.Repr(Repr(t.OnDemandThroughput))}");
    }
}
