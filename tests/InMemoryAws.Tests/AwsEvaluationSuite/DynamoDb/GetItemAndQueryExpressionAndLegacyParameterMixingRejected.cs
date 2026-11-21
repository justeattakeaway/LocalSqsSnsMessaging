// Translated from behavioral/dynamodb/check_get_item_and_query_expression_and_legacy_parameter_mixing_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.get-item-and-query-expression-and-legacy-parameter-mixing-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetItemAndQueryExpressionAndLegacyParameterMixingRejected
{
    private const string Check = "get-item-and-query-expression-and-legacy-parameter-mixing-rejected";

    private static readonly (string Code, string Message) MixGetItem = (
        "ValidationException",
        "Can not use both expression and non-expression parameters in the same request: " +
        "Non-expression parameters: {AttributesToGet} Expression parameters: {ProjectionExpression}");

    private static readonly (string Code, string Message) MixQuery = (
        "ValidationException",
        "Can not use both expression and non-expression parameters in the same request: " +
        "Non-expression parameters: {QueryFilter} Expression parameters: {KeyConditionExpression}");

    /// <summary>
    /// GetItem refuses AttributesToGet with ProjectionExpression, and Query refuses QueryFilter
    /// with KeyConditionExpression, each with ValidationException naming both parameters; the
    /// single-dialect form of each still works, and the item is unchanged after both refusals.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.get-item-and-query-expression-and-legacy-parameter-mixing-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbmix-");
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

        var item1 = Item(("pk", S("p1")), ("sk", S("s1")), ("a", S("1")), ("flag", S("keep")));
        var item2 = Item(("pk", S("p1")), ("sk", S("s2")), ("a", S("2")), ("flag", S("drop")));
        await ddb.PutItemAsync(table, item1);
        await ddb.PutItemAsync(table, item2);
        Dictionary<string, AttributeValue> Key1() => Item(("pk", S("p1")), ("sk", S("s1")));

        // GetItem: AttributesToGet and ProjectionExpression together are refused.
        await ExpectError(
            () => ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key1(), AttributesToGet = ["a"], ProjectionExpression = "a" }),
            MixGetItem);

        // Legal neighbour 1: ProjectionExpression alone projects to exactly {'a'}.
        var byExpr = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key1(), ProjectionExpression = "a", ConsistentRead = true });
        Ensure(
            Py.Equal(byExpr.Item, Item(("a", S("1")))),
            $"ProjectionExpression alone: expected only {{'a'}}, got {Py.Repr(byExpr.Item)}");

        // Legal neighbour 2: AttributesToGet alone projects to the same exact {'a'}.
        var byLegacy = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key1(), AttributesToGet = ["a"], ConsistentRead = true });
        Ensure(
            Py.Equal(byLegacy.Item, Item(("a", S("1")))),
            $"AttributesToGet alone: expected only {{'a'}}, got {Py.Repr(byLegacy.Item)}");

        // Query: QueryFilter and KeyConditionExpression together are refused.
        await ExpectError(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk",
                ExpressionAttributeValues = Item((":pk", S("p1"))),
                QueryFilter = new Dictionary<string, Condition>
                {
                    ["flag"] = new Condition { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [S("keep")] },
                },
            }),
            MixQuery);

        // Legal neighbour: KeyConditionExpression plus FilterExpression, both expression-dialect,
        // narrows the two items sharing pk=p1 down to the one matching the filter.
        var filtered = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            FilterExpression = "flag = :f",
            ExpressionAttributeValues = Item((":pk", S("p1")), (":f", S("keep"))),
            ConsistentRead = true,
        });
        Ensure(filtered.Count == 1, $"expected exactly 1 matching item, got Count={Py.Repr(filtered.Count)}");
        Ensure(Py.Equal(filtered.Items, new[] { item1 }), $"expected only {Py.Repr(item1)}, got {Py.Repr(filtered.Items)}");

        // Neither refusal mutated the item: a consistent read shows it exactly as written.
        var after = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key1(), ConsistentRead = true });
        Ensure(Py.Equal(after.Item, item1), $"expected the item unchanged after both refusals, got {Py.Repr(after.Item)}");
    }

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }
}
