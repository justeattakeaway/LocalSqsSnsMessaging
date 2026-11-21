// Translated from behavioral/dynamodb/check_batch_write_item_size_boundary_missing_table.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-size-boundary-missing-table</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemSizeBoundaryMissingTable
{
    private const string Check = "batch-write-item-size-boundary-missing-table";
    private const int LimitBytes = 409600;

    /// <summary>
    /// BatchWriteItem naming only a nonexistent table is ResourceNotFoundException for an item
    /// exactly at the 409600-byte size limit, in a single small attribute, a 27-character-named
    /// attribute and three attributes, the same shapes BatchWriteItem accepts on a real table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-size-boundary-missing-table")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var realTable = FidelityContext.UniqueName("sim2real-behav-bwsz-");
        var missingTable = FidelityContext.UniqueName("sim2real-behav-bwsz-missing-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = realTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(realTable));
        await ddb.WaitForTableExists(realTable, delaySeconds: 2, maxAttempts: 250);

        var shapes = new (string Name, string[] Names)[]
        {
            ("single", ["data"]),
            ("long-name", [AttrName(0)]),
            ("three-attrs", [AttrName(0), AttrName(1), AttrName(2)]),
        };

        foreach (var (shapeName, names) in shapes)
        {
            var item = ItemOfSize($"missing-{shapeName}", names, LimitBytes);

            // Positive control: the at-limit item in this shape is accepted whole on the real table,
            // so the rejection below is attributable to the missing table and not to this item's size.
            var control = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new() { [realTable] = [new WriteRequest { PutRequest = new PutRequest { Item = item } }] },
            });
            var unprocessed = control.UnprocessedItems ?? [];
            Ensure(
                unprocessed.Count == 0,
                $"the {shapeName} at-limit item must be fully accepted on the real table, got UnprocessedItems={Py.Repr(unprocessed.Keys)}");
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = realTable, Key = Item(("pk", item["pk"])), ConsistentRead = true })).Item;
            Ensure(Py.Equal(got, item), $"the {shapeName} at-limit item must read back byte-identical, got {(got is null ? "None" : "a different item")}");

            var (code, message) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new() { [missingTable] = [new WriteRequest { PutRequest = new PutRequest { Item = item } }] },
            }));
            Ensure(
                code == "ResourceNotFoundException",
                $"BatchWriteItem naming only a nonexistent table with a {shapeName} at-limit item must "
                + $"be ResourceNotFoundException, got {Py.Repr(code)}: {Py.Repr(message)}");
            Ensure(
                message == "Requested resource not found",
                $"unexpected ResourceNotFoundException message for the {shapeName} shape: {Py.Repr(message)}");
        }

        var describe = await Error(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = missingTable }));
        var expectedDescribe = ((string?)"ResourceNotFoundException", (string?)$"Requested resource not found: Table: {missingTable} not found");
        Ensure(
            describe == expectedDescribe,
            "the nonexistent table must still not exist after the refused calls: expected "
            + $"{Py.Repr(expectedDescribe)}, got {Py.Repr(describe)}");
    }

    /// <summary>A unique, exactly-27-character attribute name.</summary>
    private static string AttrName(int i)
    {
        var prefix = $"payload-attr-{i:00}-";
        var name = prefix + new string('n', 27 - prefix.Length);
        Ensure(name.Length == 27, $"helper built a {name.Length}-char name, expected 27: {Py.Repr(name)}");
        return name;
    }

    /// <summary>An item whose pk plus every attribute in names sums to exactly totalBytes.</summary>
    private static Dictionary<string, AttributeValue> ItemOfSize(string pkValue, string[] names, int totalBytes)
    {
        var fixedBytes = "pk".Length + System.Text.Encoding.UTF8.GetByteCount(pkValue) + names.Sum(n => System.Text.Encoding.UTF8.GetByteCount(n));
        var payloadBytes = totalBytes - fixedBytes;
        var n = names.Length;
        Ensure(payloadBytes >= n, $"{totalBytes} bytes is too small for {n} named attributes");
        var (baseLength, extra) = (payloadBytes / n, payloadBytes % n);
        var item = Item(("pk", S(pkValue)));
        for (var idx = 0; idx < n; idx++)
        {
            var length = baseLength + (idx == 0 ? extra : 0);
            item[names[idx]] = S(new string('x', length));
        }

        return item;
    }

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn) ?? throw Fail("expected the call to be rejected, but it succeeded");
        return (error.Code, error.Message);
    }
}
