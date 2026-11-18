// Translated from behavioral/dynamodb/check_partiql_and_classic_api_address_one_item_store.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-and-classic-api-address-one-item-store</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlAndClassicApiAddressOneItemStore
{
    private const string CheckId = "partiql-and-classic-api-address-one-item-store";

    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> item, params (string Name, AttributeValue Value)[] extra)
    {
        var copy = new Dictionary<string, AttributeValue>(item, StringComparer.Ordinal);
        foreach (var (name, value) in extra)
        {
            copy[name] = value;
        }

        return copy;
    }

    /// <summary>
    /// PartiQL and the classic API share one item store: inserts, updates and deletes made through
    /// either are visible through the other, compared as whole items.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-and-classic-api-address-one-item-store")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlstore-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task<List<Dictionary<string, AttributeValue>>> Select(string pk) =>
            (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{table}\" WHERE pk = ?",
                Parameters = [S(pk)],
                ConsistentRead = true,
            })).Items;

        async Task<Dictionary<string, AttributeValue>?> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // --- a PartiQL INSERT is visible to GetItem ---
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{table}\" VALUE {{'pk': ?, 'body': ?, 'n': ?}}",
            Parameters = [S("via-partiql"), S("inserted"), N("1")],
        });
        var inserted = Item(("pk", S("via-partiql")), ("body", S("inserted")), ("n", N("1")));
        Ensure(
            Py.Equal(await Get("via-partiql"), inserted),
            $"an item inserted with PartiQL must be readable by a strongly consistent GetItem: expected {Py.Repr(inserted)}, got {Py.Repr(await Get("via-partiql"))}");

        // --- a classic PutItem is visible to a PartiQL SELECT ---
        var written = Item(("pk", S("via-classic")), ("body", S("written")), ("n", N("2")));
        await ddb.PutItemAsync(table, written);
        Ensure(
            Py.Equal(await Select("via-classic"), new[] { written }),
            $"an item written with PutItem must be returned by a PartiQL SELECT: expected {Py.Repr(new[] { written })}, got {Py.Repr(await Select("via-classic"))}");

        // --- a PartiQL UPDATE is visible to GetItem ---
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET body = ? WHERE pk = ?",
            Parameters = [S("partiql-updated"), S("via-classic")],
        });
        var expected = With(written, ("body", S("partiql-updated")));
        Ensure(
            Py.Equal(await Get("via-classic"), expected),
            $"a PartiQL UPDATE must be visible to GetItem: expected {Py.Repr(expected)}, got {Py.Repr(await Get("via-classic"))}");

        // --- a classic UpdateItem is visible to a PartiQL SELECT ---
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("via-partiql"))),
            UpdateExpression = "SET body = :b",
            ExpressionAttributeValues = Item((":b", S("classic-updated"))),
        });
        expected = With(inserted, ("body", S("classic-updated")));
        Ensure(
            Py.Equal(await Select("via-partiql"), new[] { expected }),
            $"an UpdateItem must be visible to a PartiQL SELECT: expected {Py.Repr(new[] { expected })}, got {Py.Repr(await Select("via-partiql"))}");

        // --- a PartiQL DELETE removes the item for GetItem too ---
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"DELETE FROM \"{table}\" WHERE pk = ?",
            Parameters = [S("via-classic")],
        });
        Ensure(await Get("via-classic") is null, $"a PartiQL DELETE must remove the item for GetItem as well, found {Py.Repr(await Get("via-classic"))}");

        // --- a classic DeleteItem removes the item for PartiQL too ---
        await ddb.DeleteItemAsync(table, Item(("pk", S("via-partiql"))));
        Ensure((await Select("via-partiql")).Count == 0, $"a DeleteItem must remove the item for a PartiQL SELECT as well, found {Py.Repr(await Select("via-partiql"))}");
    }
}
