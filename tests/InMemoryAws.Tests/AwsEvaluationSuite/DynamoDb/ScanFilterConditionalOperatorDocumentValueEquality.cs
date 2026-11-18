// Translated from behavioral/dynamodb/check_scan_filter_conditional_operator_document_value_equality.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-conditional-operator-document-value-equality</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterConditionalOperatorDocumentValueEquality
{
    private const string Check = "scan-filter-conditional-operator-document-value-equality";

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static AttributeValue ListValue() => L(S("x"), N("1"));

    private static AttributeValue MapValue() => M(("a", S("y")), ("b", N("1")));

    private static Condition Eq(AttributeValue value) => new() { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [value] };

    /// <summary>
    /// ConditionalOperator AND/OR compose EQ on List and Map ScanFilter conditions by deep
    /// structural equality; any other ConditionalOperator value is rejected, naming AND as ALL.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-conditional-operator-document-value-equality")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-scancond-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 30);
        await ddb.PutItemAsync(table, Item(("id", S("list-match")), ("doc", ListValue())));
        await ddb.PutItemAsync(table, Item(("id", S("list-other")), ("doc", L(S("x"), N("2")))));
        await ddb.PutItemAsync(table, Item(("id", S("map-match")), ("doc", MapValue())));
        await ddb.PutItemAsync(table, Item(("id", S("map-other")), ("doc", M(("a", S("y")), ("b", N("2"))))));

        async Task<List<string>> ScanIds(Dictionary<string, Condition> scanFilter, ConditionalOperator? conditionalOperator = null)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                ScanFilter = scanFilter,
                ConditionalOperator = conditionalOperator,
            });
            return [.. resp.Items.Select(item => item["id"].S).Order(StringComparer.Ordinal)];
        }

        var eqList = await ScanIds(new() { ["doc"] = Eq(ListValue()) });
        Ensure(
            eqList.SequenceEqual(["list-match"]),
            $"ScanFilter EQ on an L value returned {Py.Repr(eqList)}, expected only the structurally identical List, excluding the one that differs by a nested element");
        var eqMap = await ScanIds(new() { ["doc"] = Eq(MapValue()) });
        Ensure(
            eqMap.SequenceEqual(["map-match"]),
            $"ScanFilter EQ on an M value returned {Py.Repr(eqMap)}, expected only the structurally identical Map, excluding the one that differs by a nested value");
        var andNarrowed = await ScanIds(new() { ["doc"] = Eq(ListValue()), ["id"] = Eq(S("list-other")) }, ConditionalOperator.AND);
        Ensure(
            andNarrowed.Count == 0,
            $"ConditionalOperator AND of a satisfiable condition and an unsatisfiable one returned {Py.Repr(andNarrowed)}, expected the empty intersection");
        var andMatched = await ScanIds(new() { ["doc"] = Eq(ListValue()), ["id"] = Eq(S("list-match")) }, ConditionalOperator.AND);
        Ensure(
            andMatched.SequenceEqual(["list-match"]),
            $"ConditionalOperator AND of two conditions both satisfied by the same item returned {Py.Repr(andMatched)}, expected that item alone");
        var orUnion = await ScanIds(new() { ["doc"] = Eq(MapValue()), ["id"] = Eq(S("list-other")) }, ConditionalOperator.OR);
        Ensure(
            orUnion.SequenceEqual(["list-other", "map-match"]),
            $"ConditionalOperator OR of two conditions satisfied by different items returned {Py.Repr(orUnion)}, expected the union of both");
        var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConditionalOperator = new ConditionalOperator("XOR"),
            ScanFilter = new() { ["doc"] = Eq(MapValue()), ["id"] = Eq(S("list-other")) },
        }));
        Ensure(code == "ValidationException", $"expected ValidationException for an unknown ConditionalOperator, got {code}: {Py.Repr(message)}");
        Ensure(
            message == "Failed to satisfy constraint: Member must satisfy enum value set: [ALL, OR]",
            $"unexpected message {Py.Repr(message)}");
    }
}
