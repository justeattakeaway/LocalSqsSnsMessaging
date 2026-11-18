// Translated from behavioral/dynamodb/check_deep_dynamodb_2_remove_list_indices_resolve_against_original.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-remove-list-indices-resolve-against-original</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2RemoveListIndicesResolveAgainstOriginal
{
    private const string Check = "deep-dynamodb-2-remove-list-indices-resolve-against-original";

    /// <summary>
    /// REMOVE l[1], l[2] in ONE expression removes the ORIGINAL elements 1 and 2: [a,d,e].
    ///
    /// The same removals as two calls compound to [a,c,e]; clause order is irrelevant
    /// (`REMOVE l[4], l[0]` -> [b,c,d]).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-remove-list-indices-resolve-against-original")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        string[] five = ["a", "b", "c", "d", "e"];

        // One expression, two indices.
        await ddb.PutItemAsync(t, Item(("pk", S("single")), ("l", SList(five))));
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("single"))),
            UpdateExpression = "REMOVE l[1], l[2]",
            ReturnValues = ReturnValue.ALL_NEW,
        });
        var returned = Strings(resp.Attributes["l"]);
        Ensure(
            returned.SequenceEqual(["a", "d", "e"]),
            $"ALL_NEW after REMOVE l[1], l[2] was {Py.Repr(returned)}; expected [a, d, e]");
        var single = Strings((await Stored(ddb, t, "single"))!["l"]);
        Ensure(
            single.SequenceEqual(["a", "d", "e"]),
            $"stored list after REMOVE l[1], l[2] was {Py.Repr(single)}; expected [a, d, e] " +
            "(both indices against the original list). [a, c, e] means sequential removal.");

        // Control: the same two removals as two separate calls DO compound.
        await ddb.PutItemAsync(t, Item(("pk", S("sequential")), ("l", SList(five))));
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = t, Key = Item(("pk", S("sequential"))), UpdateExpression = "REMOVE l[1]" });
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = t, Key = Item(("pk", S("sequential"))), UpdateExpression = "REMOVE l[2]" });
        var sequential = Strings((await Stored(ddb, t, "sequential"))!["l"]);
        Ensure(
            sequential.SequenceEqual(["a", "c", "e"]),
            $"two separate REMOVE calls (l[1] then l[2]) gave {Py.Repr(sequential)}; expected [a, c, e]");
        Ensure(
            !single.SequenceEqual(sequential),
            $"single-expression and sequential removal agree ({Py.Repr(single)}); they must differ");

        // Clause order inside one expression does not matter.
        await ddb.PutItemAsync(t, Item(("pk", S("reversed")), ("l", SList(five))));
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = t, Key = Item(("pk", S("reversed"))), UpdateExpression = "REMOVE l[4], l[0]" });
        var rev = Strings((await Stored(ddb, t, "reversed"))!["l"]);
        Ensure(
            rev.SequenceEqual(["b", "c", "d"]),
            $"stored list after REMOVE l[4], l[0] was {Py.Repr(rev)}; expected [b, c, d]");
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
