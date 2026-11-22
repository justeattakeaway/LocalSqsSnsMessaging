// Translated from behavioral/dynamodb/check_query_filter_attribute_exists_and_contains_compound.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-attribute-exists-and-contains-compound</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterAttributeExistsAndContainsCompound
{
    private const string Check = "query-filter-attribute-exists-and-contains-compound";

    /// <summary>
    /// Items in the queried partition: (sk, desc, has_status). Expected sets below are derived from
    /// these fields rather than hardcoded.
    /// </summary>
    private static readonly (string Sk, string Desc, bool HasStatus)[] Rows =
    [
        ("i1", "notice of refund", true),
        ("i2", "international customs hold notice", false),
        ("i3", "late shipment update", true),
        ("i4", "notice", false),
        ("i5", "unrelated memo", true),
    ];

    private static Dictionary<string, AttributeValue> ItemOf((string Sk, string Desc, bool HasStatus) row)
    {
        var (sk, desc, hasStatus) = row;
        var @out = Item(("pk", S("p1")), ("sk", S(sk)), ("desc", S(desc)));
        if (hasStatus)
        {
            @out["status"] = S("active");
        }

        return @out;
    }

    /// <summary>
    /// Query's FilterExpression `attribute_exists(#a) AND contains(#d, :v)` selects only items
    /// carrying the attribute whose other field also contains the substring, over the whole
    /// partition it scans.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-attribute-exists-and-contains-compound")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-ddbqfac-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        foreach (var row in Rows)
        {
            await ddb.PutItemAsync(t, ItemOf(row));
        }

        static HashSet<string> Ids(List<Dictionary<string, AttributeValue>> items) => items.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal);
        static List<string> Sorted(IEnumerable<string> set) => [.. set.Order(StringComparer.Ordinal)];

        var expected = Rows.Where(r => r.HasStatus && r.Desc.Contains("notice", StringComparison.Ordinal)).Select(r => r.Sk).ToHashSet(StringComparer.Ordinal);
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "#pk = :pk",
            FilterExpression = "attribute_exists(#st) AND contains(#d, :v)",
            ExpressionAttributeNames = Names(("#pk", "pk"), ("#st", "status"), ("#d", "desc")),
            ExpressionAttributeValues = Item((":pk", S("p1")), (":v", S("notice"))),
            ConsistentRead = true,
        });
        var got = Ids(resp.Items);
        Ensure(
            got.SetEquals(expected),
            $"attribute_exists(status) AND contains(desc, 'notice') returned {Py.Repr(Sorted(got))}, " +
            $"expected {Py.Repr(Sorted(expected))}");
        Ensure(resp.Count == expected.Count, $"Count was {Py.Repr(resp.Count)}, expected {Py.Repr(expected.Count)}");
        Ensure(
            resp.ScannedCount == Rows.Length,
            $"ScannedCount was {Py.Repr(resp.ScannedCount)}, expected the partition's {Rows.Length} items " +
            "regardless of the filter");

        // Positive control: the attribute_exists half alone matches every item carrying status,
        // proving the compound AND genuinely narrows rather than the contains() half alone deciding.
        var existsOnly = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "#pk = :pk",
            FilterExpression = "attribute_exists(#st)",
            ExpressionAttributeNames = Names(("#pk", "pk"), ("#st", "status")),
            ExpressionAttributeValues = Item((":pk", S("p1"))),
            ConsistentRead = true,
        });
        var expectedExistsOnly = Rows.Where(r => r.HasStatus).Select(r => r.Sk).ToHashSet(StringComparer.Ordinal);
        var gotExistsOnly = Ids(existsOnly.Items);
        Ensure(
            gotExistsOnly.SetEquals(expectedExistsOnly),
            $"attribute_exists(status) alone returned {Py.Repr(Sorted(gotExistsOnly))}, expected " +
            $"{Py.Repr(Sorted(expectedExistsOnly))}");
    }
}
