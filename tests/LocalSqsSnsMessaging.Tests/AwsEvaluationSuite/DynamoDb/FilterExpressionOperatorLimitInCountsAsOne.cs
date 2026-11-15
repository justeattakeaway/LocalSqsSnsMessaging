// Translated from behavioral/dynamodb/check_filter_expression_operator_limit_in_counts_as_one.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.filter-expression-operator-limit-in-counts-as-one</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterExpressionOperatorLimitInCountsAsOne
{
    private const string CheckId = "filter-expression-operator-limit-in-counts-as-one";

    /// <summary>
    /// A FilterExpression with 301 operators is rejected naming the count; 299 is accepted.
    ///
    /// An IN clause with many operands still counts as exactly one operator, so swapping one
    /// comparison for a 100-operand IN leaves the counts, and so the verdicts, unmoved.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.filter-expression-operator-limit-in-counts-as-one")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("x")), ("a", S("v"))));
        static Dictionary<string, string> NamesA() => Names(("#a", "a"));

        var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = EqualsChain(151),
            ExpressionAttributeNames = NamesA(),
            ExpressionAttributeValues = Item((":v", S("v"))),
        }));
        const string expected = "Invalid FilterExpression: The expression contains too many operators; operator count: 301";
        Ensure(
            (code, message) == ("ValidationException", expected),
            $"a 301-operator FilterExpression raised {Py.Repr((code, message))}, expected {Py.Repr(("ValidationException", expected))}");

        var accepted = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = EqualsChain(150),
            ExpressionAttributeNames = NamesA(),
            ExpressionAttributeValues = Item((":v", S("v"))),
        });
        Ensure(accepted.Count == 1, $"a 299-operator FilterExpression matched {Py.Repr(accepted.Count)}, expected 1");

        var (expr301ViaIn, values301) = ChainWithIn(151, 100);
        var (code2, message2) = await Error(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = expr301ViaIn,
            ExpressionAttributeNames = NamesA(),
            ExpressionAttributeValues = values301,
        }));
        Ensure(
            (code2, message2) == ("ValidationException", expected),
            $"151 clauses with a 100-operand IN raised {Py.Repr((code2, message2))}, expected the same 301-operator refusal {Py.Repr(("ValidationException", expected))} — an IN must count as one operator");

        var (expr299ViaIn, values299) = ChainWithIn(150, 100);
        var acceptedIn = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = expr299ViaIn,
            ExpressionAttributeNames = NamesA(),
            ExpressionAttributeValues = values299,
        });
        Ensure(
            acceptedIn.Count == 0,
            $"150 clauses with a 100-operand IN matched {Py.Repr(acceptedIn.Count)}, expected 0 (none of the IN operands equal the stored value)");
    }

    /// <summary>`n` `#a = :v` comparisons AND'd together: `2n-1` operators.</summary>
    private static string EqualsChain(int n) => string.Join(" AND ", Enumerable.Repeat("#a = :v", n));

    /// <summary>`n` clauses AND'd where the last is an IN of `in_operands` values: still `2n-1` operators.</summary>
    private static (string Expression, Dictionary<string, AttributeValue> Values) ChainWithIn(int n, int inOperands)
    {
        var values = Item((":v", S("v")));
        var clauses = Enumerable.Repeat("#a = :v", n - 1).ToList();
        var operands = Enumerable.Range(0, inOperands).Select(i => string.Create(CultureInfo.InvariantCulture, $":w{i}")).ToList();
        clauses.Add("#a IN (" + string.Join(", ", operands) + ")");
        for (var i = 0; i < operands.Count; i++)
        {
            values[operands[i]] = S(string.Create(CultureInfo.InvariantCulture, $"other{i}"));
        }

        return (string.Join(" AND ", clauses), values);
    }

    /// <summary>(code, message) of the ClientError `fn()` raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }
}
