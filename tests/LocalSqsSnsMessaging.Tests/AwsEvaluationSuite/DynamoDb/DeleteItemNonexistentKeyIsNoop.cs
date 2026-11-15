// Translated from behavioral/dynamodb/check_delete_item_nonexistent_key_is_noop.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-nonexistent-key-is-noop</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemNonexistentKeyIsNoop
{
    /// <summary>Poll DescribeTable until TableStatus is ACTIVE (a transient 404 counts as not-yet).</summary>
    private static async Task<string> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 60.0, double intervalSeconds = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName }));
            if (error is not null)
            {
                if (error.Code != "ResourceNotFoundException")
                {
                    throw error.Exception;
                }

                status = "PENDING";
            }
            else
            {
                status = resp!.Table.TableStatus.Value;
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutSeconds}s (last={status ?? "None"})");
    }

    /// <summary>Create a table, register teardown, and wait until ACTIVE.</summary>
    private static async Task<CreateTableResponse> CreateTableAndWait(
        IAmazonDynamoDB ddb, FidelityContext ctx, string tableName, List<KeySchemaElement> keySchema,
        List<AttributeDefinition> attributeDefinitions, double timeoutSeconds = 60.0)
    {
        var resp = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = keySchema,
            AttributeDefinitions = attributeDefinitions,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = "delete-item-nonexistent-key-is-noop" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitTableActive(ddb, tableName, timeoutSeconds: timeoutSeconds);
        return resp;
    }

    /// <summary>Return (KeySchema, AttributeDefinitions) for a hash-only table.</summary>
    private static (List<KeySchemaElement>, List<AttributeDefinition>) HashKeySchema(string keyName = "pk") =>
        ([new KeySchemaElement(keyName, KeyType.HASH)], [new AttributeDefinition(keyName, ScalarAttributeType.S)]);

    /// <summary>
    /// DeleteItem on a key that was never written succeeds silently (no error, no Attributes),
    /// the key stays absent under an independent read, and an unrelated item in the same table is
    /// left untouched (the no-op is scoped to the addressed key, not a side effect on the table).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-nonexistent-key-is-noop")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-delnoop-");
        var (keySchema, attrDefs) = HashKeySchema("pk");
        await CreateTableAndWait(ddb, ctx, tableName, keySchema, attrDefs);

        var controlKey = Item(("pk", S("control-survives")));
        await ddb.PutItemAsync(tableName, Item(("pk", S("control-survives")), ("data", S("untouched"))));

        var missingKey = Item(("pk", S("never-existed")));
        var resp = await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = tableName,
            Key = missingKey,
            ReturnValues = ReturnValue.ALL_OLD,
        });
        Ensure(
            resp.Attributes is null || resp.Attributes.Count == 0,
            $"expected no Attributes deleting a key that was never written, got {Py.Repr(resp.Attributes)}");

        // independent read at the destination: the addressed key stayed absent
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = missingKey, ConsistentRead = true });
        Ensure(got.Item is null, $"delete of a never-written key conjured an item: {Py.Repr(got.Item)}");

        // the no-op must be scoped to the addressed key: an unrelated item survives untouched
        var control = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = controlKey, ConsistentRead = true });
        Ensure(
            control.Item is not null && control.Item.TryGetValue("data", out var data) && Py.Equal(data, S("untouched")),
            $"expected unrelated item to survive untouched, got {Py.Repr(control.Item)}");
    }
}
