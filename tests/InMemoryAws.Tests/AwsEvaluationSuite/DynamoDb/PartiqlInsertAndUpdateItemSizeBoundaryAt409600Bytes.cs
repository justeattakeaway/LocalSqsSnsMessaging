// Translated from behavioral/dynamodb/check_partiql_insert_and_update_item_size_boundary_at_409600_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-insert-and-update-item-size-boundary-at-409600-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlInsertAndUpdateItemSizeBoundaryAt409600Bytes
{
    private const string CheckId = "partiql-insert-and-update-item-size-boundary-at-409600-bytes";
    private const int LimitBytes = 409600;
    private static readonly (string Code, string Message) InsertOver = ("ValidationException", "Item size has exceeded the maximum allowed size");
    private static readonly (string Code, string Message) UpdateOver = ("ValidationException", "Item size to update has exceeded the maximum allowed size");

    /// <summary>The filler-attribute byte length so a one-pk item's encoded size is exactly totalBytes.</summary>
    private static int FillerLen(string pkValue, string fillerAttr, int totalBytes)
    {
        var overhead = Encoding.UTF8.GetByteCount("pk") + Encoding.UTF8.GetByteCount(pkValue) + Encoding.UTF8.GetByteCount(fillerAttr);
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for this item shape");
        return fillerLen;
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)} ({Py.Repr(error.Message)})");
            Ensure(error.Message == expected.Message, $"unexpected message {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected the call to be refused with {expected.Code}, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// A PartiQL INSERT stores an item at exactly the 409600-byte limit and refuses one byte
    /// over with the put wording; a PartiQL UPDATE growing to exactly 409600 bytes stores it too,
    /// refusing one byte past with the update wording, and leaving the refused item untouched.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-insert-and-update-item-size-boundary-at-409600-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-partiql-size-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        async Task<GetItemResponse> Get(string pk) =>
            await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true });

        // INSERT: exactly at the limit is accepted and reads back whole.
        const string insertAtPk = "insert-at-limit";
        var insertAtValue = new string('x', FillerLen(insertAtPk, "data", LimitBytes));
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{table}\" VALUE {{'pk': ?, 'data': ?}}",
            Parameters = [S(insertAtPk), S(insertAtValue)],
        });
        var got = (await Get(insertAtPk)).Item ?? [];
        Ensure(
            got.GetValueOrDefault("data")?.S == insertAtValue,
            $"a {LimitBytes}-byte item must round-trip whole through PartiQL INSERT, got {(got.GetValueOrDefault("data")?.S ?? "").Length} bytes back");

        // INSERT: one byte over the limit is refused and nothing is stored.
        const string insertOverPk = "insert-over-limit";
        var insertOverValue = new string('x', FillerLen(insertOverPk, "data", LimitBytes + 1));
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"INSERT INTO \"{table}\" VALUE {{'pk': ?, 'data': ?}}",
                Parameters = [S(insertOverPk), S(insertOverValue)],
            }),
            InsertOver);
        var rejected = await Get(insertOverPk);
        Ensure(rejected.Item is null, $"a PartiQL INSERT refused for size must not persist, got {Py.Repr(rejected.Item)}");

        // UPDATE: growing to exactly the limit is accepted and reads back whole.
        const string updateAtPk = "update-at-limit";
        await ddb.PutItemAsync(table, Item(("pk", S(updateAtPk))));
        var updateAtValue = new string('x', FillerLen(updateAtPk, "bigattr", LimitBytes));
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET bigattr = ? WHERE pk = ?",
            Parameters = [S(updateAtValue), S(updateAtPk)],
        });
        var updated = (await Get(updateAtPk)).Item ?? [];
        Ensure(
            updated.GetValueOrDefault("bigattr")?.S == updateAtValue,
            $"growing an item to exactly {LimitBytes} bytes via PartiQL UPDATE must store whole, got {(updated.GetValueOrDefault("bigattr")?.S ?? "").Length} bytes back");

        // UPDATE: growing one byte past the limit is refused and the item is left unchanged.
        const string updateOverPk = "update-over-limit";
        await ddb.PutItemAsync(table, Item(("pk", S(updateOverPk))));
        var updateOverValue = new string('x', FillerLen(updateOverPk, "bigattr", LimitBytes + 1));
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"UPDATE \"{table}\" SET bigattr = ? WHERE pk = ?",
                Parameters = [S(updateOverValue), S(updateOverPk)],
            }),
            UpdateOver);
        var unchanged = (await Get(updateOverPk)).Item ?? [];
        Ensure(!unchanged.ContainsKey("bigattr"), $"a PartiQL UPDATE refused for size must leave the item unchanged, got {Py.Repr(unchanged)}");
    }
}
