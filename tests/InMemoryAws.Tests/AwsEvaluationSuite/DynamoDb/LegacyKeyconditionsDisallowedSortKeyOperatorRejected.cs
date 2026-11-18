// Translated from behavioral/dynamodb/check_legacy_keyconditions_disallowed_sort_key_operator_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-keyconditions-disallowed-sort-key-operator-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyKeyconditionsDisallowedSortKeyOperatorRejected
{
    private const string CheckId = "legacy-keyconditions-disallowed-sort-key-operator-rejected";
    private const string ErrValidation = "ValidationException";

    // A sort-key ComparisonOperator outside EQ/LE/LT/GE/GT/BEGINS_WITH/BETWEEN, each with an arity
    // that is otherwise valid for the operator so only the operator itself is under test.
    private static (string Op, Func<List<AttributeValue>> Values)[] DisallowedCases() =>
    [
        ("NE", () => [S("beta")]),
        ("CONTAINS", () => [S("e")]),
        ("NULL", () => []),
    ];

    /// <summary>The Error.Code and Error.Message <paramref name="fn"/> raises. Raises on an unexpected success.</summary>
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

    /// <summary>
    /// A legacy KeyConditions sort-key entry using a comparison operator other than
    /// EQ/LE/LT/GE/GT/BEGINS_WITH/BETWEEN is ValidationException with AWS's exact message, while
    /// the same partition key condition alone is accepted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-keyconditions-disallowed-sort-key-operator-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("alpha"))));
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("beta"))));

        Dictionary<string, Condition> PkCond() => new(StringComparer.Ordinal)
        {
            ["pk"] = new Condition { AttributeValueList = [S("p1")], ComparisonOperator = ComparisonOperator.EQ },
        };

        const string want = "One or more parameter values were invalid: Attempted conditional constraint is not an indexable operation";
        foreach (var (op, values) in DisallowedCases())
        {
            var conditions = PkCond();
            conditions["sk"] = new Condition { AttributeValueList = values(), ComparisonOperator = op };
            var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest { TableName = t, KeyConditions = conditions }));
            Ensure(code == ErrValidation, $"{op}: expected {ErrValidation}, got {Py.Repr(code)}");
            Ensure(message == want, $"{op}: expected AWS exact message, got {Py.Repr(message)}");
        }

        var positive = await ddb.QueryAsync(new QueryRequest { TableName = t, KeyConditions = PkCond(), ConsistentRead = true });
        Ensure(positive.Count == 2, $"positive control: expected Count=2, got {Py.Repr(positive.Count)}");
    }
}
