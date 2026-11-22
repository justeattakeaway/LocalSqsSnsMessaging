// Translated from behavioral/dynamodb/check_scan_1mb_page_boundary_counts_attribute_name_bytes_not_only_values.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-1mb-page-boundary-counts-attribute-name-bytes-not-only-values</summary>
[Category("AwsEvaluationSuite")]
public sealed class Scan1mbPageBoundaryCountsAttributeNameBytesNotOnlyValues
{
    private const string Check = "scan-1mb-page-boundary-counts-attribute-name-bytes-not-only-values";
    private const int OneMb = 1024 * 1024;
    private const string Pk = "shard";
    private const int PerItem = 87_400;
    private const int Fixed = 2 + 5 + 2 + 3; // pk name+value, sk name+value (3-digit)
    private const int Remaining = PerItem - Fixed - 2; // two single-byte "x" values
    private const int NameLenA = Remaining / 2;
    private const int NameLenB = Remaining - NameLenA;
    private static readonly string AttrA = new('a', NameLenA);
    private static readonly string AttrB = new('b', NameLenB);

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>An item whose size comes almost entirely from two long attribute NAMES, not their values.</summary>
    private static Dictionary<string, AttributeValue> MakeItem(int i) =>
        Item(("pk", S(Pk)), ("sk", S($"{i:D3}")), (AttrA, S("x")), (AttrB, S("x")));

    /// <summary>Item size for an all-string item: UTF-8 length of each name plus its value.</summary>
    private static int ItemBytes(Dictionary<string, AttributeValue> item) =>
        item.Sum(e => Encoding.UTF8.GetByteCount(e.Key) + Encoding.UTF8.GetByteCount(e.Value.S));

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(270.0))
        {
            if ((await ddb.DescribeTableAsync(table)).Table.TableStatus == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {Py.Repr(table)} did not become ACTIVE before the deadline");
    }

    /// <summary>
    /// The 1 MB Scan page budget counts attribute NAME bytes, not only values: items whose size
    /// is almost entirely long attribute names cross the boundary exactly like equally sized values.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-1mb-page-boundary-counts-attribute-name-bytes-not-only-values")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbscanname-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, table);

        // 11 items whose two attribute names alone sum to just under the per-item byte target; one
        // more item of this shape would push the table's total past the 1 MB budget.
        var underTotal = 11 * PerItem;
        Ensure(underTotal < OneMb && underTotal + PerItem > OneMb, $"the fixture must sit within one item of the limit; got {Py.Repr(underTotal)}");
        for (var i = 0; i < 11; i++)
        {
            await ddb.PutItemAsync(table, MakeItem(i));
        }

        var resp1 = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
        Ensure(
            resp1.LastEvaluatedKey is null,
            $"11 name-dominated items totalling {underTotal} bytes must fit on one page; got LastEvaluatedKey {Py.Repr(resp1.LastEvaluatedKey)}");
        Ensure(
            resp1.Count == resp1.ScannedCount && resp1.ScannedCount == 11 && resp1.Items.Count == 11,
            $"Count={Py.Repr(resp1.Count)} ScannedCount={Py.Repr(resp1.ScannedCount)} items={Py.Repr(resp1.Items.Count)}");
        var page1Bytes = resp1.Items.Sum(ItemBytes);
        Ensure(page1Bytes == underTotal, $"the 11 returned items must total {underTotal} bytes of names plus values; got {page1Bytes}");

        // A 12th item makes the whole table's name-dominated size cross the budget by less than one
        // item's width -- the page still truncates and carries a LastEvaluatedKey.
        await ddb.PutItemAsync(table, MakeItem(11));
        var resp2 = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
        Ensure(
            resp2.LastEvaluatedKey is not null,
            $"12 name-dominated items whose total exceeds 1 MB must truncate even though they are the entire table; got Count={Py.Repr(resp2.Count)} ScannedCount={Py.Repr(resp2.ScannedCount)} LastEvaluatedKey={Py.Repr(resp2.LastEvaluatedKey)}");
        Ensure(
            resp2.Count == resp2.ScannedCount && resp2.ScannedCount == 12,
            $"Count={Py.Repr(resp2.Count)} ScannedCount={Py.Repr(resp2.ScannedCount)}, expected 12 and 12");
        var items2 = resp2.Items;
        var total2 = items2.Sum(ItemBytes);
        var withoutLast2 = total2 - ItemBytes(items2[^1]);
        Ensure(total2 > OneMb, $"the truncated page must exceed 1 MB; got {total2} bytes over {items2.Count} items");
        Ensure(
            withoutLast2 <= OneMb,
            $"the page must be the SHORTEST prefix exceeding 1 MB; without its last item it is still {withoutLast2} bytes");
        var resume = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true, ExclusiveStartKey = resp2.LastEvaluatedKey });
        Ensure(
            resume.Count == resume.ScannedCount && resume.ScannedCount == 0 && resume.LastEvaluatedKey is null,
            $"resuming from that LastEvaluatedKey must find the table exhausted; got Count={Py.Repr(resume.Count)} LastEvaluatedKey={Py.Repr(resume.LastEvaluatedKey)}");
    }
}
