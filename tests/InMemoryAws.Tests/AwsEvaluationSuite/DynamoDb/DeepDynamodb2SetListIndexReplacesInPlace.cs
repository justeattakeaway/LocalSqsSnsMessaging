// Translated from behavioral/dynamodb/check_deep_dynamodb_2_set_list_index_replaces_in_place.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-set-list-index-replaces-in-place</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2SetListIndexReplacesInPlace
{
    private const string Check = "deep-dynamodb-2-set-list-index-replaces-in-place";

    /// <summary>
    /// SET l[0] on a 5-element list overwrites element 0 in place (returned and stored list stay 5 long), a second
    /// SET l[3] changes only element 3, and a nested path m[0][1] addresses the inner list's element.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-set-list-index-replaces-in-place")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);

        await ddb.PutItemAsync(t, Item(("pk", S("flat")), ("l", SList("a", "b", "c", "d", "e"))));
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("flat"))),
            UpdateExpression = "SET l[0] = :v",
            ExpressionAttributeValues = Item((":v", S("X"))),
            ReturnValues = ReturnValue.ALL_NEW,
        });
        var returned = Strings(resp.Attributes["l"]);
        Ensure(
            returned.SequenceEqual(["X", "b", "c", "d", "e"]),
            $"ALL_NEW after SET l[0] was {Py.Repr(returned)}; expected index 0 replaced in place");
        var stored = Strings((await Stored(ddb, t, "flat"))!["l"]);
        Ensure(
            stored.SequenceEqual(["X", "b", "c", "d", "e"]),
            $"stored list after SET l[0] was {Py.Repr(stored)}; expected [X, b, c, d, e] (no insert, no shift)");
        Ensure(stored.Count == 5, $"list length changed from 5 to {stored.Count}: {Py.Repr(stored)}");

        // A middle index replaces only that element too.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("flat"))),
            UpdateExpression = "SET l[3] = :v",
            ExpressionAttributeValues = Item((":v", S("Y"))),
        });
        stored = Strings((await Stored(ddb, t, "flat"))!["l"]);
        Ensure(
            stored.SequenceEqual(["X", "b", "c", "Y", "e"]),
            $"stored list after SET l[3] was {Py.Repr(stored)}; expected [X, b, c, Y, e]");

        // Nested list path: m[0][1] addresses the inner list.
        await ddb.PutItemAsync(t, Item(("pk", S("nested")), ("m", L(SList("p", "q"), S("r")))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("nested"))),
            UpdateExpression = "SET m[0][1] = :v",
            ExpressionAttributeValues = Item((":v", S("Q"))),
        });
        var m = (await Stored(ddb, t, "nested"))!["m"];
        Ensure(
            Py.Equal(m, L(L(S("p"), S("Q")), S("r"))),
            $"stored nested list after SET m[0][1] was {Py.Repr(m)}; expected [[p, Q], r]");
    }

    private static async Task<string> Table(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("ddbexpr-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    private static AttributeValue SList(params string[] values) => L([.. values.Select(v => S(v))]);

    private static List<string> Strings(AttributeValue attr)
    {
        Ensure(Py.Boto(attr).Keys.SequenceEqual(["L"]), $"expected a list attribute, got {Py.Repr(attr)}");
        var result = new List<string>();
        foreach (var el in attr.L)
        {
            Ensure(Py.Boto(el).Keys.SequenceEqual(["S"]), $"list element is not a plain string: {Py.Repr(el)} in {Py.Repr(attr)}");
            result.Add(el.S);
        }

        return result;
    }

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}
