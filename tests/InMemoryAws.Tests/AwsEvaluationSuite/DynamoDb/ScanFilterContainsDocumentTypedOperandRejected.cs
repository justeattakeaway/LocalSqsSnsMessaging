// Translated from behavioral/dynamodb/check_scan_filter_contains_document_typed_operand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-contains-document-typed-operand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterContainsDocumentTypedOperandRejected
{
    private const string Check = "scan-filter-contains-document-typed-operand-rejected";
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

    private static Dictionary<string, Condition> Contains(AttributeValue operand) =>
        new() { ["coll"] = new Condition { ComparisonOperator = ComparisonOperator.CONTAINS, AttributeValueList = [operand] } };

    /// <summary>
    /// Legacy ScanFilter CONTAINS refuses an M- or L-typed AttributeValueList entry with
    /// ValidationException naming that document type, while a BOOL or NULL operand is accepted
    /// and a matching scalar operand still finds its item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-contains-document-typed-operand-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbdoc-");
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
        await ddb.PutItemAsync(t, Item(("pk", S("i2")), ("coll", S("needle"))));

        foreach (var (docType, docValue) in new[] { ("M", M(("k", S("a")))), ("L", L(S("a"))) })
        {
            var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
            {
                TableName = t,
                ScanFilter = Contains(docValue),
            }));
            Ensure(
                code == Validation,
                $"CONTAINS with a {docType} operand: expected {Validation}, got {code}: {Py.Repr(message)}");
            var want = $"One or more parameter values were invalid: ComparisonOperator CONTAINS is not valid for {docType} AttributeValue type";
            Ensure(
                message == want,
                $"CONTAINS with a {docType} operand: expected message {Py.Repr(want)}, got {Py.Repr(message)}");
        }

        foreach (var scalarValue in new[] { Bool(true), Null() })
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = t,
                ConsistentRead = true,
                ScanFilter = Contains(scalarValue),
            });
            Ensure(
                resp.Count == 0,
                $"CONTAINS with operand {Py.Repr(scalarValue)} matched {resp.Count} items, expected 0 -- it is accepted, not rejected, but matches nothing on a string-set attribute");
        }

        var found = await ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            ConsistentRead = true,
            ScanFilter = Contains(S("needle")),
        });
        Ensure(
            found.Items.Select(i => i["pk"].S).ToHashSet(StringComparer.Ordinal).SetEquals(["i2"]),
            $"CONTAINS with a matching S operand returned {Py.Repr(found.Items)}, expected only item i2");
    }
}
