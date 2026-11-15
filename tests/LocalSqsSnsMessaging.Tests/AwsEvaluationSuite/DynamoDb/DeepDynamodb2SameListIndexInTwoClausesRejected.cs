// Translated from behavioral/dynamodb/check_deep_dynamodb_2_same_list_index_in_two_clauses_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-same-list-index-in-two-clauses-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2SameListIndexInTwoClausesRejected
{
    private const string Check = "deep-dynamodb-2-same-list-index-in-two-clauses-rejected";
    private const string ErrValidation = "ValidationException";

    private const string MsgOverlapL1 =
        "1 validation error detected: Invalid UpdateExpression: Two document paths overlap with each " +
        "other; must remove or rewrite one of these paths; path one: [l, [1]], path two: [l, [1]]";

    /// <summary>
    /// Naming one list index in two clauses (REMOVE l[1] SET l[1]; REMOVE l[1], l[1]) is rejected.
    ///
    /// The message renders the index as `[l, [1]]`; the item is unchanged after each rejection,
    /// and each clause alone works.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-same-list-index-in-two-clauses-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        var key = Item(("pk", S("k")));
        var original = Item(("pk", S("k")), ("l", SList("a", "b", "c", "d", "e")));
        await ddb.PutItemAsync(t, original);

        var (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "REMOVE l[1] SET l[1] = :v",
            ExpressionAttributeValues = Item((":v", S("X"))),
        }));
        Ensure(code == ErrValidation, $"REMOVE l[1] SET l[1]: expected {ErrValidation}, got {Py.Repr(code)} ({Py.Repr(msg)})");
        Ensure(msg == MsgOverlapL1, $"REMOVE l[1] SET l[1]: expected {Py.Repr(MsgOverlapL1)}, got {Py.Repr(msg)}");
        var after = await Stored(ddb, t, "k");
        Ensure(Py.Equal(after, original), $"rejected update changed the item: {Py.Repr(after)}");

        (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "REMOVE l[1], l[1]",
        }));
        Ensure(code == ErrValidation, $"REMOVE l[1], l[1]: expected {ErrValidation}, got {Py.Repr(code)} ({Py.Repr(msg)})");
        Ensure(msg == MsgOverlapL1, $"REMOVE l[1], l[1]: expected {Py.Repr(MsgOverlapL1)}, got {Py.Repr(msg)}");
        after = await Stored(ddb, t, "k");
        Ensure(Py.Equal(after, original), $"rejected update changed the item: {Py.Repr(after)}");

        // Positive controls: each clause alone is valid.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET l[1] = :v",
            ExpressionAttributeValues = Item((":v", S("X"))),
        });
        after = (await Stored(ddb, t, "k"))!;
        Ensure(Py.Equal(after["l"], SList("a", "X", "c", "d", "e")), $"SET l[1] alone stored {Py.Repr(after)}");
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = t, Key = key, UpdateExpression = "REMOVE l[1]" });
        after = (await Stored(ddb, t, "k"))!;
        Ensure(Py.Equal(after["l"], SList("a", "c", "d", "e")), $"REMOVE l[1] alone stored {Py.Repr(after)}");
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
