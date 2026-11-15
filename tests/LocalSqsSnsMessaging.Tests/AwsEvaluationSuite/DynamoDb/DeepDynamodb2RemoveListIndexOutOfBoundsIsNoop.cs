// Translated from behavioral/dynamodb/check_deep_dynamodb_2_remove_list_index_out_of_bounds_is_noop.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-remove-list-index-out-of-bounds-is-noop</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2RemoveListIndexOutOfBoundsIsNoop
{
    private const string Check = "deep-dynamodb-2-remove-list-index-out-of-bounds-is-noop";

    /// <summary>
    /// REMOVE l[99] on a 5-element list succeeds and leaves the stored list unchanged (no clamping to the last
    /// element), while REMOVE l[99], l[0] in one expression removes only the first element.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-remove-list-index-out-of-bounds-is-noop")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);

        await ddb.PutItemAsync(t, Item(("pk", S("k")), ("l", SList("a", "b", "c", "d", "e"))));
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("k"))),
            UpdateExpression = "REMOVE l[99]",
            ReturnValues = ReturnValue.ALL_NEW,
        });
        Ensure((int)resp.HttpStatusCode == 200, $"REMOVE l[99] response: {Py.Repr(resp.Attributes)}");
        var stored = Strings((await Stored(ddb, t, "k"))!["l"]);
        Ensure(
            stored.SequenceEqual(["a", "b", "c", "d", "e"]),
            $"stored list after REMOVE l[99] was {Py.Repr(stored)}; expected it unchanged [a, b, c, d, e]");

        // Mixed with an in-bounds index: only the in-bounds removal applies.
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = t, Key = Item(("pk", S("k"))), UpdateExpression = "REMOVE l[99], l[0]" });
        stored = Strings((await Stored(ddb, t, "k"))!["l"]);
        Ensure(
            stored.SequenceEqual(["b", "c", "d", "e"]),
            $"stored list after REMOVE l[99], l[0] was {Py.Repr(stored)}; expected [b, c, d, e]");
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
