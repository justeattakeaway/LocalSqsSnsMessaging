// Translated from behavioral/dynamodb/check_update_table_billing_mode_provisioned_same_value_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-billing-mode-provisioned-same-value-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableBillingModeProvisionedSameValueRejected
{
    private const string Check = "update-table-billing-mode-provisioned-same-value-rejected";

    private static readonly (string Code, string Message) SameValueNoOp = (
        "ValidationException",
        "The provisioned throughput for the table will not change. The requested value equals the " +
        "current value. Current ReadCapacityUnits provisioned for the table: 1. Requested " +
        "ReadCapacityUnits: 1. Current WriteCapacityUnits provisioned for the table: 1. Requested " +
        "WriteCapacityUnits: 1. Refer to the Amazon DynamoDB Developer Guide for current limits and " +
        "how to request higher limits.");

    /// <summary>
    /// UpdateTable rejects a PROVISIONED table's same-value throughput as a no-op, whether or not
    /// BillingMode=PROVISIONED is also given, and a genuine capacity change still applies afterward.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-billing-mode-provisioned-same-value-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-provnoop-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 },
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, table);

        await ExpectError(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = table,
                BillingMode = BillingMode.PROVISIONED,
                ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 },
            }),
            SameValueNoOp.Code,
            SameValueNoOp.Message);
        await ExpectError(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = table,
                ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 },
            }),
            SameValueNoOp.Code,
            SameValueNoOp.Message);

        var desc = (await ddb.DescribeTableAsync(table)).Table;
        var status = desc.TableStatus;
        Ensure(status == TableStatus.ACTIVE, $"expected the rejected no-op to leave the table ACTIVE, got {Py.Repr(status)}");
        var cap = desc.ProvisionedThroughput;
        var gotCap = (cap.ReadCapacityUnits, cap.WriteCapacityUnits);
        Ensure(gotCap == (1, 1), $"expected throughput to stay (1, 1) after the rejections, got {Py.Repr(gotCap)}");

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 2, WriteCapacityUnits = 1 },
        });
        var after = await WaitCapacity(ddb, table, (2, 1));
        var afterCap = after.ProvisionedThroughput;
        var gotAfter = (afterCap.ReadCapacityUnits, afterCap.WriteCapacityUnits);
        Ensure(gotAfter == (2, 1), $"expected a genuine capacity change to (2, 1) to apply, got {Py.Repr(gotAfter)}");
    }

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            desc = (await ddb.DescribeTableAsync(tableName)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never settled to ACTIVE in {timeoutS:0.0}s (last={Py.Repr(desc?.TableStatus)})");
    }

    /// <summary>Poll until TableStatus is ACTIVE and ProvisionedThroughput equals <paramref name="target"/>.</summary>
    private static async Task<TableDescription> WaitCapacity(IAmazonDynamoDB ddb, string tableName, (long Read, long Write) target, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            desc = (await ddb.DescribeTableAsync(tableName)).Table;
            var cap = desc.ProvisionedThroughput;
            var got = (cap.ReadCapacityUnits, cap.WriteCapacityUnits);
            if (desc.TableStatus == TableStatus.ACTIVE && got == target)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never reached {Py.Repr(target)} in {timeoutS:0.0}s (last={Py.Repr(desc?.TableStatus)})");
    }

    /// <summary>Assert <paramref name="fn"/> raises exactly (code, message); an unexpected success raises too.</summary>
    private static async Task ExpectError(Func<Task<UpdateTableResponse>> fn, string code, string message)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == (code, message), $"expected {Py.Repr((code, message))}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result?.TableDescription?.TableStatus)}");
    }
}
