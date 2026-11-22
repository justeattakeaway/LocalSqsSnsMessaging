// Translated from behavioral/dynamodb/check_query_attribute_type_filter_selects_collection_and_null_types.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-attribute-type-filter-selects-collection-and-null-types</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryAttributeTypeFilterSelectsCollectionAndNullTypes
{
    private const string Check = "query-attribute-type-filter-selects-collection-and-null-types";

    /// <summary>(sort key, attribute_type code, the AttributeValue stored under "attr") under test.</summary>
    private static List<(string Sk, string TypeCode, AttributeValue Val)> Cases() =>
    [
        ("ns", "NS", NS("1", "2", "3")),
        ("bs", "BS", BS([0x00, 0x01], [0xff])),
        ("l", "L", L(S("a"), N("1"))),
        ("m", "M", M(("x", S("1")), ("y", N("2")))),
        ("null", "NULL", Null()),
        ("ss", "SS", SS("a", "b")),
    ];

    /// <summary><paramref name="value"/> with a set's members sorted; DynamoDB sets carry no member order.</summary>
    private static Dictionary<string, object?> Unordered(AttributeValue value)
    {
        if (value.SS is not null)
        {
            return new() { ["SS"] = value.SS.Order(StringComparer.Ordinal).ToList() };
        }

        if (value.NS is not null)
        {
            return new() { ["NS"] = value.NS.Order(StringComparer.Ordinal).ToList() };
        }

        if (value.BS is not null)
        {
            return new() { ["BS"] = value.BS.Select(b => b.ToArray()).OrderBy(Convert.ToHexString, StringComparer.Ordinal).ToList() };
        }

        return Py.Boto(value);
    }

    /// <summary>
    /// A Query FilterExpression attribute_type(attr, :t) selects exactly the item whose stored
    /// attribute has type NS, BS, L, M, NULL or SS, and a missing attribute matches none of them.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-attribute-type-filter-selects-collection-and-null-types")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-qdatyp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var (sk, _, val) in Cases())
        {
            await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S(sk)), ("attr", val)));
        }

        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("missing"))));
        var total = Cases().Count + 1;

        foreach (var (sk, typeCode, val) in Cases())
        {
            var resp = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :p",
                FilterExpression = "attribute_type(#a, :t)",
                ExpressionAttributeNames = Names(("#a", "attr")),
                ExpressionAttributeValues = Item((":p", S("p1")), (":t", S(typeCode))),
                ConsistentRead = true,
            });
            Ensure(
                resp.ScannedCount == total,
                $"type {Py.Repr(typeCode)}: expected ScannedCount {total} (pre-filter), got {Py.Repr(resp.ScannedCount)}");
            Ensure(
                resp.Count == 1,
                $"type {Py.Repr(typeCode)}: expected exactly one matching item, got Count {Py.Repr(resp.Count)}");
            var item = resp.Items[0];
            Ensure(
                Py.Equal(item["sk"], S(sk)),
                $"type {Py.Repr(typeCode)}: expected the item at sk {Py.Repr(sk)} to match, got {Py.Repr(item)}");
            Ensure(
                Py.Equal(Unordered(item["attr"]), Unordered(val)),
                $"type {Py.Repr(typeCode)}: returned attribute value {Py.Repr(item["attr"])}, expected {Py.Repr(val)}");
        }
    }
}
