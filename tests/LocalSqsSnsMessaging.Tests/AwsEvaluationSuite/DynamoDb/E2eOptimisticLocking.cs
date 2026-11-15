// Translated from behavioral/dynamodb/check_e2e_optimistic_locking.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.e2e-optimistic-locking</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2eOptimisticLocking
{
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";
    private static readonly string[] CcfCodes = [ErrConditionalCheckFailed];

    /// <summary>Optimistic locking: conditional update with version check, stale version rejected.</summary>
    [Test]
    [DisplayName("dynamodb.e2e-optimistic-locking")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = "e2e-optimistic-locking" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("id", S("doc-1")), ("version", N("1")), ("data", S("initial"))));
        await ddb.UpdateItemAsync(VersionedUpdate(table, "1", "updated-v2"));
        var got = await Get(ddb, table);
        Ensure(Py.Equal(got!["version"], N("2")), $"Expected version 2, got {Py.Repr(got["version"])}");
        Ensure(Py.Equal(got["data"], S("updated-v2")), $"Expected data 'updated-v2', got {Py.Repr(got["data"])}");
        var exc = await ExpectError(() => ddb.UpdateItemAsync(VersionedUpdate(table, "1", "stale-update")), CcfCodes);
        var message = exc.Message;
        Ensure(message == "The conditional request failed", $"the stale update: got the message {Py.Repr(message)}");
        var got2 = await Get(ddb, table);
        Ensure(Py.Equal(got2!["version"], N("2")), $"Expected version to stay 2 after rejected stale update, got {Py.Repr(got2["version"])}");
        Ensure(Py.Equal(got2["data"], S("updated-v2")), $"Expected data to stay 'updated-v2' after rejected stale update, got {Py.Repr(got2["data"])}");
        await ddb.UpdateItemAsync(VersionedUpdate(table, "2", "updated-v3"));
        var got3 = await Get(ddb, table);
        Ensure(Py.Equal(got3!["version"], N("3")), $"Expected version 3 after retrying with the current version, got {Py.Repr(got3["version"])}");
        Ensure(Py.Equal(got3["data"], S("updated-v3")), $"Expected data 'updated-v3' after retrying with the current version, got {Py.Repr(got3["data"])}");

        // Item-level gone beats: the deleted item reads absent, queries empty, and is not resurrected.
        await ddb.DeleteItemAsync(table, Item(("id", S("doc-1"))));
        var gone = await Get(ddb, table);
        Ensure(gone is null, $"deleted item doc-1 still readable: {Py.Repr(gone)}");
        var q = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "id = :id",
            ExpressionAttributeValues = Item((":id", S("doc-1"))),
            ConsistentRead = true,
        });
        Ensure(q.Count == 0, $"expected Query for deleted doc-1 to return Count 0, got {q.Count}: {Py.Repr(q.Items)}");
        exc = await ExpectError(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("id", S("doc-1"))),
                UpdateExpression = "SET #d = :data",
                ConditionExpression = "attribute_exists(id)",
                ExpressionAttributeNames = Names(("#d", "data")),
                ExpressionAttributeValues = Item((":data", S("resurrected"))),
            }),
            CcfCodes);
        message = exc.Message;
        Ensure(message == "The conditional request failed", $"the resurrecting update: got the message {Py.Repr(message)}");
        var after = await Get(ddb, table);
        Ensure(after is null, $"refused conditional update resurrected doc-1: {Py.Repr(after)}");
    }

    private static UpdateItemRequest VersionedUpdate(string table, string expectedVersion, string data) => new()
    {
        TableName = table,
        Key = Item(("id", S("doc-1"))),
        UpdateExpression = "SET version = version + :inc, #d = :data",
        ConditionExpression = "version = :v",
        ExpressionAttributeNames = Names(("#d", "data")),
        ExpressionAttributeValues = Item((":inc", N("1")), (":v", N(expectedVersion)), (":data", S(data))),
    };

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("id", S("doc-1"))), ConsistentRead = true })).Item;

    /// <summary>Grade a ClientError on code, HTTP status class, a non-empty message and the ids it must name.</summary>
    private static void AssertError(AwsError exc, string[] codes, int status = 400, params string[] contains)
    {
        var code = exc.Code;
        var msg = exc.Message ?? string.Empty;
        var gotStatus = (int)exc.StatusCode;
        Ensure(codes.Contains(code), $"expected error code in {Py.Repr(Tuple(codes))}, got {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(gotStatus / 100 == status / 100, $"expected HTTP {status / 100}xx with {code}, got {Py.Repr(gotStatus)}");
        Ensure(msg.Trim().Length > 0, $"expected a non-empty message with {code}");
        foreach (var want in contains)
        {
            Ensure(msg.Contains(want, StringComparison.Ordinal), $"expected the {code} message to name {Py.Repr(want)}, got {Py.Repr(msg)}");
        }
    }

    /// <summary>Call fn() and grade the ClientError it raises; an accepted call is itself a failure.</summary>
    private static async Task<AwsError> ExpectError<T>(Func<Task<T>> fn, string[] codes, int status = 400, params string[] contains)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected {Py.Repr(Tuple(codes))}, but the call succeeded: {Py.Repr(result)}");
        }

        AssertError(error, codes, status, contains);
        return error;
    }

    /// <summary>A one-element tuple's repr, as Python prints <c>('X',)</c>.</summary>
    private static Tuple<string> Tuple(string[] codes) => new(codes[0]);
}
