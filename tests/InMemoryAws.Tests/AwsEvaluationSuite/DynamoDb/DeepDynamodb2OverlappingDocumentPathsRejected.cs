// Translated from behavioral/dynamodb/check_deep_dynamodb_2_overlapping_document_paths_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-overlapping-document-paths-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2OverlappingDocumentPathsRejected
{
    private const string Check = "deep-dynamodb-2-overlapping-document-paths-rejected";
    private const string ErrValidation = "ValidationException";

    private const string MsgOverlapAB_A =
        "1 validation error detected: Invalid UpdateExpression: Two document paths overlap with each " +
        "other; must remove or rewrite one of these paths; path one: [a, b], path two: [a]";

    private const string MsgOverlapAB_ABC =
        "1 validation error detected: Invalid UpdateExpression: Two document paths overlap with each " +
        "other; must remove or rewrite one of these paths; path one: [a, b], path two: [a, b, c]";

    /// <summary>
    /// `SET a.b = :v REMOVE a` and `SET a.b = :v, a.b.c = :w` each raise ValidationException with the exact overlap
    /// message and change nothing, while each clause on its own succeeds against the same item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-overlapping-document-paths-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        var key = Item(("pk", S("k")));
        var original = Item(("pk", S("k")), ("a", M(("b", N("1")))));
        await ddb.PutItemAsync(t, original);

        var (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET a.b = :v REMOVE a",
            ExpressionAttributeValues = Item((":v", N("2"))),
        }));
        Ensure(code == ErrValidation, $"SET a.b REMOVE a: expected {ErrValidation}, got {Py.Repr(code)} ({Py.Repr(msg)})");
        Ensure(msg == MsgOverlapAB_A, $"SET a.b REMOVE a: expected {Py.Repr(MsgOverlapAB_A)}, got {Py.Repr(msg)}");
        var after = await Stored(ddb, t, "k");
        Ensure(Py.Equal(after, original), $"rejected update changed the item: {Py.Repr(after)} (was {Py.Repr(original)})");

        (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET a.b = :v, a.b.c = :w",
            ExpressionAttributeValues = Item((":v", N("2")), (":w", N("3"))),
        }));
        Ensure(code == ErrValidation, $"SET a.b, a.b.c: expected {ErrValidation}, got {Py.Repr(code)} ({Py.Repr(msg)})");
        Ensure(msg == MsgOverlapAB_ABC, $"SET a.b, a.b.c: expected {Py.Repr(MsgOverlapAB_ABC)}, got {Py.Repr(msg)}");
        after = await Stored(ddb, t, "k");
        Ensure(Py.Equal(after, original), $"rejected update changed the item: {Py.Repr(after)} (was {Py.Repr(original)})");

        // Positive controls: each clause alone is valid on this item.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET a.b = :v",
            ExpressionAttributeValues = Item((":v", N("2"))),
        });
        after = await Stored(ddb, t, "k");
        Ensure(Py.Equal(after, Item(("pk", S("k")), ("a", M(("b", N("2")))))), $"SET a.b alone stored {Py.Repr(after)}");
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = t, Key = key, UpdateExpression = "REMOVE a" });
        after = await Stored(ddb, t, "k");
        Ensure(Py.Equal(after, Item(("pk", S("k")))), $"REMOVE a alone stored {Py.Repr(after)}");
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
