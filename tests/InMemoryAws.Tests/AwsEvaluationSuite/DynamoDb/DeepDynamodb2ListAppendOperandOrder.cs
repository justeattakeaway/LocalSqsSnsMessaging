// Translated from behavioral/dynamodb/check_deep_dynamodb_2_list_append_operand_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-list-append-operand-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2ListAppendOperandOrder
{
    private const string Check = "deep-dynamodb-2-list-append-operand-order";

    /// <summary>
    /// list_append(#l, :v) appends and list_append(:v, #l) prepends, as the stored item shows after each update,
    /// and a multi-element operand keeps its own order: concatenation follows argument order.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-list-append-operand-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        string[] five = ["a", "b", "c", "d", "e"];

        await ddb.PutItemAsync(t, Item(("pk", S("append")), ("l", SList(five))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("append"))),
            UpdateExpression = "SET #l = list_append(#l, :v)",
            ExpressionAttributeNames = Names(("#l", "l")),
            ExpressionAttributeValues = Item((":v", SList("Z"))),
        });
        var appended = Strings((await Stored(ddb, t, "append"))!["l"]);
        Ensure(
            appended.SequenceEqual(["a", "b", "c", "d", "e", "Z"]),
            $"list_append(#l, :v) stored {Py.Repr(appended)}; expected Z at the END");

        await ddb.PutItemAsync(t, Item(("pk", S("prepend")), ("l", SList(five))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("prepend"))),
            UpdateExpression = "SET #l = list_append(:v, #l)",
            ExpressionAttributeNames = Names(("#l", "l")),
            ExpressionAttributeValues = Item((":v", SList("Z"))),
        });
        var prepended = Strings((await Stored(ddb, t, "prepend"))!["l"]);
        Ensure(
            prepended.SequenceEqual(["Z", "a", "b", "c", "d", "e"]),
            $"list_append(:v, #l) stored {Py.Repr(prepended)}; expected Z at the START");
        Ensure(!appended.SequenceEqual(prepended), $"operand order had no effect: both gave {Py.Repr(appended)}");

        // Multi-element operand keeps its own internal order on both sides.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("prepend"))),
            UpdateExpression = "SET #l = list_append(:v, #l)",
            ExpressionAttributeNames = Names(("#l", "l")),
            ExpressionAttributeValues = Item((":v", SList("P", "Q"))),
        });
        var both = Strings((await Stored(ddb, t, "prepend"))!["l"]);
        Ensure(
            both.SequenceEqual(["P", "Q", "Z", "a", "b", "c", "d", "e"]),
            $"prepending [P, Q] stored {Py.Repr(both)}; expected [P, Q, Z, a, b, c, d, e]");
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
