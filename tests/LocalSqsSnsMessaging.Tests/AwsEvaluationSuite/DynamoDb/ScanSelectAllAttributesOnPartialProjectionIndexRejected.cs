// Translated from behavioral/dynamodb/check_scan_select_all_attributes_on_partial_projection_index_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-select-all-attributes-on-partial-projection-index-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanSelectAllAttributesOnPartialProjectionIndexRejected
{
    private const string KeysOnly = "keys-only-index";
    private const string Include = "include-index";

    private static string NotAll(string index) =>
        "One or more parameter values were invalid: Select type ALL_ATTRIBUTES is not supported for " +
        $"global secondary index {index} because its projection type is not ALL";

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0));
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
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
    /// Scan with Select=ALL_ATTRIBUTES on a KEYS_ONLY or INCLUDE global secondary index is refused,
    /// while ALL_PROJECTED_ATTRIBUTES on each index and ALL_ATTRIBUTES on the table are served.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_Scan.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-select-all-attributes-on-partial-projection-index-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-scansel-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = KeysOnly,
                    KeySchema = [new KeySchemaElement("gk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
                new GlobalSecondaryIndex
                {
                    IndexName = Include,
                    KeySchema = [new KeySchemaElement("gk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["inc"] },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        var item = Item(("pk", S("P")), ("sk", S("S")), ("gk", S("G")), ("inc", S("i")), ("body", S("b")));
        await ddb.PutItemAsync(table, item);
        var keys = Item(("pk", item["pk"]), ("sk", item["sk"]), ("gk", item["gk"]));
        var projected = new (string Index, Dictionary<string, AttributeValue> Want)[]
        {
            (KeysOnly, keys),
            (Include, Item(("pk", item["pk"]), ("sk", item["sk"]), ("gk", item["gk"]), ("inc", item["inc"]))),
        };
        foreach (var index in new[] { KeysOnly, Include })
        {
            var expected = NotAll(index);
            var (code, msg) = await Error(() => ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = index, Select = Select.ALL_ATTRIBUTES }));
            Ensure((code, msg) == ("ValidationException", expected), $"Scan IndexName={Py.Repr(index)} Select=ALL_ATTRIBUTES gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        // Index reads are eventually consistent: poll until each index serves its projection.
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left == 0 ? 300.0 : left) - 60.0));
        foreach (var (index, want) in projected)
        {
            List<Dictionary<string, AttributeValue>> got;
            while (true)
            {
                got = (await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = index, Select = Select.ALL_PROJECTED_ATTRIBUTES })).Items;
                if (got.Count > 0)
                {
                    break;
                }

                Ensure(clock.Elapsed < deadline, $"Scan of {index} stayed empty: {Py.Repr(got)}");
                await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            }

            Ensure(Py.Equal(got, new[] { want }), $"Scan {index} ALL_PROJECTED_ATTRIBUTES returned {Py.Repr(got)}, expected [{Py.Repr(want)}]");
        }

        var full = (await ddb.ScanAsync(new ScanRequest { TableName = table, Select = Select.ALL_ATTRIBUTES, ConsistentRead = true })).Items;
        Ensure(Py.Equal(full, new[] { item }), $"Scan of the table with Select=ALL_ATTRIBUTES returned {Py.Repr(full)}");
    }
}
