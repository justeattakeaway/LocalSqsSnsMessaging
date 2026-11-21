// Translated from behavioral/dynamodb/check_query_filter_dangling_logical_operator_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-dangling-logical-operator-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterDanglingLogicalOperatorRejected
{
    private const string Check = "query-filter-dangling-logical-operator-rejected";
    private const string ErrValidation = "ValidationException";
    private const string Pk = "pk";
    private const string Sk = "sk";
    private const string Part = "p1";

    /// <summary>
    /// A Query FilterExpression rejects a dangling AND and a stray repeated AND as grammar
    /// syntax errors, each with its own exact message; the legal filter still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-dangling-logical-operator-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement(Pk, KeyType.HASH), new KeySchemaElement(Sk, KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition(Pk, ScalarAttributeType.S), new AttributeDefinition(Sk, ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item((Pk, S(Part)), (Sk, S("i1")), ("status", S("active"))));

        QueryRequest Base(string filter) => new()
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = Names(("#pk", Pk), ("#s", "status")),
            ExpressionAttributeValues = Item((":pk", S(Part)), (":a", S("active"))),
            FilterExpression = filter,
        };

        // (1) A dangling AND with nothing after it -- a grammar syntax error, not a semantic one.
        var (code, message) = await Error(() => ddb.QueryAsync(Base("#s = :a AND")));
        Ensure(code == ErrValidation, $"dangling AND answered {Py.Repr(code)} ({Py.Repr(message)}), expected {ErrValidation}");
        const string expectedDangling = "Invalid FilterExpression: Syntax error; token: \"<EOF>\", near: \"AND\"";
        Ensure(message == expectedDangling, $"expected message {Py.Repr(expectedDangling)}, got {Py.Repr(message)}");

        // (2) A stray repeated AND -- a token the grammar never accepts there.
        var (code2, message2) = await Error(() => ddb.QueryAsync(Base("#s = :a AND AND #s = :a")));
        Ensure(code2 == ErrValidation, $"stray AND answered {Py.Repr(code2)} ({Py.Repr(message2)}), expected {ErrValidation}");
        const string expectedStray = "Invalid FilterExpression: Syntax error; token: \"AND\", near: \"AND AND #s\"";
        Ensure(message2 == expectedStray, $"expected message {Py.Repr(expectedStray)}, got {Py.Repr(message2)}");

        // Positive control: the legal filter over the same partition still works after both
        // rejections, and neither reached the table.
        var control = await ddb.QueryAsync(Base("#s = :a"));
        Ensure(control.Count == 1, $"expected Count 1 for the legal filter, got {control.Count}");
        Ensure(
            Py.Equal(control.Items, new[] { Item((Pk, S(Part)), (Sk, S("i1")), ("status", S("active"))) }),
            $"expected only the original item, got {Py.Repr(control.Items)}");
    }

    /// <summary>The (code, message) of the error <paramref name="fn"/> raises. Raises when it unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {Py.Repr(result)}");
    }
}
