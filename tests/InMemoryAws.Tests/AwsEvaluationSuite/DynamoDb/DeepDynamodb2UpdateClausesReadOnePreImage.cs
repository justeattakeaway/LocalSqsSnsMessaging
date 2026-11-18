// Translated from behavioral/dynamodb/check_deep_dynamodb_2_update_clauses_read_one_pre_image.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-update-clauses-read-one-pre-image</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2UpdateClausesReadOnePreImage
{
    private const string Check = "deep-dynamodb-2-update-clauses-read-one-pre-image";

    /// <summary>
    /// Every right-hand side of one UpdateExpression reads the same pre-image: from {a:1, b:2, c:3},
    /// `REMOVE a SET b = a, c = b` stores exactly {b:1, c:2}, and `SET a = c, b = a, c = b` rotates to {a:3, b:1, c:2}.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-update-clauses-read-one-pre-image")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        var key = Item(("pk", S("k")));

        await ddb.PutItemAsync(t, Item(("pk", S("k")), ("a", N("1")), ("b", N("2")), ("c", N("3"))));
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "REMOVE a SET b = a, c = b",
            ReturnValues = ReturnValue.ALL_NEW,
        });
        var returned = resp.Attributes;
        Ensure(
            Py.Equal(returned, Item(("pk", S("k")), ("b", N("1")), ("c", N("2")))),
            $"ALL_NEW was {Py.Repr(returned)}; expected exactly {{pk, b:1, c:2}}");
        var stored = (await Stored(ddb, t, "k"))!;
        Ensure(
            Py.Equal(stored, Item(("pk", S("k")), ("b", N("1")), ("c", N("2")))),
            $"stored item was {Py.Repr(stored)}; expected exactly {{pk, b:1, c:2}}. " +
            "c == 1 means clauses were evaluated sequentially instead of against one pre-image.");
        Ensure(!stored.ContainsKey("a"), $"REMOVE a did not take effect: {Py.Repr(stored)}");

        // Clause order in the text does not change the result: SET before REMOVE, rotated.
        await ddb.PutItemAsync(t, Item(("pk", S("k")), ("a", N("1")), ("b", N("2")), ("c", N("3"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = t, Key = key, UpdateExpression = "SET a = c, b = a, c = b" });
        var rotated = await Stored(ddb, t, "k");
        Ensure(
            Py.Equal(rotated, Item(("pk", S("k")), ("a", N("3")), ("b", N("1")), ("c", N("2")))),
            $"SET a = c, b = a, c = b stored {Py.Repr(rotated)}; expected a rotation {{a:3, b:1, c:2}}");
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

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}
