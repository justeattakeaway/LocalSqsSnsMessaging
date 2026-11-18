// Translated from behavioral/dynamodb/check_deep_dynamodb_2_list_index_on_non_list_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-list-index-on-non-list-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2ListIndexOnNonListRejected
{
    private const string Check = "deep-dynamodb-2-list-index-on-non-list-rejected";
    private const string ErrValidation = "ValidationException";
    private const string MsgInvalidPath = "The document path provided in the update expression is invalid for update";

    /// <summary>
    /// SET x[0] on a string attribute or an absent one raises ValidationException with the exact message and leaves
    /// the item unchanged, while SET l[0] on a list attribute of the same item replaces element 0.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-list-index-on-non-list-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        var key = Item(("pk", S("k")));
        var original = Item(("pk", S("k")), ("s", S("str")), ("l", SList("a", "b", "c", "d", "e")));
        await ddb.PutItemAsync(t, original);
        Dictionary<string, AttributeValue> Vals() => Item((":v", S("X")));

        var (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET s[0] = :v",
            ExpressionAttributeValues = Vals(),
        }));
        Ensure(code == ErrValidation, $"SET s[0] on a string: expected {ErrValidation}, got {Py.Repr(code)} ({Py.Repr(msg)})");
        Ensure(msg == MsgInvalidPath, $"SET s[0] on a string: expected {Py.Repr(MsgInvalidPath)}, got {Py.Repr(msg)}");
        var after = await Stored(ddb, t, "k");
        Ensure(Py.Equal(after, original), $"rejected update changed the item: {Py.Repr(after)}");

        (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET nope[0] = :v",
            ExpressionAttributeValues = Vals(),
        }));
        Ensure(code == ErrValidation, $"SET nope[0] on absent attr: expected {ErrValidation}, got {Py.Repr(code)} ({Py.Repr(msg)})");
        Ensure(msg == MsgInvalidPath, $"SET nope[0] on absent attr: expected {Py.Repr(MsgInvalidPath)}, got {Py.Repr(msg)}");
        after = await Stored(ddb, t, "k");
        Ensure(Py.Equal(after, original), $"rejected update changed the item: {Py.Repr(after)}");
        Ensure(!after!.ContainsKey("nope"), $"rejected update created attribute nope: {Py.Repr(after)}");

        // Positive control: the same form on the real list in the same item.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET l[0] = :v",
            ExpressionAttributeValues = Vals(),
        });
        after = (await Stored(ddb, t, "k"))!;
        Ensure(Py.Equal(after["l"], SList("X", "b", "c", "d", "e")), $"SET l[0] on a list stored {Py.Repr(after)}");
        Ensure(Py.Equal(after["s"], S("str")), $"string attribute changed: {Py.Repr(after)}");
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

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    /// <summary>(code, message) of the ClientError fn raises. Raises if fn SUCCEEDS.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"call was expected to be rejected but succeeded: {resp}");
        }

        return (error.Code, error.Message);
    }
}
