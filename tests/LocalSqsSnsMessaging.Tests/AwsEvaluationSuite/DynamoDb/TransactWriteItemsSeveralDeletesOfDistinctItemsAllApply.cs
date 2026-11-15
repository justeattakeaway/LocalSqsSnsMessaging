// Translated from behavioral/dynamodb/check_transact_write_items_several_deletes_of_distinct_items_all_apply.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-several-deletes-of-distinct-items-all-apply</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsSeveralDeletesOfDistinctItemsAllApply
{
    private const string Check = "transact-write-items-several-deletes-of-distinct-items-all-apply";
    private static readonly string[] Keys = ["tx-del-a", "tx-del-b", "tx-del-c"];

    /// <summary>
    /// A TransactWriteItems holding several Deletes of distinct items commits every one: none
    /// of the deleted items is readable afterwards, and an item outside the transaction survives.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-several-deletes-of-distinct-items-all-apply")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var key in Keys)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(key)), ("v", N("1"))));
        }

        await ddb.PutItemAsync(table, Item(("pk", S("tx-del-survivor")), ("v", N("1"))));

        foreach (var key in Keys)
        {
            var before = await Get(ddb, table, key);
            Ensure(before is not null, $"setup: {Py.Repr(key)} must exist before the transaction, got none");
        }

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [.. Keys.Select(key => new TransactWriteItem { Delete = new Delete { TableName = table, Key = Item(("pk", S(key))) } })],
        });

        foreach (var key in Keys)
        {
            var after = await Get(ddb, table, key);
            Ensure(after is null, $"{Py.Repr(key)} must be gone after the transaction deleted it, got {Py.Repr(after)}");
        }

        var survivor = await Get(ddb, table, "tx-del-survivor");
        Ensure(
            Py.Equal(survivor, Item(("pk", S("tx-del-survivor")), ("v", N("1")))),
            $"an item outside the transaction must be untouched, got {Py.Repr(survivor)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}
