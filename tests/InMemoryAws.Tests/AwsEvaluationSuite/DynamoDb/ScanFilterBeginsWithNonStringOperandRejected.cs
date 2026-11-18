// Translated from behavioral/dynamodb/check_scan_filter_begins_with_non_string_operand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-begins-with-non-string-operand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterBeginsWithNonStringOperandRejected
{
    private const string Check = "scan-filter-begins-with-non-string-operand-rejected";

    private static string InvalidOperandMessage(string opType) =>
        "Invalid FilterExpression: Incorrect operand type for operator or function; " +
        $"operator or function: begins_with, operand type: {opType}";

    /// <summary>(code, message) of the ClientError <paramref name="fn"/> raises; RAISES if it unexpectedly succeeds.</summary>
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

    /// <summary>Wait for the table to become ACTIVE, bounded by the check's own remaining budget.</summary>
    private static Task WaitTableActive(IAmazonDynamoDB ddb, FidelityContext ctx, string name)
    {
        var left = ctx.TimeLeftSeconds;
        var budget = Math.Max(10.0, Math.Min(120.0, (left == 0 ? 120.0 : left) - 10.0));
        const int delay = 2;
        var attempts = Math.Max(1, (int)(budget / delay));
        return ddb.WaitForTableExists(name, delaySeconds: delay, maxAttempts: attempts);
    }

    /// <summary>
    /// A Scan FilterExpression refuses begins_with on an N or BOOL operand with
    /// ValidationException, identically on an empty table and a populated one, and
    /// scans nothing into existence while doing so.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-begins-with-non-string-operand-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-behav", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await WaitTableActive(ddb, ctx, t);

        Task<ScanResponse> ScanBeginsWith(AttributeValue operandValue) => ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            FilterExpression = "begins_with(attr, :v)",
            ExpressionAttributeValues = Item((":v", operandValue)),
            ConsistentRead = true,
        });

        foreach (var stage in new[] { "empty", "populated" })
        {
            if (stage == "populated")
            {
                await ddb.PutItemAsync(t, Item(("pk", S("alpha-item")), ("attr", S("alphabet"))));
                await ddb.PutItemAsync(t, Item(("pk", S("zeta-item")), ("attr", S("zebra"))));
            }

            var (code, message) = await Error(() => ScanBeginsWith(N("1")));
            Ensure(code == "ValidationException", $"begins_with with an N operand on the {stage} table answered {Py.Repr(code)}, expected ValidationException");
            Ensure(
                message == InvalidOperandMessage("N"),
                $"begins_with with an N operand on the {stage} table said {Py.Repr(message)}, expected {Py.Repr(InvalidOperandMessage("N"))}");
            (code, message) = await Error(() => ScanBeginsWith(Bool(true)));
            Ensure(code == "ValidationException", $"begins_with with a BOOL operand on the {stage} table answered {Py.Repr(code)}, expected ValidationException");
            Ensure(
                message == InvalidOperandMessage("BOOL"),
                $"begins_with with a BOOL operand on the {stage} table said {Py.Repr(message)}, expected {Py.Repr(InvalidOperandMessage("BOOL"))}");
            var unfiltered = await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true });
            var expectedCount = stage == "empty" ? 0 : 2;
            Ensure(
                unfiltered.Count == expectedCount,
                $"after the rejected begins_with scans the {stage} table held {unfiltered.Count} item(s), expected {expectedCount}: the rejected reads must not have written or removed anything");
        }

        // Positive control: begins_with over the correctly-typed S operand still filters normally.
        var matched = await ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            FilterExpression = "begins_with(attr, :p)",
            ExpressionAttributeValues = Item((":p", S("alpha"))),
            ConsistentRead = true,
        });
        var matchedPks = matched.Items.Select(item => item["pk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            matchedPks.SetEquals(["alpha-item"]),
            $"begins_with(attr, 'alpha') with a correctly-typed S operand matched {{{string.Join(", ", matchedPks.Select(p => Py.Repr(p)))}}}, expected {{'alpha-item'}}");
    }
}
