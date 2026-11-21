// Translated from behavioral/dynamodb/check_scan_filter_legacy_contains_set_typed_operand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-legacy-contains-set-typed-operand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterLegacyContainsSetTypedOperandRejected
{
    private const string Check = "scan-filter-legacy-contains-set-typed-operand-rejected";
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

    /// <summary>
    /// Legacy ScanFilter CONTAINS and NOT_CONTAINS refuse a Set-typed (SS, NS or BS)
    /// AttributeValueList entry with ValidationException; a scalar operand still matches.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-legacy-contains-set-typed-operand-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbset-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(t, Item(("pk", S("i1")), ("coll", SS("a", "b"))));

        foreach (var op in new[] { "CONTAINS", "NOT_CONTAINS" })
        {
            foreach (var (setType, setValue) in new (string, Func<AttributeValue>)[]
            {
                ("SS", () => SS("a")),
                ("NS", () => NS("1")),
                ("BS", () => BS("a"u8.ToArray())),
            })
            {
                var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
                {
                    TableName = t,
                    ScanFilter = new Dictionary<string, Condition>
                    {
                        ["coll"] = new() { ComparisonOperator = op, AttributeValueList = [setValue()] },
                    },
                }));
                Ensure(
                    code == Validation,
                    $"{op} with a {setType} operand: expected {Validation}, got {code}: {Py.Repr(message)}");
                var want =
                    $"One or more parameter values were invalid: ComparisonOperator {op} is not " +
                    $"valid for {setType} AttributeValue type";
                Ensure(
                    message == want,
                    $"{op} with a {setType} operand: expected message {Py.Repr(want)}, got {Py.Repr(message)}");
            }
        }

        // Positive control: the same attribute under a scalar S operand still matches, proving the
        // refusals above are about the operand's type, not a broken table or filter.
        var resp = await ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            ConsistentRead = true,
            ScanFilter = new Dictionary<string, Condition>
            {
                ["coll"] = new() { ComparisonOperator = ComparisonOperator.CONTAINS, AttributeValueList = [S("a")] },
            },
        });
        var ids = resp.Items.Select(i => i["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(ids.SequenceEqual(["i1"]), $"CONTAINS with a scalar S operand: expected ['i1'], got {Py.Repr(ids)}");
    }
}
