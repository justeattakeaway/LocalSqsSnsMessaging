// Translated from behavioral/dynamodb/check_scan_filter_eq_and_contains_type_strict_across_s_n_b.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-eq-and-contains-type-strict-across-s-n-b</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterEqAndContainsTypeStrictAcrossSNB
{
    private const string Check = "scan-filter-eq-and-contains-type-strict-across-s-n-b";

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static SortedSet<string> Ids(IEnumerable<Dictionary<string, AttributeValue>> items) =>
        new(items.Select(i => i["pk"].S), StringComparer.Ordinal);

    /// <summary>
    /// A Scan FilterExpression `=` matches an N, S or B attribute only against the identically
    /// typed operand, never the other two holding the same digits; `contains()` with an N operand
    /// matches an NS member or an N element inside an L, never an S, SS or B holding the same digits.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-eq-and-contains-type-strict-across-s-n-b")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbts-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 150);
        await ddb.PutItemAsync(t, Item(("pk", S("eq-n")), ("val", N("5"))));
        await ddb.PutItemAsync(t, Item(("pk", S("eq-s")), ("val", S("5"))));
        await ddb.PutItemAsync(t, Item(("pk", S("eq-b")), ("val", B("5"))));
        await ddb.PutItemAsync(t, Item(("pk", S("coll-ns")), ("coll", NS("5", "6"))));
        await ddb.PutItemAsync(t, Item(("pk", S("coll-l")), ("coll", L(N("5"), S("x")))));
        await ddb.PutItemAsync(t, Item(("pk", S("coll-s")), ("coll", S("a5b"))));
        await ddb.PutItemAsync(t, Item(("pk", S("coll-ss")), ("coll", SS("5", "6"))));
        await ddb.PutItemAsync(t, Item(("pk", S("coll-b")), ("coll", B("5"))));

        async Task<SortedSet<string>> Scan(string filterExpression, AttributeValue value) =>
            Ids((await ddb.ScanAsync(new ScanRequest
            {
                TableName = t,
                ConsistentRead = true,
                FilterExpression = filterExpression,
                ExpressionAttributeValues = Item((":v", value)),
            })).Items);

        var eqN = await Scan("val = :v", N("5"));
        Ensure(eqN.SetEquals(["eq-n"]), $"val = (N)'5' matched {Py.Repr(eqN)}, expected only the N-typed item");
        var eqS = await Scan("val = :v", S("5"));
        Ensure(eqS.SetEquals(["eq-s"]), $"val = (S)'5' matched {Py.Repr(eqS)}, expected only the S-typed item");
        var eqB = await Scan("val = :v", B("5"));
        Ensure(eqB.SetEquals(["eq-b"]), $"val = (B)'5' matched {Py.Repr(eqB)}, expected only the B-typed item");
        var containsN = await Scan("contains(coll, :v)", N("5"));
        Ensure(
            containsN.SetEquals(["coll-ns", "coll-l"]),
            $"contains(coll, (N)5) matched {Py.Repr(containsN)}, expected only the NS and L items holding an N 5 member/element, never the S, SS or B items holding the same digits");

        // Positive control: contains() over the correctly typed operand still matches the S, SS and B
        // items, proving the filtering mechanism discriminates by value, not merely by excluding N.
        var containsS = await Scan("contains(coll, :v)", S("5"));
        Ensure(
            containsS.SetEquals(["coll-s", "coll-ss"]),
            $"contains(coll, (S)'5') matched {Py.Repr(containsS)}, expected the S item (substring) and the SS item (member), never the NS, L or B items");
        var containsB = await Scan("contains(coll, :v)", B("5"));
        Ensure(containsB.SetEquals(["coll-b"]), $"contains(coll, (B)'5') matched {Py.Repr(containsB)}, expected only the B item");
    }
}
