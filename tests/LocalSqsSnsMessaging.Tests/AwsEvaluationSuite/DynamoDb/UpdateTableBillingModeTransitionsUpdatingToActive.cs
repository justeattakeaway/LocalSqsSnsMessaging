// Translated from behavioral/dynamodb/check_update_table_billing_mode_transitions_updating_to_active.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-billing-mode-transitions-updating-to-active</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableBillingModeTransitionsUpdatingToActive
{
    private const string Check = "update-table-billing-mode-transitions-updating-to-active";

    /// <summary>
    /// UpdateTable starts a billing-mode transition that settles to ACTIVE with the new mode applied.
    ///
    /// This covers the UpdateTable state machine without starting an online GSI backfill (which can
    /// pin a table in UPDATING for many minutes and cannot be cancelled).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-billing-mode-transitions-updating-to-active")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-billing-");
        var (keySchema, attrDefs) = HashKeySchema("pk");
        await CreateTableAndWait(ddb, ctx, tableName, keySchema, attrDefs);

        var before = (await ddb.DescribeTableAsync(tableName)).Table;
        var beforeMode = before.BillingModeSummary.BillingMode;
        Ensure(
            beforeMode == BillingMode.PAY_PER_REQUEST,
            $"expected freshly-created table billing mode PAY_PER_REQUEST, got {Py.Repr(beforeMode)}");

        var resp = await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = tableName,
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 3, WriteCapacityUnits = 4 },
        });
        var startStatus = resp.TableDescription.TableStatus;
        Ensure(startStatus == TableStatus.UPDATING, $"expected UPDATING, got {startStatus}");

        await WaitTableActive(ddb, tableName, timeoutS: 600);

        var after = (await ddb.DescribeTableAsync(tableName)).Table;
        var afterMode = after.BillingModeSummary.BillingMode;
        Ensure(
            afterMode == BillingMode.PROVISIONED,
            $"expected settled billing mode PROVISIONED, got {Py.Repr(afterMode)}");
        var readCap = after.ProvisionedThroughput.ReadCapacityUnits;
        Ensure(readCap == 3, $"expected ReadCapacityUnits 3 to persist, got {readCap}");
        var writeCap = after.ProvisionedThroughput.WriteCapacityUnits;
        Ensure(writeCap == 4, $"expected WriteCapacityUnits 4 to persist, got {writeCap}");

        // The data plane must remain fully usable after the billing-mode transition.
        await ddb.PutItemAsync(tableName, Item(("pk", S("k1")), ("v", S("post-update"))));
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("k1"))), ConsistentRead = true })).Item;
        var gotV = got["v"].S;
        Ensure(
            gotV == "post-update",
            $"expected post-transition GetItem to read back 'post-update', got {Py.Repr(gotV)}");
    }

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE (a transient 404 counts as not-yet).</summary>
    private static async Task<string> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 60.0, double intervalS = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            try
            {
                status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus.Value;
            }
            catch (ResourceNotFoundException)
            {
                status = "PENDING";
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutS}s (last={status})");
    }

    /// <summary>
    /// Delete a table; a table already gone is fine.
    ///
    /// A table that is CREATING/UPDATING rejects DeleteTable with ResourceInUseException,
    /// so that code is retried until it settles or timeout_s passes; anything else raises.
    /// </summary>
    private static async Task ForceDeleteTable(IAmazonDynamoDB ddb, string tableName, double timeoutS = 300.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    /// <summary>Create a table, register teardown, and wait until ACTIVE.</summary>
    private static async Task<CreateTableResponse> CreateTableAndWait(
        IAmazonDynamoDB ddb, FidelityContext ctx, string tableName, List<KeySchemaElement> keySchema, List<AttributeDefinition> attributeDefinitions)
    {
        var resp = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = keySchema,
            AttributeDefinitions = attributeDefinitions,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ForceDeleteTable(ddb, tableName));
        await WaitTableActive(ddb, tableName, timeoutS: 60.0);
        return resp;
    }

    /// <summary>Return (KeySchema, AttributeDefinitions) for a hash-only table.</summary>
    private static (List<KeySchemaElement>, List<AttributeDefinition>) HashKeySchema(string keyName = "pk") =>
        ([new KeySchemaElement(keyName, KeyType.HASH)], [new AttributeDefinition(keyName, ScalarAttributeType.S)]);
}
