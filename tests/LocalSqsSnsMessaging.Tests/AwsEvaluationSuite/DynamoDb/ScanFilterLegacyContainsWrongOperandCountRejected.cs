// Translated from behavioral/dynamodb/check_scan_filter_legacy_contains_wrong_operand_count_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-legacy-contains-wrong-operand-count-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterLegacyContainsWrongOperandCountRejected
{
    private const string Check = "scan-filter-legacy-contains-wrong-operand-count-rejected";
    private const string Validation = "ValidationException";

    /// <summary>The Error.Code and Error.Message <paramref name="fn"/> raises. Raises on an unexpected success.</summary>
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

    private static Dictionary<string, Condition> Contains(params AttributeValue[] values) =>
        new(StringComparer.Ordinal) { ["tag"] = new Condition { AttributeValueList = [.. values], ComparisonOperator = ComparisonOperator.CONTAINS } };

    /// <summary>
    /// Legacy ScanFilter CONTAINS with zero or two AttributeValueList entries is
    /// ValidationException with AWS's exact message; CONTAINS with exactly one entry still
    /// matches the substring.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.ScanFilter.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-legacy-contains-wrong-operand-count-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbcwc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(name, Item(("id", S("r1")), ("tag", S("banana"))));
        const string want =
            "One or more parameter values were invalid: Invalid number of argument(s) for the " +
            "CONTAINS ComparisonOperator";
        var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest { TableName = name, ScanFilter = Contains() }));
        Ensure(code == Validation, $"CONTAINS with 0 values: expected {Validation}, got {code}");
        Ensure(message == want, $"CONTAINS with 0 values: expected {Py.Repr(want)}, got {Py.Repr(message)}");
        (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest { TableName = name, ScanFilter = Contains(S("an"), S("na")) }));
        Ensure(code == Validation, $"CONTAINS with 2 values: expected {Validation}, got {code}");
        Ensure(message == want, $"CONTAINS with 2 values: expected {Py.Repr(want)}, got {Py.Repr(message)}");
        var resp = await ddb.ScanAsync(new ScanRequest { TableName = name, ScanFilter = Contains(S("an")), ConsistentRead = true });
        var ids = resp.Items.Select(i => i["id"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(ids.SequenceEqual(["r1"]), $"CONTAINS with exactly 1 value: expected ['r1'], got {Py.Repr(ids)}");
    }
}
