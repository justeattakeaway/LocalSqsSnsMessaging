// Translated from behavioral/dynamodb/check_binary_range_key_item_set_and_nested_path_updates_then_table_refuses_reads.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.binary-range-key-item-set-and-nested-path-updates-then-table-refuses-reads</summary>
[Category("AwsEvaluationSuite")]
public sealed class BinaryRangeKeyItemSetAndNestedPathUpdatesThenTableRefusesReads
{
    private const string Check = "binary-range-key-item-set-and-nested-path-updates-then-table-refuses-reads";
    private const string ErrResourceNotFound = "ResourceNotFoundException";

    /// <summary>Bound on each asynchronous table transition; below the harness ceiling.</summary>
    private const double PollBudgetS = 40.0;

    /// <summary>Bound on each wait; a target that needs longer fails with the last answer it gave.</summary>
    private const double WaitS = 40.0;

    /// <summary>
    /// A provisioned N-hash/B-range table reports its schema and capacity; an item of sets, empty and
    /// nested values round-trips, set ADD/DELETE, nested SET/REMOVE and list_append apply with exact
    /// UPDATED_NEW/UPDATED_OLD answers; the deleted table refuses reads, queries and updates.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.binary-range-key-item-set-and-nested-path-updates-then-table-refuses-reads")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("tenant", KeyType.HASH), new KeySchemaElement("blob", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("tenant", ScalarAttributeType.N), new AttributeDefinition("blob", ScalarAttributeType.B)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 2, WriteCapacityUnits = 3 },
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, table));
        await WaitTableActive(ddb, table);
        var desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
        Ensure(desc.TableStatus == TableStatus.ACTIVE, $"expected TableStatus ACTIVE, got {Py.Repr(desc.TableStatus)}");
        var keySchema = desc.KeySchema.Select(ks => (ks.AttributeName, ks.KeyType.Value)).ToList();
        Ensure(
            keySchema.SequenceEqual([("tenant", "HASH"), ("blob", "RANGE")]),
            $"unexpected key schema {Py.Repr(keySchema)}");
        var attrs = desc.AttributeDefinitions.ToDictionary(a => a.AttributeName, a => a.AttributeType.Value, StringComparer.Ordinal);
        Ensure(
            Py.Equal(attrs, new Dictionary<string, string> { ["tenant"] = "N", ["blob"] = "B" }),
            $"unexpected attribute definitions {Py.Repr(attrs)}");
        var thr = desc.ProvisionedThroughput;
        Ensure(
            (thr.ReadCapacityUnits, thr.WriteCapacityUnits) == (2, 3),
            $"expected provisioned 2 read / 3 write units, got {Py.Repr(new Dictionary<string, object?> { ["ReadCapacityUnits"] = thr.ReadCapacityUnits, ["WriteCapacityUnits"] = thr.WriteCapacityUnits })}");

        var blob = new byte[] { 0x00, 0x01, 0xfe }.Concat(Encoding.UTF8.GetBytes(FidelityContext.UniqueName("k-"))).ToArray();
        Dictionary<string, AttributeValue> Key() => Item(("tenant", N("42")), ("blob", B(blob)));
        Dictionary<string, AttributeValue> ItemValue() => Item(
            ("tenant", N("42")),
            ("blob", B(blob)),
            ("s", S("")),
            ("small", N("-0.000123")),
            ("ns", NS("1", "2.5", "-3")),
            ("bs", BS("a"u8.ToArray(), [0xff, 0x00])),
            ("ss", SS("é", "z")),
            ("el", L()),
            ("em", new AttributeValue { M = [] }),
            ("deep", M(("tags", L(S("t0"), S("t1"))), ("n", N("1")))),
            ("flag", Bool(false)));
        await ddb.PutItemAsync(table, ItemValue());
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(Norm(got), Norm(ItemValue())), $"expected the typed item back, got {Py.Repr(got)}");

        // Set union and difference, a nested counter, a nested list element removal and an append to
        // an empty list; UPDATED_NEW reports only the touched attributes after the update.
        var updated = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET deep.n = deep.n + :two, el = list_append(el, :more), s = :s " +
                               "REMOVE deep.tags[0] ADD ns :ns2, ss :ss2 DELETE bs :bs1",
            ExpressionAttributeValues = Item(
                (":two", N("2")),
                (":more", L(N("7"), Null())),
                (":s", S("filled")),
                (":ns2", NS("2.5", "10")),
                (":ss2", SS("a")),
                (":bs1", BS("a"u8.ToArray()))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Dictionary<string, AttributeValue> WantNew() => Item(
            ("deep", M(("n", N("3")), ("tags", L(S("t1"))))),
            ("el", L(N("7"), Null())),
            ("s", S("filled")),
            ("ns", NS("1", "2.5", "-3", "10")),
            ("ss", SS("é", "z", "a")),
            ("bs", BS([0xff, 0x00])));
        Ensure(Py.Equal(Norm(updated), Norm(WantNew())), $"UpdateItem UPDATED_NEW: expected {Py.Repr(WantNew())}, got {Py.Repr(updated)}");

        var old = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "REMOVE flag, ss",
            ReturnValues = ReturnValue.UPDATED_OLD,
        })).Attributes;
        var wantOld = Item(("flag", Bool(false)), ("ss", SS("é", "z", "a")));
        Ensure(Py.Equal(Norm(old), Norm(wantOld)), $"UpdateItem UPDATED_OLD: expected {Py.Repr(wantOld)}, got {Py.Repr(old)}");

        var expected = ItemValue().Where(kv => kv.Key is not ("flag" or "ss" or "deep")).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var wantNew = WantNew();
        foreach (var name in new[] { "deep", "el", "s", "ns", "bs" })
        {
            expected[name] = wantNew[name];
        }

        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(Norm(got), Norm(expected)), $"GetItem after the updates: expected {Py.Repr(expected)}, got {Py.Repr(got)}");

        await ddb.DeleteTableAsync(table);
        var clock = FidelityTime.StartNew();
        string? last;
        while (true)
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table }));
            if (error is not null)
            {
                if (error.Code != ErrResourceNotFound)
                {
                    throw error.Exception;
                }

                Ensure(
                    error.Message == $"Requested resource not found: Table: {table} not found",
                    $"DescribeTable on the deleted table: got the message {Py.Repr(error.Message)}");
                break;
            }

            last = resp!.Table.TableStatus.Value;
            if (clock.Elapsed.TotalSeconds > PollBudgetS)
            {
                throw Fail($"table {table} still describable after DeleteTable; last TableStatus {Py.Repr(last)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        var names = new List<string>();
        string? start = null;
        do
        {
            var page = await ddb.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = start });
            names.AddRange(page.TableNames ?? []);
            start = page.LastEvaluatedTableName;
        }
        while (start is not null);

        Ensure(!names.Contains(table), $"deleted table {table} still listed by ListTables");
        var getExc = await ExpectError(() => ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true }), ErrResourceNotFound);
        Ensure(getExc.Message == "Requested resource not found", $"GetItem on the deleted table: got the message {Py.Repr(getExc.Message)}");
        var queryExc = await ExpectError(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "tenant = :t",
                ExpressionAttributeValues = Item((":t", N("42"))),
            }),
            ErrResourceNotFound);
        Ensure(queryExc.Message == "Requested resource not found", $"Query on the deleted table: got the message {Py.Repr(queryExc.Message)}");
        var updateExc = await ExpectError(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET s = :s",
                ExpressionAttributeValues = Item((":s", S("after-delete"))),
            }),
            ErrResourceNotFound);
        Ensure(updateExc.Message == "Requested resource not found", $"UpdateItem on the deleted table: got the message {Py.Repr(updateExc.Message)}");
    }

    /// <summary>
    /// An item or attribute value with string, number and binary sets as sets (DynamoDB keeps no
    /// set order); every other type, numbers included, compares exactly.
    /// </summary>
    private static object? Norm(Dictionary<string, AttributeValue>? item) =>
        item?.ToDictionary(kv => kv.Key, kv => Norm(kv.Value), StringComparer.Ordinal);

    private static object? Norm(AttributeValue value)
    {
        if (value.SS is not null)
        {
            return new Dictionary<string, object?> { ["SS"] = value.SS.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() };
        }

        if (value.NS is not null)
        {
            return new Dictionary<string, object?> { ["NS"] = value.NS.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() };
        }

        if (value.BS is not null)
        {
            return new Dictionary<string, object?> { ["BS"] = value.BS.Select(b => Convert.ToHexString(b.ToArray())).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() };
        }

        if (value.M is not null)
        {
            return new Dictionary<string, object?> { ["M"] = Norm(value.M) };
        }

        if (value.L is not null)
        {
            return new Dictionary<string, object?> { ["L"] = value.L.Select(Norm).ToList() };
        }

        return Py.Boto(value);
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != ErrResourceNotFound)
        {
            throw error.Exception;
        }
    }

    /// <summary>Grade a ClientError on code, HTTP status class (4xx vs 5xx), and a non-empty message.</summary>
    private static void AssertError(AwsError error, string[] codes, int status = 400)
    {
        var (code, msg) = (error.Code, error.Message ?? "");
        var gotStatus = (int)error.StatusCode;
        Ensure(codes.Contains(code), $"expected error code in {Py.Repr(codes)}, got {Py.Repr(code)}: {Py.Repr(msg)}");
        var lo = status / 100 * 100;
        Ensure(
            lo <= gotStatus && gotStatus < lo + 100,
            $"expected an HTTP {lo / 100}xx (AWS answers {status}) with {code}, got {Py.Repr(gotStatus)}");
        Ensure(msg.Trim().Length > 0, $"expected a non-empty message with {code}");
    }

    /// <summary>Call fn() and grade the ClientError it raises; an accepted call is itself a failure.</summary>
    private static async Task<AwsError> ExpectError<T>(Func<Task<T>> fn, params string[] codes)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            AssertError(error, codes);
            return error;
        }

        throw Fail($"expected {Py.Repr(codes)}, but the call succeeded: {Py.Repr(result)}");
    }

    /// <summary>Poll DescribeTable until the table is ACTIVE; on expiry fail with the last status read.</summary>
    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string table, double timeoutS = WaitS)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"table {table} not ACTIVE within {timeoutS.ToString("F0", CultureInfo.InvariantCulture)}s; last TableStatus {Py.Repr(status)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }
}
