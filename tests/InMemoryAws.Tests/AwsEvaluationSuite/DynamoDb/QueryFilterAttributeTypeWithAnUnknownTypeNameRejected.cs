// Translated from behavioral/dynamodb/check_query_filter_attribute_type_with_an_unknown_type_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-attribute-type-with-an-unknown-type-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterAttributeTypeWithAnUnknownTypeNameRejected
{
    private const string Check = "query-filter-attribute-type-with-an-unknown-type-name-rejected";

    /// <summary>
    /// The ten DynamoDB attribute type descriptors; attribute_type's second operand is checked against
    /// exactly this closed set.
    /// </summary>
    private static readonly HashSet<string> ValidTypes = new(["S", "SS", "N", "NS", "B", "BS", "BOOL", "NULL", "L", "M"], StringComparer.Ordinal);

    /// <summary>
    /// A Query FilterExpression attribute_type's type operand is matched case-sensitively against
    /// the ten type descriptors; a name outside that set is refused and the legal form still works.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-attribute-type-with-an-unknown-type-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-qdatypneg-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("a")), ("attr", N("1"))));

        async Task Rejected(string typeName)
        {
            var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :p",
                FilterExpression = "attribute_type(#a, :t)",
                ExpressionAttributeNames = Names(("#a", "attr")),
                ExpressionAttributeValues = Item((":p", S("p1")), (":t", S(typeName))),
            }));
            Ensure(
                code == "ValidationException",
                $"{Py.Repr(typeName)} raised {Py.Repr(code)} ({Py.Repr(message)}), expected 'ValidationException'");
            var wantHead =
                $"Invalid FilterExpression: Invalid attribute type name found; type: {typeName}, " +
                "valid types: {";
            Ensure(
                message.StartsWith(wantHead, StringComparison.Ordinal) && message.EndsWith('}'),
                $"{Py.Repr(typeName)} raised message {Py.Repr(message)}, expected it wrapped by {Py.Repr(wantHead)} and '}}'");

            // The ten type names are fixed, but their listed order is not: the set, not the string, is
            // the invariant.
            Ensure(
                message[wantHead.Length..^1].Split(',').ToHashSet(StringComparer.Ordinal).SetEquals(ValidTypes),
                $"{Py.Repr(typeName)} raised message {Py.Repr(message)}, expected it to name exactly " +
                $"{Py.Repr(ValidTypes.Order(StringComparer.Ordinal).ToList())}");
        }

        await Rejected("XX");

        // "n" is a real type letter, lower-cased: the ten descriptors are matched case-sensitively, not
        // folded, so a real type name spelled wrong is refused exactly like a wholly unknown one.
        await Rejected("n");

        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            FilterExpression = "attribute_type(#a, :t)",
            ExpressionAttributeNames = Names(("#a", "attr")),
            ExpressionAttributeValues = Item((":p", S("p1")), (":t", S("N"))),
            ConsistentRead = true,
        });
        Ensure(control.Count == 1, $"attribute_type(attr, N) matched {Py.Repr(control.Count)}, expected 1");
        Ensure(
            Py.Equal(control.Items, new[] { Item(("pk", S("p1")), ("sk", S("a")), ("attr", N("1"))) }),
            $"the legal Query returned {Py.Repr(control.Items)}, expected the original item unchanged");
    }

    /// <summary>The (code, message) of the error fn() raises. RAISES when fn() unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message ?? "");
        }

        throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {Py.Repr(result)}");
    }
}
