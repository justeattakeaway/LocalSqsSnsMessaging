// Translated from behavioral/dynamodb/check_scan_filter_not_equal_type_strict_across_s_n_b.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-not-equal-type-strict-across-s-n-b</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterNotEqualTypeStrictAcrossSNB
{
    private const string Check = "scan-filter-not-equal-type-strict-across-s-n-b";

    private static SortedSet<string> Ids(IEnumerable<Dictionary<string, AttributeValue>> items) =>
        new(items.Select(i => i["pk"].S), StringComparer.Ordinal);

    /// <summary>
    /// A Scan FilterExpression `&lt;>` against an N, S or B operand matches the other two
    /// identically-digited items holding a different type, and excludes only the item whose
    /// type and value both match the operand.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-not-equal-type-strict-across-s-n-b")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbne-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 150);

        await ddb.PutItemAsync(t, Item(("pk", S("val-n")), ("val", N("5"))));
        await ddb.PutItemAsync(t, Item(("pk", S("val-s")), ("val", S("5"))));
        await ddb.PutItemAsync(t, Item(("pk", S("val-b")), ("val", B("5"))));

        async Task<SortedSet<string>> Scan(AttributeValue operand)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = t,
                ConsistentRead = true,
                FilterExpression = "val <> :v",
                ExpressionAttributeValues = Item((":v", operand)),
            });
            return Ids(resp.Items);
        }

        var neN = await Scan(N("5"));
        Ensure(
            neN.SetEquals(["val-s", "val-b"]),
            $"val <> (N)'5' matched {Py.Repr(neN)}, expected the S and B items holding the same " +
            "digits, never the N item whose type and value both match");
        var neS = await Scan(S("5"));
        Ensure(
            neS.SetEquals(["val-n", "val-b"]),
            $"val <> (S)'5' matched {Py.Repr(neS)}, expected the N and B items holding the same " +
            "digits, never the S item whose type and value both match");
        var neB = await Scan(B("5"));
        Ensure(
            neB.SetEquals(["val-n", "val-s"]),
            $"val <> (B)'5' matched {Py.Repr(neB)}, expected the N and S items holding the same " +
            "digits, never the B item whose type and value both match");
    }
}
