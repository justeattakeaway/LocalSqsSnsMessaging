// Translated from behavioral/dynamodb/check_deep_dynamodb_2_list_index_clauses_read_one_pre_image.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-list-index-clauses-read-one-pre-image</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2ListIndexClausesReadOnePreImage
{
    private const string Check = "deep-dynamodb-2-list-index-clauses-read-one-pre-image";

    /// <summary>
    /// List-index clauses in one expression read and address the PRE-update list.
    ///
    /// `SET l[0] = l[1], l[1] = l[0]` swaps ([b,a,c,d,e]); `REMOVE l[0] SET l[1] = :v` replaces
    /// original index 1 and removes original index 0 ([X,c,d,e]).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-list-index-clauses-read-one-pre-image")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        string[] five = ["a", "b", "c", "d", "e"];

        await ddb.PutItemAsync(t, Item(("pk", S("swap")), ("l", SList(five))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("swap"))),
            UpdateExpression = "SET l[0] = l[1], l[1] = l[0]",
        });
        var swapped = Strings((await Stored(ddb, t, "swap"))!["l"]);
        Ensure(
            swapped.SequenceEqual(["b", "a", "c", "d", "e"]),
            $"SET l[0] = l[1], l[1] = l[0] stored {Py.Repr(swapped)}; expected a swap [b, a, c, d, e]. " +
            "[b, b, ...] means the second clause read the already-updated l[0].");

        await ddb.PutItemAsync(t, Item(("pk", S("mixed")), ("l", SList(five))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("mixed"))),
            UpdateExpression = "REMOVE l[0] SET l[1] = :v",
            ExpressionAttributeValues = Item((":v", S("X"))),
        });
        var mixed = Strings((await Stored(ddb, t, "mixed"))!["l"]);
        Ensure(
            mixed.SequenceEqual(["X", "c", "d", "e"]),
            $"REMOVE l[0] SET l[1] = :v stored {Py.Repr(mixed)}; expected [X, c, d, e] " +
            "(original b replaced, original a removed). [b, X, d, e] means REMOVE shifted first.");

        // A RHS reading a list element sees the pre-image even when the same list is rewritten.
        await ddb.PutItemAsync(t, Item(("pk", S("read")), ("l", SList(five))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("read"))),
            UpdateExpression = "SET l = list_append(:z, l), last_seen = l[4]",
            ExpressionAttributeValues = Item((":z", SList("Z"))),
        });
        var item = (await Stored(ddb, t, "read"))!;
        var lastSeen = item.GetValueOrDefault("last_seen");
        Ensure(
            lastSeen is not null && Py.Equal(lastSeen, S("e")),
            $"last_seen = l[4] stored {Py.Repr(lastSeen)}; expected the pre-image element e " +
            "(d would mean it read the list after the prepend)");
        Ensure(Strings(item["l"]).SequenceEqual(["Z", "a", "b", "c", "d", "e"]), $"list after prepend: {Py.Repr(item["l"])}");
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
