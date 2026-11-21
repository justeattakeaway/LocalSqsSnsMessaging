// Translated from behavioral/dynamodb/check_update_item_creates_and_grows_items_across_the_1kb_write_capacity_boundary.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-creates-and-grows-items-across-the-1kb-write-capacity-boundary</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemCreatesAndGrowsItemsAcrossThe1kbWriteCapacityBoundary
{
    private const string Check = "update-item-creates-and-grows-items-across-the-1kb-write-capacity-boundary";

    // "pk" (2) + a 3-char key value (3) = 5 bytes of key overhead on every item below.
    private const int KeyBytes = 5;

    // Binary attribute value bytes are billed 1:1 (no base64 expansion): "v" (1) + raw_len bytes.
    // total 1024 -> 1 write unit, total 1025 -> 2 write units.
    private const int BLo = 1024 - KeyBytes - 1; // 1018 raw bytes
    private const int BHi = BLo + 1;             // 1019 raw bytes

    private static byte[] Bytes(char c, int n) => Enumerable.Repeat((byte)c, n).ToArray();

    /// <summary>A cleanup running undo() where a failed delete raises; the resource's own not-found code means it is already gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double boundSeconds)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < boundSeconds)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE with its GSI before the deadline: {Py.Repr(desc)}");
    }

    /// <summary><c>sorted(cc)</c>: the members of the ConsumedCapacity that are present.</summary>
    private static List<string> Members(ConsumedCapacity cc)
    {
        var members = new List<string>();
        if (cc.CapacityUnits is not null)
        {
            members.Add("CapacityUnits");
        }

        if (cc.GlobalSecondaryIndexes is not null)
        {
            members.Add("GlobalSecondaryIndexes");
        }

        if (cc.LocalSecondaryIndexes is not null)
        {
            members.Add("LocalSecondaryIndexes");
        }

        if (cc.ReadCapacityUnits is not null)
        {
            members.Add("ReadCapacityUnits");
        }

        if (cc.Table is not null)
        {
            members.Add("Table");
        }

        if (cc.TableName is not null)
        {
            members.Add("TableName");
        }

        if (cc.WriteCapacityUnits is not null)
        {
            members.Add("WriteCapacityUnits");
        }

        return members;
    }

    /// <summary>UpdateItem that creates a brand-new item with one attribute; the ConsumedCapacity.</summary>
    private static async Task<ConsumedCapacity> CreateWcu(IAmazonDynamoDB ddb, string table, string pk, string attr, AttributeValue value, ReturnConsumedCapacity returnCap)
    {
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(pk))),
            UpdateExpression = $"SET {attr} = :v",
            ExpressionAttributeValues = Item((":v", value)),
            ReturnConsumedCapacity = returnCap,
        });
        return resp.ConsumedCapacity;
    }

    /// <summary>(lo, hi) adjacent sizes where creating a brand-new item via UpdateItem bills 1.0 / 2.0 WCU.</summary>
    private static async Task<(int Lo, int Hi)> BisectNewItemBoundary(
        IAmazonDynamoDB ddb, string table, Func<string> pkSource, string attr, Func<int, AttributeValue> valueFor)
    {
        async Task<double> Wcu(int n) => (await CreateWcu(ddb, table, pkSource(), attr, valueFor(n), ReturnConsumedCapacity.TOTAL)).CapacityUnits ?? 0;

        int lo = 1, hi = 1024;
        Ensure(await Wcu(lo) == 1.0, $"a {lo}-byte {Py.Repr(attr)} value on a new item cost more than 1.0 WCU");
        while (await Wcu(hi) < 2.0)
        {
            hi *= 2;
            Ensure(hi < (1 << 20), "boundary search ran away without finding a 2.0 WCU item");
        }

        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (await Wcu(mid) < 2.0)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        Ensure(await Wcu(lo) == 1.0, $"re-measurement at {lo} bytes no longer costs 1.0 WCU");
        Ensure(await Wcu(hi) == 2.0, $"re-measurement at {hi} bytes no longer costs 2.0 WCU");
        return (lo, hi);
    }

    /// <summary>
    /// UpdateItem creating a brand-new item bills 1 WCU at exactly 1KB and 2 WCU one byte over,
    /// with TOTAL and untouched-index INDEXES shapes, and growing an existing item one byte past
    /// that edge bills on the larger post-update image.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/CapacityUnitCalculations.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-creates-and-grows-items-across-the-1kb-write-capacity-boundary")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EmulatorBudget, "The check's deadline is the runner's time left minus 60 s, which is nothing under the 60-second emulator budget, so the ACTIVE wait gives up at once")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbwcu-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        var left = ctx.TimeLeftSeconds;
        await WaitActive(ddb, table, Math.Max(0.0, (left == 0 ? 600.0 : left) - 60.0));

        var pkCounter = 0;
        string NextPk() => "n" + (++pkCounter).ToString("D2", CultureInfo.InvariantCulture);

        // --- a new item at exactly 1KB: TOTAL shape is the aggregate only ---
        var ccTotal = await CreateWcu(ddb, table, NextPk(), "v", B(Bytes('x', BLo)), ReturnConsumedCapacity.TOTAL);
        Ensure(
            ccTotal.CapacityUnits == 1.0,
            $"a new {KeyBytes + 1 + BLo}-byte item (TOTAL) cost {Py.Repr(ccTotal.CapacityUnits)} WCU, expected 1.0");
        Ensure(
            Members(ccTotal).SequenceEqual(["CapacityUnits", "TableName"]),
            $"TOTAL ConsumedCapacity carried {Py.Repr(Members(ccTotal))}, expected only CapacityUnits and TableName");
        Ensure(ccTotal.TableName == table, $"TOTAL ConsumedCapacity named table {Py.Repr(ccTotal.TableName)}");

        // --- the same 1KB item, this time itemized: Table arm only, no GSI arm since gpk is untouched ---
        var pkIdx = NextPk();
        var ccIdx = await CreateWcu(ddb, table, pkIdx, "v", B(Bytes('x', BLo)), ReturnConsumedCapacity.INDEXES);
        Ensure(ccIdx.CapacityUnits == 1.0, $"a new 1KB item (INDEXES) cost {Py.Repr(ccIdx.CapacityUnits)} WCU, expected 1.0");
        Ensure(
            Members(ccIdx).SequenceEqual(["CapacityUnits", "Table", "TableName"]),
            $"INDEXES ConsumedCapacity for an item that never touches gpk carried {Py.Repr(Members(ccIdx))}, " +
            "expected CapacityUnits, TableName and Table only (no GlobalSecondaryIndexes arm)");
        Ensure(
            ccIdx.Table is { CapacityUnits: 1.0, ReadCapacityUnits: null, WriteCapacityUnits: null },
            $"Table arm was {Py.Repr(ccIdx.Table?.CapacityUnits)}, expected CapacityUnits 1.0");
        var storedIdx = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pkIdx))), ConsistentRead = true })).Item;
        Ensure(storedIdx["v"].B.Length == BLo, $"the 1KB item read back {storedIdx["v"].B.Length} raw bytes");

        // --- one byte over 1KB: a new item costs 2 WCU ---
        var pkOver = NextPk();
        var ccOver = await CreateWcu(ddb, table, pkOver, "v", B(Bytes('x', BHi)), ReturnConsumedCapacity.TOTAL);
        Ensure(
            ccOver.CapacityUnits == 2.0,
            $"a new {KeyBytes + 1 + BHi}-byte item cost {Py.Repr(ccOver.CapacityUnits)} WCU, expected 2.0");
        var storedOver = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pkOver))), ConsistentRead = true })).Item;
        Ensure(storedOver["v"].B.Length == BHi, $"the 1025-byte item read back {storedOver["v"].B.Length} raw bytes");

        // --- a Binary Set's own byte contribution: bisect it the same way as the plain Binary above ---
        var (bsLo, bsHi) = await BisectNewItemBoundary(ddb, table, NextPk, "w", n => BS(Bytes('y', n)));
        Ensure(bsHi == bsLo + 1, $"the BS boundary did not converge to adjacent raw lengths: {Py.Repr((bsLo, bsHi))}");
        var bsOverhead = 1024 - KeyBytes - 1 - bsLo;
        Ensure(
            bsOverhead == 0,
            $"a one-element Binary Set added {bsOverhead} byte(s) of overhead beyond its element's " +
            "raw length, expected 0");

        // --- growing an existing item: staying at exactly 1KB still bills 1 WCU ---
        var growPk = NextPk();
        await ddb.PutItemAsync(table, Item(("pk", S(growPk)), ("v", B(Bytes('x', 10)))));
        var growToLo = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(growPk))),
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", B(Bytes('x', BLo)))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity;
        Ensure(
            growToLo.CapacityUnits == 1.0,
            $"growing a 15-byte item up to exactly 1KB (no crossing) cost {Py.Repr(growToLo.CapacityUnits)} " +
            "WCU, expected 1.0");
        var afterLo = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(growPk))), ConsistentRead = true })).Item;
        Ensure(afterLo["v"].B.Length == BLo, $"the grown item read back {afterLo["v"].B.Length} raw bytes");

        // --- growing it one byte further, crossing 1KB, bills on the larger post-update image ---
        var growToHi = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(growPk))),
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", B(Bytes('x', BHi)))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity;
        Ensure(
            growToHi.CapacityUnits == 2.0,
            $"growing a 1KB item one byte past the edge cost {Py.Repr(growToHi.CapacityUnits)} WCU, " +
            "expected 2.0 (the larger, post-update image)");
        var afterHi = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(growPk))), ConsistentRead = true })).Item;
        Ensure(afterHi["v"].B.Length == BHi, $"the grown item read back {afterHi["v"].B.Length} raw bytes");
    }
}
