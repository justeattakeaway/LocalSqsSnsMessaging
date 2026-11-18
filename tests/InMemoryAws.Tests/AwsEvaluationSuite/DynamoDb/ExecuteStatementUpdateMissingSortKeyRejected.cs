// Translated from behavioral/dynamodb/check_execute_statement_update_missing_sort_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-update-missing-sort-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementUpdateMissingSortKeyRejected
{
    private const string CheckId = "execute-statement-update-missing-sort-key-rejected";
    private static readonly (string Code, string Message) MissingKey =
        ("ValidationException", "Where clause does not contain a mandatory equality on all key attributes");

    /// <summary>
    /// On a composite-key table, a PartiQL UPDATE whose WHERE omits the sort key is a
    /// ValidationException naming the missing key equality, and no item is created.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.update.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-update-missing-sort-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbupdwc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        await ddb.PutItemAsync(table, Item(("pk", S("x")), ("sk", S("y")), ("a", N("0"))));

        // The WHERE clause names the hash key but omits the sort key: AWS rejects the whole
        // statement rather than treating it as a prefix match over the partition.
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{table}\" SET a=1 WHERE pk='x'" }),
            MissingKey.Code,
            MissingKey.Message);
        var unseeded = await Stored(ddb, table, "x", "y");
        Ensure(
            Py.Equal(unseeded, Item(("pk", S("x")), ("sk", S("y")), ("a", N("0")))),
            $"a rejected UPDATE changed the existing item: {Py.Repr(unseeded)}");
        var partition = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("x"))),
            ConsistentRead = true,
        })).Items;
        Ensure(
            Py.Equal(partition, new[] { unseeded }),
            $"a rejected UPDATE created another item under partition key 'x': {Py.Repr(partition)}");

        // Positive control: the identical SET succeeds once the WHERE names both key attributes.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{table}\" SET a=1 WHERE pk='x' AND sk='y'" });
        var updated = await Stored(ddb, table, "x", "y");
        Ensure(
            Py.Equal(updated, Item(("pk", S("x")), ("sk", S("y")), ("a", N("1")))),
            $"UPDATE with both key attributes in WHERE did not land: {Py.Repr(updated)}");
    }

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected ExecuteStatement to be rejected, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == code, $"expected {Py.Repr(code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk, string sk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S(sk))), ConsistentRead = true })).Item;
}
