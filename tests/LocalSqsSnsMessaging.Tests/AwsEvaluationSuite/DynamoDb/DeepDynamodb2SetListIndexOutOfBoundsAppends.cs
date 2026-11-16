// Translated from behavioral/dynamodb/check_deep_dynamodb_2_set_list_index_out_of_bounds_appends.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-set-list-index-out-of-bounds-appends</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2SetListIndexOutOfBoundsAppends
{
    private const string Check = "deep-dynamodb-2-set-list-index-out-of-bounds-appends";

    /// <summary>
    /// SET l[99] on a 5-element list appends at the end, with no error and no null padding.
    ///
    /// Two out-of-bounds indices in one expression append in ascending index order:
    /// `SET l[99] = X, l[98] = Y` yields [..., Y, X].
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-set-list-index-out-of-bounds-appends")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);

        await ddb.PutItemAsync(t, Item(("pk", S("one")), ("l", SList("a", "b", "c", "d", "e"))));
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("one"))),
            UpdateExpression = "SET l[99] = :v",
            ExpressionAttributeValues = Item((":v", S("X"))),
            ReturnValues = ReturnValue.ALL_NEW,
        });
        var returned = Strings(resp.Attributes["l"]);
        Ensure(
            returned.SequenceEqual(["a", "b", "c", "d", "e", "X"]),
            $"ALL_NEW after SET l[99] was {Py.Repr(returned)}; expected X appended at the end");
        var stored = Strings((await Stored(ddb, t, "one"))!["l"]);
        Ensure(stored.Count == 6, $"stored list length {stored.Count} after out-of-bounds SET; expected 6: {Py.Repr(stored)}");
        Ensure(
            stored.SequenceEqual(["a", "b", "c", "d", "e", "X"]),
            $"stored list after SET l[99] was {Py.Repr(stored)}; expected [a, b, c, d, e, X]");

        // The appended element is now addressable at index 5, not 99.
        var got = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("one"))),
            ConsistentRead = true,
            ProjectionExpression = "l[5], l[99]",
        })).Item;
        Ensure(
            Py.Equal(got, Item(("l", L(S("X"))))),
            $"projection l[5], l[99] returned {Py.Repr(got)}; expected only l[5] = X (nothing lives at 99)");

        // Two out-of-bounds indices in one expression: appended in ascending index order.
        await ddb.PutItemAsync(t, Item(("pk", S("two")), ("l", SList("a", "b", "c", "d", "e"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("two"))),
            UpdateExpression = "SET l[99] = :x, l[98] = :y",
            ExpressionAttributeValues = Item((":x", S("X")), (":y", S("Y"))),
        });
        stored = Strings((await Stored(ddb, t, "two"))!["l"]);
        Ensure(
            stored.SequenceEqual(["a", "b", "c", "d", "e", "Y", "X"]),
            $"stored list after SET l[99]=X, l[98]=Y was {Py.Repr(stored)}; expected [a, b, c, d, e, Y, X] " +
            "(appends ordered by index, not by clause order)");
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
            Ensure(Py.Boto(el).Keys.SequenceEqual(["S"]), $"list element is not a plain string (padding?): {Py.Repr(el)} in {Py.Repr(attr)}");
            result.Add(el.S);
        }

        return result;
    }

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}
