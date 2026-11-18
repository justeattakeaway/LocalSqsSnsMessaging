// Translated from behavioral/dynamodb/check_legacy_queryfilter_scanfilter_keyconditions_argument_rules.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-queryfilter-scanfilter-keyconditions-argument-rules</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyQueryfilterScanfilterKeyconditionsArgumentRules
{
    private const string CheckId = "legacy-queryfilter-scanfilter-keyconditions-argument-rules";
    private const string ErrValidation = "ValidationException";

    // (ComparisonOperator, AttributeValueList length given, required length) for each operator
    // this check exercises: EQ takes exactly 1, BETWEEN exactly 2, NULL exactly 0.
    private static (string Op, Func<List<AttributeValue>> BadValues)[] ArityCases() =>
    [
        ("EQ", () => [S("Item-0"), S("Item-1")]),
        ("BETWEEN", () => [S("Item-0")]),
        ("NULL", () => [S("Item-0")]),
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

    private static Condition PkEq() => new() { AttributeValueList = [S("p1")], ComparisonOperator = ComparisonOperator.EQ };

    private static Dictionary<string, Condition> Conditions(params (string Name, Condition Condition)[] entries)
    {
        var result = new Dictionary<string, Condition>(StringComparer.Ordinal);
        foreach (var (name, condition) in entries)
        {
            result[name] = condition;
        }

        return result;
    }

    /// <summary>
    /// A legacy QueryFilter/ScanFilter/KeyConditions entry with the wrong AttributeValueList
    /// length for its operator, or a ConditionalOperator with no filter, is ValidationException
    /// with AWS's exact message; a QueryFilter and its FilterExpression twin agree.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.QueryFilter.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-queryfilter-scanfilter-keyconditions-argument-rules")]
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
        await ddb.WaitForTableExists(t, delaySeconds: 1, maxAttempts: 60);
        for (var i = 0; i < 3; i++)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S($"s{i}")), ("name", S($"Item-{i}"))));
        }

        foreach (var (op, badValues) in ArityCases())
        {
            var want = $"One or more parameter values were invalid: Invalid number of argument(s) for the {op} ComparisonOperator";

            var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
            {
                TableName = t,
                KeyConditions = Conditions(("pk", PkEq())),
                QueryFilter = Conditions(("name", new Condition { AttributeValueList = badValues(), ComparisonOperator = op })),
            }));
            Ensure(code == ErrValidation, $"QueryFilter/{op}: expected {ErrValidation}, got {code}");
            Ensure(message == want, $"QueryFilter/{op}: expected AWS exact message, got {Py.Repr(message)}");

            (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
            {
                TableName = t,
                ScanFilter = Conditions(("name", new Condition { AttributeValueList = badValues(), ComparisonOperator = op })),
            }));
            Ensure(code == ErrValidation, $"ScanFilter/{op}: expected {ErrValidation}, got {code}");
            Ensure(message == want, $"ScanFilter/{op}: expected AWS exact message, got {Py.Repr(message)}");

            (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
            {
                TableName = t,
                KeyConditions = Conditions(("pk", PkEq()), ("sk", new Condition { AttributeValueList = badValues(), ComparisonOperator = op })),
            }));
            Ensure(code == ErrValidation, $"KeyConditions/{op}: expected {ErrValidation}, got {code}");
            Ensure(message == want, $"KeyConditions/{op}: expected AWS exact message, got {Py.Repr(message)}");
        }

        {
            var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
            {
                TableName = t,
                KeyConditions = Conditions(("pk", PkEq())),
                ConditionalOperator = ConditionalOperator.OR,
            }));
            Ensure(code == ErrValidation, $"Query ConditionalOperator w/o filter: expected {ErrValidation}, got {code}");
            Ensure(
                message == "ConditionalOperator cannot be used without Filter or Expected",
                $"Query ConditionalOperator w/o filter: expected AWS exact message, got {Py.Repr(message)}");

            (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest { TableName = t, ConditionalOperator = ConditionalOperator.OR }));
            Ensure(code == ErrValidation, $"Scan ConditionalOperator w/o filter: expected {ErrValidation}, got {code}");
            Ensure(
                message == "ConditionalOperator cannot be used without Filter or Expected",
                $"Scan ConditionalOperator w/o filter: expected AWS exact message, got {Py.Repr(message)}");
        }

        var legacy = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditions = Conditions(("pk", PkEq())),
            QueryFilter = Conditions(("name", new Condition { AttributeValueList = [S("Item-1")], ComparisonOperator = ComparisonOperator.EQ })),
            ConsistentRead = true,
        });
        var expr = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "pk = :p",
            FilterExpression = "#n = :n",
            ExpressionAttributeNames = Names(("#n", "name")),
            ExpressionAttributeValues = Item((":p", S("p1")), (":n", S("Item-1"))),
            ConsistentRead = true,
        });
        Ensure(legacy.Count == 1 && expr.Count == 1, $"legacy and expression forms disagree on count: {legacy.Count} vs {expr.Count}");
        Ensure(Py.Equal(legacy.Items, expr.Items), $"legacy QueryFilter and its FilterExpression twin returned different items: {Py.Repr(legacy.Items)} vs {Py.Repr(expr.Items)}");
        Ensure(
            legacy.ScannedCount == expr.ScannedCount && expr.ScannedCount == 3,
            $"both forms must scan all 3 items under pk=p1 before filtering, got {legacy.ScannedCount} and {expr.ScannedCount}");
    }
}
