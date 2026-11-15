// Translated from behavioral/dynamodb/check_put_item_overwrite_replaces_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-overwrite-replaces-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemOverwriteReplacesItem
{
    private const string CheckId = "put-item-overwrite-replaces-item";

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE (a transient 404 counts as not-yet).</summary>
    private static async Task<string> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 60.0, double intervalS = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(tableName));
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
                status = resp!.Table.TableStatus?.Value;
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutS}s (last={status})");
    }

    /// <summary>Create a table, register teardown, and wait until ACTIVE.</summary>
    private static async Task<CreateTableResponse> CreateTableAndWait(
        IAmazonDynamoDB ddb,
        FidelityContext ctx,
        string tableName,
        List<KeySchemaElement> keySchema,
        List<AttributeDefinition> attributeDefinitions,
        string billingMode = "PAY_PER_REQUEST",
        double timeoutS = 60.0)
    {
        var resp = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = keySchema,
            AttributeDefinitions = attributeDefinitions,
            BillingMode = billingMode,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitTableActive(ddb, tableName, timeoutS: timeoutS);
        return resp;
    }

    /// <summary>Return (KeySchema, AttributeDefinitions) for a hash-only table.</summary>
    private static (List<KeySchemaElement> KeySchema, List<AttributeDefinition> AttributeDefinitions) HashKeySchema(string keyName = "pk", string keyType = "S") =>
        ([new KeySchemaElement(keyName, KeyType.HASH)], [new AttributeDefinition(keyName, keyType)]);

    /// <summary>PutItem fully replaces an existing item (no merge) — previously-present attributes disappear.</summary>
    [Test]
    [DisplayName("dynamodb.put-item-overwrite-replaces-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-overwrite-");
        var (keySchema, attrDefs) = HashKeySchema("pk");
        await CreateTableAndWait(ddb, ctx, tableName, keySchema, attrDefs);

        await ddb.PutItemAsync(tableName, Item(("pk", S("k1")), ("a", N("1")), ("b", N("2"))));

        var resp = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = tableName,
            Item = Item(("pk", S("k1")), ("c", N("3"))),
            ReturnValues = ReturnValue.ALL_OLD,
        });
        var oldAttrs = resp.Attributes;
        Ensure(oldAttrs["a"].N == "1", $"expected old attribute a=1, got {Py.Repr(oldAttrs.GetValueOrDefault("a"))}");
        Ensure(oldAttrs["b"].N == "2", $"expected old attribute b=2, got {Py.Repr(oldAttrs.GetValueOrDefault("b"))}");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("k1"))), ConsistentRead = true })).Item;
        Ensure(!item.ContainsKey("a"), $"expected attribute a to be gone after overwrite, item={Py.Repr(item)}");
        Ensure(!item.ContainsKey("b"), $"expected attribute b to be gone after overwrite, item={Py.Repr(item)}");
        Ensure(item["c"].N == "3", $"expected surviving attribute c=3, got {Py.Repr(item.GetValueOrDefault("c"))}");
    }
}
