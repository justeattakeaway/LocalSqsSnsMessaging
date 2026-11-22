// Translated from behavioral/dynamodb/check_scan_filter_nested_document_path_forms.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-nested-document-path-forms</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterNestedDocumentPathForms
{
    private const string Check = "scan-filter-nested-document-path-forms";

    // KEEP matches every filter below; SKIP matches none, proving each filter selects by its own
    // nested path rather than by accident.
    private static Dictionary<string, AttributeValue> Keep() => Item(
        ("pk", S("keep")),
        ("info", M(("status", S("accepted")))),
        ("steps", L(M(("child", S("p"))), M(("child", S("q"))))),
        ("box", M(("other", S("x")))),
        ("lvl1", M(("lvl2", M(("lvl3", S("target")))))),
        ("doc", M(("big", S("abcdef")))));

    private static Dictionary<string, AttributeValue> Skip() => Item(
        ("pk", S("skip")),
        ("info", M(("status", S("declined")))),
        ("steps", L(M(("child", S("z"))))),
        ("box", M(("child", S("present")))),
        ("lvl1", M(("lvl2", M(("lvl3", S("other")))))),
        ("doc", M(("big", S("ab")))));

    /// <summary>The set of <c>pk</c> values a Scan returns under one FilterExpression.</summary>
    private static async Task<SortedSet<string>> ScanPks(
        IAmazonDynamoDB ddb, string table, string filterExpression, Dictionary<string, string>? names, Dictionary<string, AttributeValue>? values)
    {
        var resp = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            FilterExpression = filterExpression,
            ExpressionAttributeNames = names is { Count: > 0 } ? names : null,
            ExpressionAttributeValues = values is { Count: > 0 } ? values : null,
        });
        return new SortedSet<string>(resp.Items.Select(i => i["pk"].S), StringComparer.Ordinal);
    }

    /// <summary>
    /// A Scan FilterExpression selects by a nested document path: begins_with through an
    /// aliased reserved-word segment, a list-index-then-map-key path, attribute_not_exists on an
    /// absent nested map child, equality three levels deep, and size() of a nested string.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-nested-document-path-forms")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-ddbnp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [table] =
                [
                    new WriteRequest { PutRequest = new PutRequest { Item = Keep() } },
                    new WriteRequest { PutRequest = new PutRequest { Item = Skip() } },
                ],
            },
        });
        var unprocessed = resp.UnprocessedItems ?? [];
        Ensure(unprocessed.Count == 0, $"seeding BatchWriteItem left {Py.Repr(unprocessed)} unprocessed");

        // begins_with through an aliased nested path whose segment ("status") is a reserved word.
        var names = Names(("#i", "info"), ("#s", "status"));
        var got = await ScanPks(ddb, table, "begins_with(#i.#s, :p)", names, Item((":p", S("acc"))));
        Ensure(got.SetEquals(["keep"]), $"begins_with(info.status, 'acc') selected {Py.Repr(got)}, expected ['keep']");
        var none = await ScanPks(ddb, table, "begins_with(#i.#s, :p)", names, Item((":p", S("xyz"))));
        Ensure(none.Count == 0, $"begins_with(info.status, 'xyz') selected {Py.Repr(none)}, expected none");

        // list-index then map-key: steps[0].child.
        got = await ScanPks(ddb, table, "steps[0].child = :v", null, Item((":v", S("p"))));
        Ensure(got.SetEquals(["keep"]), $"steps[0].child = 'p' selected {Py.Repr(got)}, expected ['keep']");

        // attribute_not_exists on an absent nested map child.
        names = Names(("#b", "box"), ("#c", "child"));
        got = await ScanPks(ddb, table, "attribute_not_exists(#b.#c)", names, null);
        Ensure(got.SetEquals(["keep"]), $"attribute_not_exists(box.child) selected {Py.Repr(got)}, expected ['keep']");

        // equality three levels deep.
        got = await ScanPks(ddb, table, "lvl1.lvl2.lvl3 = :v", null, Item((":v", S("target"))));
        Ensure(got.SetEquals(["keep"]), $"lvl1.lvl2.lvl3 = 'target' selected {Py.Repr(got)}, expected ['keep']");

        // size() of a nested document path.
        got = await ScanPks(ddb, table, "size(doc.big) = :n", null, Item((":n", N("6"))));
        Ensure(got.SetEquals(["keep"]), $"size(doc.big) = 6 selected {Py.Repr(got)}, expected ['keep']");
    }
}
