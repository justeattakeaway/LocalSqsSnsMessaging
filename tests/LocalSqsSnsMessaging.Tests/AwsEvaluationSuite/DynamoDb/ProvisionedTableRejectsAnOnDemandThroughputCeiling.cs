// Translated from behavioral/dynamodb/check_provisioned_table_rejects_an_on_demand_throughput_ceiling.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.provisioned-table-rejects-an-on-demand-throughput-ceiling</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProvisionedTableRejectsAnOnDemandThroughputCeiling
{
    /// <summary>The Error from a call that MUST be refused.</summary>
    private static async Task<AwsError> Error(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call) ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");

    /// <summary>
    /// The on-demand throughput ceilings apply only to an on-demand table, so setting them on a
    /// PROVISIONED table is refused -- a target that accepts both mixes two capacity models.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.provisioned-table-rejects-an-on-demand-throughput-ceiling")]
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
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var err = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = table,
                OnDemandThroughput = new OnDemandThroughput { MaxReadRequestUnits = 100 },
            }),
            "an on-demand ceiling on a provisioned table");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        const string expected = "One or more parameter values were invalid: MaxReadRequestUnits for OnDemandThroughput " +
            "cannot be specified when the table BillingMode is PROVISIONED";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        var t = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
        Ensure(t.ProvisionedThroughput.ReadCapacityUnits == 5, "the refused update must leave provisioned throughput unchanged");
    }
}
