// Translated from behavioral/dynamodb/check_provisioned_table_rejects_a_switch_with_no_throughput.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.provisioned-table-rejects-a-switch-with-no-throughput</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProvisionedTableRejectsASwitchWithNoThroughput
{
    /// <summary>The Error from a call that MUST be refused.</summary>
    private static async Task<AwsError> Error(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call) ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");

    /// <summary>
    /// Switching to PROVISIONED without naming ReadCapacityUnits and WriteCapacityUnits is refused:
    /// there is no default to fall back on.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.provisioned-table-rejects-a-switch-with-no-throughput")]
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
        var err = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest { TableName = table, BillingMode = BillingMode.PROVISIONED }),
            "a switch to PROVISIONED with no throughput");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        const string expected = "One or more parameter values were invalid: ProvisionedThroughput must be specified when " +
            "BillingMode is PROVISIONED";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        var t = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
        Ensure(t.BillingModeSummary.BillingMode == BillingMode.PAY_PER_REQUEST, "the refused switch must leave the table on-demand");
    }
}
