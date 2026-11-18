// Translated from behavioral/dynamodb/check_create_describe_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-describe-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateDescribeDelete
{
    private const string CheckId = "create-describe-delete";
    private const string ErrResourceNotFound = "ResourceNotFoundException";

    /// <summary>Bound on each asynchronous table transition; below the harness ceiling.</summary>
    private const double PollBudgetSeconds = 180.0;

    private static readonly string[] ResourceNotFound = [ErrResourceNotFound];
    private static readonly byte[] BinaryValue = [0x00, 0xff, 0x10, 0x80];

    /// <summary>
    /// CreateTable reports its schema and billing mode; a typed item round-trips and UpdateItem
    /// applies SET/ADD/REMOVE; the deleted table reads not-found, is unlisted and refuses writes.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-describe-delete")]
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
        // table_exists waiter bound: 2 s x 90 attempts.
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 90);
        var desc = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(desc.TableName == table, $"expected TableName {Py.Repr(table)}, got {Py.Repr(desc.TableName)}");
        Ensure(desc.TableStatus == "ACTIVE", $"expected TableStatus ACTIVE, got {Py.Repr(desc.TableStatus?.Value)}");
        var keySchema = desc.KeySchema.ToDictionary(ks => ks.AttributeName, ks => (object?)ks.KeyType.Value, StringComparer.Ordinal);
        Ensure(
            Py.Equal(keySchema, new Dictionary<string, object?> { ["id"] = "HASH" }),
            $"expected key schema {{'id': 'HASH'}}, got {Py.Repr(keySchema)}");
        Ensure(desc.BillingModeSummary is not null, "expected BillingModeSummary on a PAY_PER_REQUEST table, got keys of a table description without it");
        var billing = desc.BillingModeSummary.BillingMode?.Value;
        Ensure(billing == "PAY_PER_REQUEST", $"expected BillingMode PAY_PER_REQUEST, got {Py.Repr(billing)}");

        // Use beat: one item of every common attribute type round-trips under a consistent read.
        // Numbers are written as canonical literals, so the string DynamoDB returns must match exactly.
        var nonce = FidelityContext.UniqueName("nonce-");
        var key = Item(("id", S(nonce)));
        var item = Item(
            ("id", S(nonce)),
            ("s", S("first")),
            ("n", N("15")),
            ("f", N("1.5")),
            ("b", B(BinaryValue)),
            ("flag", Bool(true)),
            ("nul", Null()),
            ("l", L(S("a"), N("2"))),
            ("m", M(("k", S("v")), ("neg", N("-3")))),
            ("ss", SS("x", "y", "z")));
        await ddb.PutItemAsync(table, item);
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(Norm(got), Norm(item)), $"expected the typed item back, got {Py.Repr(got)}");

        // UpdateItem SET a string, ADD to a number, REMOVE an attribute; ALL_NEW and a consistent
        // read both report exactly the updated item.
        var updated = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET #s = :s ADD #n :inc REMOVE #flag",
            ExpressionAttributeNames = Names(("#s", "s"), ("#n", "n"), ("#flag", "flag")),
            ExpressionAttributeValues = Item((":s", S("second")), (":inc", N("5"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        var expected = item.Where(e => e.Key != "flag").ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        expected["s"] = S("second");
        expected["n"] = N("20");
        Ensure(Py.Equal(Norm(updated), Norm(expected)), $"UpdateItem ALL_NEW: expected {Py.Repr(expected)}, got {Py.Repr(updated)}");
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(Norm(got), Norm(expected)), $"GetItem after UpdateItem: expected {Py.Repr(expected)}, got {Py.Repr(got)}");

        Ensure((await ListAllTables(ddb)).Contains(table), $"table {table} not listed by ListTables before the delete");
        await ddb.DeleteTableAsync(table);
        // Gone beat 1: DescribeTable reaches ResourceNotFoundException (the delete is asynchronous).
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(table));
            if (error is not null)
            {
                if (error.Code != ErrResourceNotFound)
                {
                    throw error.Exception;
                }

                AssertError(error, ResourceNotFound);
                var message = error.Message;
                Ensure(
                    message == $"Requested resource not found: Table: {table} not found",
                    $"DescribeTable on the deleted table: got the message {Py.Repr(message)}");
                break;
            }

            if (clock.Elapsed.TotalSeconds > PollBudgetSeconds)
            {
                throw Fail($"table {table} still describable after DeleteTable");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        // Gone beat 2: the paginated listing no longer contains the table.
        var names = await ListAllTables(ddb);
        Ensure(!names.Contains(table), $"deleted table {table} still listed by ListTables");

        // Gone beat 3: a write to the dead table is refused, and so is a second delete.
        var putExc = await ExpectError(() => ddb.PutItemAsync(table, Item(("id", S(nonce)))), ResourceNotFound);
        var putMessage = putExc.Message;
        Ensure(putMessage == "Requested resource not found", $"PutItem on the deleted table: got the message {Py.Repr(putMessage)}");
        var deleteExc = await ExpectError(() => ddb.DeleteTableAsync(table), ResourceNotFound);
        var deleteMessage = deleteExc.Message;
        Ensure(
            deleteMessage == $"Requested resource not found: Table: {table} not found",
            $"a second DeleteTable: got the message {Py.Repr(deleteMessage)}");
    }

    private static async Task<List<string>> ListAllTables(IAmazonDynamoDB ddb)
    {
        var names = new List<string>();
        string? start = null;
        do
        {
            var page = await ddb.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = start });
            names.AddRange(page.TableNames ?? []);
            start = page.LastEvaluatedTableName;
        }
        while (start is not null);

        return names;
    }

    /// <summary>
    /// An item or attribute value with string and number sets as sets (DynamoDB keeps no set
    /// order); every other type, numbers included, compares exactly.
    /// </summary>
    private static object? Norm(object? value)
    {
        switch (value)
        {
            case Dictionary<string, AttributeValue> item:
                return item.ToDictionary(e => e.Key, e => Norm(e.Value), StringComparer.Ordinal);
            case AttributeValue attribute:
            {
                var boto = Py.Boto(attribute);
                return boto.ToDictionary(
                    e => e.Key,
                    e => e.Key switch
                    {
                        "SS" or "NS" => ((List<string>)e.Value!).Order(StringComparer.Ordinal).ToList(),
                        "BS" => ((List<byte[]>)e.Value!).Select(Convert.ToHexString).Order(StringComparer.Ordinal).ToList(),
                        _ => Norm(e.Value),
                    },
                    StringComparer.Ordinal);
            }

            case List<AttributeValue> list:
                return list.Select(Norm).ToList();
            default:
                return value;
        }
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
        var code = error.Code;
        var msg = error.Message ?? string.Empty;
        var gotStatus = (int)error.StatusCode;
        Ensure(codes.Contains(code), $"expected error code in {Py.Repr(codes.ToList())}, got {Py.Repr(code)}: {Py.Repr(msg)}");
        var lo = status / 100 * 100;
        Ensure(lo <= gotStatus && gotStatus < lo + 100, $"expected an HTTP {lo / 100}xx (AWS answers {status}) with {code}, got {gotStatus}");
        Ensure(msg.Trim().Length > 0, $"expected a non-empty message with {code}");
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

        throw Fail($"expected {Py.Repr(codes.ToList())}, but the call succeeded: {result}");
    }
}
