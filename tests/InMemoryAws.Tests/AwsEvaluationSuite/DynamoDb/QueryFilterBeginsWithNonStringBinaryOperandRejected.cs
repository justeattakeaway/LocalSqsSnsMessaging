// Translated from behavioral/dynamodb/check_query_filter_begins_with_non_string_binary_operand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-begins-with-non-string-binary-operand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterBeginsWithNonStringBinaryOperandRejected
{
    private const string Check = "query-filter-begins-with-non-string-binary-operand-rejected";

    private static string InvalidOperandMessage(string opType) =>
        "Invalid FilterExpression: Incorrect operand type for operator or function; " +
        $"operator or function: begins_with, operand type: {opType}";

    /// <summary>
    /// Query's FilterExpression refuses begins_with() on an N or BOOL operand with
    /// ValidationException, over a populated partition, without changing what a correctly typed
    /// filter over the same partition returns.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-begins-with-non-string-binary-operand-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-ddbqfbwr-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("alpha-item")), ("attr", S("alphabet"))));
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("zeta-item")), ("attr", S("zebra"))));

        Task<QueryResponse> QueryBeginsWith(AttributeValue operandValue) => ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "#pk = :pk",
            FilterExpression = "begins_with(#a, :v)",
            ExpressionAttributeNames = Names(("#pk", "pk"), ("#a", "attr")),
            ExpressionAttributeValues = Item((":pk", S("p1")), (":v", operandValue)),
            ConsistentRead = true,
        });

        var (code, message) = await Error(() => QueryBeginsWith(N("1")));
        Ensure(
            code == "ValidationException",
            $"begins_with with an N operand answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(
            message == InvalidOperandMessage("N"),
            $"begins_with with an N operand said {Py.Repr(message)}, expected " +
            $"{Py.Repr(InvalidOperandMessage("N"))}");

        (code, message) = await Error(() => QueryBeginsWith(Bool(true)));
        Ensure(
            code == "ValidationException",
            $"begins_with with a BOOL operand answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(
            message == InvalidOperandMessage("BOOL"),
            $"begins_with with a BOOL operand said {Py.Repr(message)}, expected " +
            $"{Py.Repr(InvalidOperandMessage("BOOL"))}");

        // The rejected calls wrote nothing: the partition still holds exactly the two seeded items.
        var unfiltered = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = Names(("#pk", "pk")),
            ExpressionAttributeValues = Item((":pk", S("p1"))),
            ConsistentRead = true,
        });
        Ensure(
            Ids(unfiltered.Items).SetEquals(["alpha-item", "zeta-item"]),
            $"after the rejected begins_with calls the partition held {Repr(Ids(unfiltered.Items))}, " +
            "expected the two seeded items unchanged");

        // Positive control: begins_with over the correctly typed S operand still filters normally.
        var matched = await QueryBeginsWith(S("alpha"));
        Ensure(
            Ids(matched.Items).SetEquals(["alpha-item"]),
            $"begins_with(attr, (S)'alpha') matched {Repr(Ids(matched.Items))}, expected " +
            "{'alpha-item'}");
    }

    private static HashSet<string> Ids(List<Dictionary<string, AttributeValue>> items) => items.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal);

    /// <summary>The repr of the frozenset <c>_ids</c> returns.</summary>
    private static string Repr(HashSet<string> ids) =>
        ids.Count == 0 ? "frozenset()" : "frozenset({" + string.Join(", ", ids.Select(Py.Repr)) + "})";

    /// <summary>(code, message) of the error <paramref name="fn"/> raises; raises if it unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
