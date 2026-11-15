// Translated from behavioral/dynamodb/check_partiql_typed_item_insert_update_select_then_deleted_table_refuses_statements.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-typed-item-insert-update-select-then-deleted-table-refuses-statements</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlTypedItemInsertUpdateSelectThenDeletedTableRefusesStatements
{
    private const string CheckId = "partiql-typed-item-insert-update-select-then-deleted-table-refuses-statements";
    private const string ErrResourceNotFound = "ResourceNotFoundException";

    /// <summary>Bound on each asynchronous table transition; below the harness ceiling.</summary>
    private const double PollBudgetS = 40.0;

    /// <summary>Bound on each wait; a target that needs longer fails with the last answer it gave.</summary>
    private const double WaitS = 40.0;

    /// <summary>
    /// A typed item inserted by PartiQL reads back exactly through GetItem and SELECT, a second
    /// INSERT of its key is refused, UPDATE ... RETURNING ALL NEW applies SET/REMOVE on nested paths;
    /// the deleted table refuses SELECT and INSERT statements.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-typed-item-insert-update-select-then-deleted-table-refuses-statements")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, table));
        await WaitTableActive(ddb, table);

        var nonce = FidelityContext.UniqueName("doc-");
        var item = Item(
            ("id", S(nonce)),
            ("n", N("-12.5")),
            ("b", B([0x80, 0x00, 0xff])),
            ("flag", Bool(false)),
            ("nul", Null()),
            ("l", L(S("p"), N("0"), M(("x", S("y"))))),
            ("m", M(("sub", M(("lvl", N("2")))), ("k", S("v")))),
            ("ns", NS("4", "8")),
            ("bs", BS([0x01], [0x02])));
        var order = item.Keys.ToList();
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{table}\" VALUE {{" + string.Join(", ", order.Select(k => $"'{k}': ?")) + "}",
            Parameters = [.. order.Select(k => item[k])],
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("id", S(nonce))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(Norm(got), Norm(item)), $"GetItem after the PartiQL INSERT: expected {Py.Repr(item)}, got {Py.Repr(got)}");

        var dup = await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"INSERT INTO \"{table}\" VALUE {{'id': ?, 'n': ?}}",
                Parameters = [S(nonce), N("1")],
            }),
            ["DuplicateItemException"]);
        var dupMessage = dup.Message;
        Ensure(dupMessage == "Duplicate primary key exists in table", $"a second INSERT of the same key: got the message {Py.Repr(dupMessage)}");

        var updated = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET n = n + ? SET m.sub.lvl = ? SET l[1] = ? REMOVE flag REMOVE l[2] WHERE id = ? RETURNING ALL NEW *",
            Parameters = [N("0.5"), S("replaced"), Bool(true), S(nonce)],
        })).Items;
        var expected = item.Where(e => e.Key != "flag").ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        expected["n"] = N("-12");
        expected["m"] = M(("sub", M(("lvl", S("replaced")))), ("k", S("v")));
        expected["l"] = L(S("p"), Bool(true));
        Ensure(
            Py.Equal(updated.Select(Norm).ToList(), new[] { Norm(expected) }),
            $"UPDATE RETURNING ALL NEW: expected [{Py.Repr(expected)}], got {Py.Repr(updated)}");
        var selected = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE id = ?",
            Parameters = [S(nonce)],
            ConsistentRead = true,
        })).Items;
        Ensure(
            Py.Equal(selected.Select(Norm).ToList(), new[] { Norm(expected) }),
            $"SELECT after the UPDATE: expected [{Py.Repr(expected)}], got {Py.Repr(selected)}");

        await ddb.DeleteTableAsync(table);
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (true)
        {
            var (described, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(table));
            if (error is not null)
            {
                if (error.Code != ErrResourceNotFound)
                {
                    throw error.Exception;
                }

                var message = error.Message;
                Ensure(
                    message == $"Requested resource not found: Table: {table} not found",
                    $"DescribeTable on the deleted table: got the message {Py.Repr(message)}");
                break;
            }

            last = described!.Table.TableStatus;
            if (clock.Elapsed.TotalSeconds > PollBudgetS)
            {
                throw Fail($"table {table} still describable after DeleteTable; last TableStatus {Py.Repr(last)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        var selExc = await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{table}\" WHERE id = ?",
                Parameters = [S(nonce)],
                ConsistentRead = true,
            }),
            [ErrResourceNotFound]);
        var selMessage = selExc.Message;
        Ensure(selMessage == "Requested resource not found", $"SELECT on the deleted table: got the message {Py.Repr(selMessage)}");
        var insExc = await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"INSERT INTO \"{table}\" VALUE {{'id': ?}}",
                Parameters = [S(nonce)],
            }),
            [ErrResourceNotFound]);
        var insMessage = insExc.Message;
        Ensure(insMessage == "Requested resource not found", $"INSERT on the deleted table: got the message {Py.Repr(insMessage)}");
    }

    /// <summary>
    /// An item or attribute value with string, number and binary sets as sets (DynamoDB keeps no
    /// set order); every other type, numbers included, compares exactly.
    /// </summary>
    private static Dictionary<string, AttributeValue>? Norm(Dictionary<string, AttributeValue>? item) =>
        item?.ToDictionary(e => e.Key, e => NormValue(e.Value), StringComparer.Ordinal);

    private static AttributeValue NormValue(AttributeValue value)
    {
        if (value.SS is not null)
        {
            return SS([.. value.SS.Order(StringComparer.Ordinal)]);
        }

        if (value.NS is not null)
        {
            return NS([.. value.NS.Order(StringComparer.Ordinal)]);
        }

        if (value.BS is not null)
        {
            return BS([.. value.BS.Select(b => b.ToArray()).OrderBy(Convert.ToHexString, StringComparer.Ordinal)]);
        }

        if (value.M is not null)
        {
            return new AttributeValue { M = Norm(value.M) };
        }

        if (value.L is not null)
        {
            return L([.. value.L.Select(NormValue)]);
        }

        return value;
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
    private static void AssertError(AwsError exc, string[] codes, int status = 400)
    {
        var code = exc.Code;
        var msg = exc.Message ?? "";
        var gotStatus = (int)exc.StatusCode;
        Ensure(codes.Contains(code), $"expected error code in {Py.Repr(codes)}, got {Py.Repr(code)}: {Py.Repr(msg)}");
        var lo = status / 100 * 100;
        Ensure(lo <= gotStatus && gotStatus < lo + 100, $"expected an HTTP {lo / 100}xx (AWS answers {status}) with {code}, got {gotStatus}");
        Ensure(!string.IsNullOrWhiteSpace(msg), $"expected a non-empty message with {code}");
    }

    /// <summary>Call fn() and grade the ClientError it raises; an accepted call is itself a failure.</summary>
    private static async Task<AwsError> ExpectError<T>(Func<Task<T>> fn, string[] codes, int status = 400)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            AssertError(error, codes, status);
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
            string status = (await ddb.DescribeTableAsync(table)).Table.TableStatus;
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
