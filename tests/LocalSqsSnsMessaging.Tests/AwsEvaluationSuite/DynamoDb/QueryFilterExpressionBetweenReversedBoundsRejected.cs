// Translated from behavioral/dynamodb/check_query_filter_expression_between_reversed_bounds_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-expression-between-reversed-bounds-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterExpressionBetweenReversedBoundsRejected
{
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// A Query FilterExpression's BETWEEN with its lower bound above its upper bound is
    /// rejected with ValidationException before any item is examined, while the same bounds in
    /// ascending order select the matching items.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-expression-between-reversed-bounds-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var (sk, score) in new[] { ("i1", "5"), ("i2", "15"), ("i3", "30") })
        {
            await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S(sk)), ("score", N(score))));
        }

        QueryRequest Control() => new()
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :pk",
            FilterExpression = "#sc BETWEEN :lo AND :hi",
            ExpressionAttributeNames = Names(("#sc", "score")),
            ExpressionAttributeValues = Item((":pk", S("p1")), (":lo", N("10")), (":hi", N("25"))),
        };

        // Positive control: the same bounds in ascending order parse and select the one item
        // (score=15) that falls inside [10, 25].
        var ok = await ddb.QueryAsync(Control());
        var got = ok.Items.Select(i => i["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(got.SequenceEqual(["i2"]), $"the ascending-bounds control returned {Py.Repr(got)}, expected ['i2']");

        var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :pk",
            FilterExpression = "#sc BETWEEN :hi AND :lo",
            ExpressionAttributeNames = Names(("#sc", "score")),
            ExpressionAttributeValues = Item((":pk", S("p1")), (":lo", N("10")), (":hi", N("25"))),
        }));
        Ensure(code == ErrValidation, $"the reversed-bounds Query was refused with {Py.Repr(code)}, expected {Py.Repr(ErrValidation)}");
        const string expectedMessage =
            "Invalid FilterExpression: The BETWEEN operator requires upper bound to be greater " +
            "than or equal to lower bound; lower bound operand: AttributeValue: {N:25}, upper " +
            "bound operand: AttributeValue: {N:10}";
        Ensure(message == expectedMessage, $"unexpected message for the reversed BETWEEN: {Py.Repr(message)}");

        // After the rejection the table still answers the control exactly as before.
        var again = await ddb.QueryAsync(Control());
        var gotAgain = again.Items.Select(i => i["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(gotAgain.SequenceEqual(["i2"]), $"after the rejection the control returned {Py.Repr(gotAgain)}, expected ['i2']");
    }

    /// <summary>The (code, message) <paramref name="fn"/> is refused with. An unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}
