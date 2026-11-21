// Translated from behavioral/dynamodb/check_legacy_attribute_updates_add_binary_scalar_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-attribute-updates-add-binary-scalar-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyAttributeUpdatesAddBinaryScalarRejected
{
    private const string Check = "legacy-attribute-updates-add-binary-scalar-rejected";

    private static readonly (string Code, string Message) AddOnBRejected = (
        "ValidationException",
        "1 validation error detected: One or more parameter values were invalid: " +
        "ADD action is not supported for the type B");

    /// <summary>
    /// Legacy AttributeUpdates ADD on a Binary (B) scalar attribute is refused, leaving the item
    /// unchanged, while the same Action on a Binary Set (BS) attribute still applies.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-attribute-updates-add-binary-scalar-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-addb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, table);

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("item1")));
        await ddb.PutItemAsync(table, Item(
            ("pk", S("item1")),
            ("blob", B("x")),
            ("blobs", BS("a"u8.ToArray()))));

        var exc = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate>
            {
                ["blob"] = new() { Action = AttributeAction.ADD, Value = B("y") },
            },
        }));
        var (code, msg) = (exc.Code, exc.Message);
        Ensure(
            (code, msg) == AddOnBRejected,
            $"ADD onto a Binary (B) attribute: expected {Py.Repr(AddOnBRejected)}, got {Py.Repr((code, msg))}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            unchanged is not null && Py.Equal(unchanged.GetValueOrDefault("blob"), B("x")),
            $"'blob' changed by the rejected ADD: {Py.Repr(unchanged?.GetValueOrDefault("blob"))}, expected b'x' unchanged");

        var newBlobs = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate>
            {
                ["blobs"] = new() { Action = AttributeAction.ADD, Value = BS("z"u8.ToArray()) },
            },
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            newBlobs is not null && SetOf(newBlobs["blobs"].BS).SetEquals(["a", "z"]),
            $"positive control: ADD BS {{z}} onto existing {{a}} with UPDATED_NEW returned {Py.Repr(newBlobs)}, " +
            "expected the union {a,z}");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            item is not null && SetOf(item["blobs"].BS).SetEquals(["a", "z"]),
            $"final blobs read back as {Py.Repr(item?.GetValueOrDefault("blobs"))}, expected {{a,z}} after the positive control");
    }

    /// <summary>A Binary Set's members as text, for set comparison.</summary>
    private static HashSet<string> SetOf(List<MemoryStream>? members) =>
        [.. (members ?? []).Select(m => System.Text.Encoding.UTF8.GetString(m.ToArray()))];

    /// <summary>Poll DescribeTable until the table is ACTIVE; on expiry fail with the last status read.</summary>
    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double timeoutS = 40.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"table {table} not ACTIVE within {timeoutS:F0}s; last TableStatus {Py.Repr(status)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>The ClientError <c>fn</c> raises. An unexpected success raises too, naming what came back.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
