// Translated from behavioral/dynamodb/check_capacity_mode_switches_both_ways.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.capacity-mode-switches-both-ways</summary>
[Category("AwsEvaluationSuite")]
public sealed class CapacityModeSwitchesBothWays
{
    /// <summary>
    /// A table switches on-demand to provisioned and back. Provisioned requires explicit throughput;
    /// switching back to on-demand drops it and stamps LastUpdateToPayPerRequestDateTime.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.capacity-mode-switches-both-ways")]
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
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 3, WriteCapacityUnits = 3 },
        });
        var t = await WaitActive(ddb, table);
        Ensure(t.BillingModeSummary is not null, "DescribeTable must report BillingModeSummary after a mode switch, got None");
        Ensure(
            t.BillingModeSummary.BillingMode == BillingMode.PROVISIONED,
            $"expected PROVISIONED, got {Py.Repr(t.BillingModeSummary.BillingMode)}");
        Ensure(
            t.ProvisionedThroughput.ReadCapacityUnits == 3,
            $"expected 3 RCU, got {Py.Repr(t.ProvisionedThroughput.ReadCapacityUnits)}");
        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = table, BillingMode = BillingMode.PAY_PER_REQUEST });
        t = await WaitActive(ddb, table);
        Ensure(t.BillingModeSummary is not null, "DescribeTable must report BillingModeSummary after switching back, got None");
        Ensure(
            t.BillingModeSummary.BillingMode == BillingMode.PAY_PER_REQUEST,
            $"expected PAY_PER_REQUEST, got {Py.Repr(t.BillingModeSummary.BillingMode)}");
        Ensure(
            t.BillingModeSummary.LastUpdateToPayPerRequestDateTime is not null,
            "switching to on-demand must stamp LastUpdateToPayPerRequestDateTime");
        Ensure(
            t.ProvisionedThroughput.ReadCapacityUnits == 0,
            $"an on-demand table reports zero provisioned capacity, got {Py.Repr(t.ProvisionedThroughput.ReadCapacityUnits)}");
    }

    /// <summary>Poll DescribeTable until the table is ACTIVE with no index still mutating.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, string table, double deadlineS = 240)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < deadlineS)
        {
            var t = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
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
