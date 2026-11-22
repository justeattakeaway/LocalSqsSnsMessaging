// Translated from behavioral/dynamodb/check_query_filter_expression_in_operator_empty_list_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-expression-in-operator-empty-list-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterExpressionInOperatorEmptyListRejected
{
    private const string Check = "query-filter-expression-in-operator-empty-list-rejected";

    /// <summary>
    /// A Query FilterExpression's IN with an empty operand list is rejected with
    /// ValidationException before any item is examined, while a one-value list parses and selects
    /// the matching item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-expression-in-operator-empty-list-rejected")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("i1")), ("score", N("9"))));

        // Positive control: a one-value IN list parses and selects the matching item.
        var ok = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "#pk = :pk",
            FilterExpression = "#sc IN (:a)",
            ExpressionAttributeNames = Names(("#pk", "pk"), ("#sc", "score")),
            ExpressionAttributeValues = Item((":pk", S("p1")), (":a", N("9"))),
        });
        var got = ok.Items.Select(i => i["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(got.SequenceEqual(["i1"]), $"the one-value IN control returned {Py.Repr(got)}, expected ['i1']");

        var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "#pk = :pk",
            FilterExpression = "#sc IN ()",
            ExpressionAttributeNames = Names(("#pk", "pk"), ("#sc", "score")),
            ExpressionAttributeValues = Item((":pk", S("p1"))),
        }));
        var emptyIn = ("ValidationException", "Invalid FilterExpression: Syntax error; token: \")\", near: \"()\"");
        Ensure((code, message) == emptyIn, $"empty IN list was refused with {Py.Repr((code, message))}, expected {Py.Repr(emptyIn)}");

        // The item is unaffected by the rejected filter: a direct read still returns it unchanged.
        var after = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p1")), ("sk", S("i1"))), ConsistentRead = true });
        Ensure(
            Py.Equal(after.Item["score"], N("9")),
            $"item score after the rejected Query was {Py.Repr(after.Item["score"])}, expected unchanged {{'N': '9'}}");
    }

    /// <summary>The (code, message) <paramref name="fn"/> is refused with. An unexpected success raises, naming it.</summary>
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
