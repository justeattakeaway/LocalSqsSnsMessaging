// Translated from behavioral/dynamodb/check_update_table_billing_mode_pay_per_request_no_op_accepted.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-billing-mode-pay-per-request-no-op-accepted</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableBillingModePayPerRequestNoOpAccepted
{
    private const string Check = "update-table-billing-mode-pay-per-request-no-op-accepted";

    /// <summary>
    /// UpdateTable accepts BillingMode=PAY_PER_REQUEST on an already on-demand table as a no-op,
    /// settles to ACTIVE, and the table keeps serving reads and writes afterward.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-billing-mode-pay-per-request-no-op-accepted")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-pprnoop-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, table);

        var before = (await ddb.DescribeTableAsync(table)).Table;
        var beforeMode = before.BillingModeSummary.BillingMode;
        Ensure(
            beforeMode == BillingMode.PAY_PER_REQUEST,
            $"expected freshly-created table billing mode PAY_PER_REQUEST, got {Py.Repr(beforeMode)}");

        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = table, BillingMode = BillingMode.PAY_PER_REQUEST });

        var after = await WaitActive(ddb, table);
        var afterMode = after.BillingModeSummary.BillingMode;
        Ensure(
            afterMode == BillingMode.PAY_PER_REQUEST,
            $"expected billing mode to stay PAY_PER_REQUEST after the no-op update, got {Py.Repr(afterMode)}");

        await ddb.PutItemAsync(table, Item(("pk", S("k1")), ("v", S("post-noop"))));
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("k1"))), ConsistentRead = true })).Item;
        var gotV = got["v"].S;
        Ensure(gotV == "post-noop", $"expected post-no-op GetItem to read back 'post-noop', got {Py.Repr(gotV)}");
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
}
