// Translated from behavioral/dynamodb/check_vector_item_size_gate_counts_stored_number_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.vector-item-size-gate-counts-stored-number-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class VectorItemSizeGateCountsStoredNumberBytes
{
    private const string Check = "vector-item-size-gate-counts-stored-number-bytes";
    private const int Dims = 4096;
    private const int PadBytes = 370 * 1024;

    private static readonly (string Code, string Message) ItemTooLarge = ("ValidationException", "Item size has exceeded the maximum allowed size");

    /// <summary>
    /// The 400 KB item-size limit is computed from the vector's actual stored numbers, not from
    /// the fixed 4-bytes-per-dimension rate the write-capacity meter uses for the same attribute.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "vector indexes are not emulated: PutItem reports no VectorIndexes capacity")]
    [DisplayName("dynamodb.vector-item-size-gate-counts-stored-number-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvecgate-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "v" },
                    Dimensions = Dims,
                    DistanceFunction = VectorDistanceFunction.COSINE,
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table)));
        await WaitActive(ctx, ddb, table);

        // Positive control: padding that brings the item close to the 400 KB limit, plus a vector
        // of single-digit numbers, fits comfortably — and the write's billed vector bytes are tiny
        // relative to the item's real size, which is dominated by the padding.
        var okItem = Item(
            ("pk", S("ok")),
            ("pad", S(new string('x', PadBytes))),
            ("v", L([.. Enumerable.Range(0, Dims).Select(_ => N("1"))])));
        var okOut = await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = okItem, ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES });
        var okVwb = okOut.ConsumedCapacity.VectorIndexes is { } vi && vi.TryGetValue("idx", out var cap) ? cap.VectorWriteRequestBytes : null;
        Ensure(
            okVwb == 16389.0,
            $"the positive control's billed VectorWriteRequestBytes was {Py.Repr(okVwb)}, expected 16389.0 — tiny next to the ~370 KB of padding in the same item");

        // The same padding, and the same dimension count, but numbers with far more significant
        // digits: the real stored size grows enough to cross the 400 KB limit, even though the
        // dimension count — and so the billed vector bytes above — did not change.
        var badItem = Item(
            ("pk", S("bad")),
            ("pad", S(new string('x', PadBytes))),
            ("v", L([.. Enumerable.Range(0, Dims).Select(_ => N(new string('9', 38)))])));
        await ExpectError(() => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = badItem }), ItemTooLarge.Code, ItemTooLarge.Message);

        // The positive control's item persisted; the refused one never did.
        var readOk = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("ok"))), ConsistentRead = true });
        Ensure(
            readOk.Item is not null && readOk.Item["v"].L[0].N == "1",
            $"the positive control's item did not read back as written: {(readOk.Item is null ? "None" : "<item>")}");
        var readBad = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("bad"))), ConsistentRead = true });
        Ensure(readBad.Item is null, $"the refused PutItem left an item behind: {(readBad.Item is null ? "None" : "<item>")}");
    }

    /// <summary>Assert <paramref name="fn"/> raises exactly (code, message).</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected {Py.Repr((code, message))}, but the call succeeded: {result}");
        }

        var got = (error.Code, error.Message);
        Ensure(got == (code, message), $"expected {Py.Repr((code, message))}, got {Py.Repr(got)}");
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task<TableDescription> WaitActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table, double capS = 240.0)
    {
        var left = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(capS, left - 5.0)));
        var clock = FidelityTime.StartNew();
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        while (true)
        {
            tbl = (await ddb.DescribeTableAsync(table)).Table;
            var indexes = tbl.VectorIndexes ?? [];
            if (tbl.TableStatus == TableStatus.ACTIVE && indexes.All(v => v.IndexStatus == IndexStatus.ACTIVE))
            {
                return tbl;
            }

            if (clock.Elapsed >= deadline)
            {
                throw Fail(
                    $"table/vector index never became ACTIVE in time: status {Py.Repr(tbl.TableStatus?.Value)}, indexes "
                    + Py.Repr(indexes.Select(v => (v.IndexName, v.IndexStatus?.Value)).ToList()));
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
