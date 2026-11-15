// Translated from behavioral/dynamodb/check_item_size_400kb_limit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.item-size-400kb-limit</summary>
[Category("AwsEvaluationSuite")]
public sealed class ItemSize400kbLimit
{
    private const string CheckId = "item-size-400kb-limit";
    private const string ErrValidation = "ValidationException";
    private static readonly string[] ValidationCodes = [ErrValidation];

    /// <summary>
    /// PutItem of a 400 KB attribute raises ValidationException with the exact message and stores nothing, while a
    /// small item and a 399 KB item on the same table read back whole.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.item-size-400kb-limit")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        // Positive control: a small item on the same wiring must succeed, so a target that
        // rejects every PutItem (not just oversized ones) cannot pass this check.
        await ddb.PutItemAsync(table, Item(("pk", S("small")), ("data", S("ok"))));
        var control = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("small"))), ConsistentRead = true });
        Ensure(control.Item?.GetValueOrDefault("data")?.S == "ok", $"positive control PutItem/GetItem round-trip failed, got {Py.Repr(control.Item)}");

        // The boundary itself: an item just under 400 KB (attribute names + values) is accepted and
        // reads back whole, so a target with any lower size cap fails here.
        var nearValue = new string('n', 399 * 1024);
        await ddb.PutItemAsync(table, Item(("pk", S("near")), ("data", S(nearValue))));
        var near = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("near"))), ConsistentRead = true });
        var nearLen = (near.Item?.GetValueOrDefault("data")?.S ?? "").Length;
        Ensure(nearLen == nearValue.Length, $"expected the {nearValue.Length}-byte near-limit attribute to read back whole, got {nearLen} bytes");

        var bigValue = new string('x', 400 * 1024);
        var exc = await ExpectError(() => ddb.PutItemAsync(table, Item(("pk", S("big")), ("data", S(bigValue)))), ValidationCodes);
        var message = exc.Message;
        Ensure(message == "Item size has exceeded the maximum allowed size", $"an over-400 KB PutItem: got the message {Py.Repr(message)}");

        // Independent read at the destination: the rejected write must not have persisted.
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("big"))), ConsistentRead = true });
        Ensure(got.Item is null, $"oversized item must not persist after rejection, got {Py.Repr(got.Item)}");
    }

    /// <summary>Grade a ClientError on code, HTTP status, a non-empty message and the ids it must name.</summary>
    private static void AssertError(AwsError exc, string[] codes, int status = 400, string[]? contains = null)
    {
        var code = exc.Code;
        var msg = exc.Message ?? "";
        var gotStatus = (int)exc.StatusCode;
        Ensure(codes.Contains(code), $"expected error code in {Py.Repr(codes)}, got {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(gotStatus == status, $"expected HTTP {status} with {code}, got {Py.Repr(gotStatus)}");
        Ensure(msg.Trim().Length > 0, $"expected a non-empty message with {code}");
        foreach (var want in contains ?? [])
        {
            Ensure(msg.Contains(want, StringComparison.Ordinal), $"expected the {code} message to name {Py.Repr(want)}, got {Py.Repr(msg)}");
        }
    }

    /// <summary>Call fn() and grade the ClientError it raises; an accepted call is itself a failure.</summary>
    private static async Task<AwsError> ExpectError<T>(Func<Task<T>> fn, string[] codes, int status = 400, string[]? contains = null)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            AssertError(error, codes, status, contains);
            return error;
        }

        throw Fail($"expected {Py.Repr(codes)}, but the call succeeded: {Py.Repr(result)}");
    }
}
