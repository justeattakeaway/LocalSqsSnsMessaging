// Translated from behavioral/dynamodb/check_put_get_collection_types.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-get-collection-types</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutGetCollectionTypes
{
    private const string CheckId = "put-get-collection-types";

    /// <summary>PutItem/GetItem round-trips collection types: L, M, SS, NS, BS.</summary>
    [Test]
    [DisplayName("dynamodb.put-get-collection-types")]
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
            Tags = [new Tag { Key = "sim2real-behav", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var item = Item(
            ("pk", S("collections")),
            ("string_set", SS("a", "b", "c")),
            ("number_set", NS("1", "2", "3")),
            ("binary_set", BS("x"u8.ToArray(), "y"u8.ToArray(), "z"u8.ToArray())),
            ("list_attr", L(S("x"), N("42"), Bool(true))),
            ("map_attr", M(("nested", S("value")), ("count", N("7")))));
        await ddb.PutItemAsync(table, item);
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("collections"))), ConsistentRead = true })).Item;
        Ensure(got["string_set"].SS.ToHashSet().SetEquals(["a", "b", "c"]), $"SS round-trip: expected {{'a','b','c'}}, got {Py.Repr(got.GetValueOrDefault("string_set"))}");
        Ensure(got["number_set"].NS.ToHashSet().SetEquals(["1", "2", "3"]), $"NS round-trip: expected {{'1','2','3'}}, got {Py.Repr(got.GetValueOrDefault("number_set"))}");
        Ensure(
            got["binary_set"].BS.Select(b => Convert.ToHexString(b.ToArray())).ToHashSet().SetEquals(["78", "79", "7A"]),
            $"BS round-trip: expected {{b'x',b'y',b'z'}}, got {Py.Repr(got.GetValueOrDefault("binary_set"))}");
        Ensure(Py.Equal(got["list_attr"], L(S("x"), N("42"), Bool(true))), $"L round-trip: expected [S,N,BOOL] list, got {Py.Repr(got.GetValueOrDefault("list_attr"))}");
        Ensure(Py.Equal(got["map_attr"], M(("nested", S("value")), ("count", N("7")))), $"M round-trip: expected nested map, got {Py.Repr(got.GetValueOrDefault("map_attr"))}");
    }
}
