// Translated from behavioral/dynamodb/check_conc_ddb_optimistic_cancelled_token_is_not_burned.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.conc-ddb-optimistic-cancelled-token-is-not-burned</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcDdbOptimisticCancelledTokenIsNotBurned
{
    private const string Check = "conc-ddb-optimistic-cancelled-token-is-not-burned";
    private const string ErrCanceled = "TransactionCanceledException";
    private const string ErrMismatch = "IdempotentParameterMismatchException";
    private const string MsgCanceled = "Transaction cancelled, please refer cancellation reasons for specific reasons " +
                                       "[ConditionalCheckFailed, None]";
    private const string MsgMismatch = "Specified idempotent token was used with different request parameters within the " +
                                       "idempotency window";

    /// <summary>
    /// A ClientRequestToken whose first TransactWriteItems was cancelled is not consumed: the identical
    /// request later commits under it, a further replay applies nothing again, and reuse with a refreshed
    /// version fails with IdempotentParameterMismatchException; each refusal carries AWS's exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.conc-ddb-optimistic-cancelled-token-is-not-burned")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await MakeTable(ctx);
        await ddb.PutItemAsync(table, Item(("pk", S("acct")), ("version", N("1")), ("n", N("0"))));
        var token = Guid.NewGuid().ToString();

        Func<Task> Tx(int expected) => () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            ClientRequestToken = token,
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S("acct"))),
                        UpdateExpression = "SET version = :next ADD n :one",
                        ConditionExpression = "version = :expected",
                        ExpressionAttributeValues = Item(
                            (":expected", N(expected.ToString(CultureInfo.InvariantCulture))),
                            (":next", N((expected + 1).ToString(CultureInfo.InvariantCulture))),
                            (":one", N("1"))),
                    },
                },
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("receipt"))) } },
            ],
        });

        async Task<(int Version, int N, bool Receipt)> State()
        {
            var acct = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("acct"))), ConsistentRead = true })).Item;
            var receipt = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("receipt"))), ConsistentRead = true })).Item;
            return (int.Parse(acct["version"].N, CultureInfo.InvariantCulture), int.Parse(acct["n"].N, CultureInfo.InvariantCulture), receipt is not null);
        }

        // 1. first use expects version 2 while the item is at 1: cancelled, nothing applies.
        var error = await Error(Tx(2));
        Ensure(error.Code == ErrCanceled, $"first use: expected {ErrCanceled}, got {Py.Repr(error.Code)}");
        Ensure(error.Message == MsgCanceled, $"first use: expected {Py.Repr(MsgCanceled)}, got {Py.Repr(error.Message)}");
        var reasons = (error.CancellationReasons() ?? []).Select(Reason).ToList();
        Ensure(
            Py.Equal(reasons, new[]
            {
                new Dictionary<string, object?> { ["Code"] = "ConditionalCheckFailed", ["Message"] = "The conditional request failed" },
                new Dictionary<string, object?> { ["Code"] = "None" },
            }),
            $"first use reasons {Py.Repr(reasons)}");
        Ensure(await State() == (1, 0, false), $"cancelled first use applied something: {Py.Repr(await State())}");

        // 2. another writer moves the item to the version the request expects.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("acct"))),
            UpdateExpression = "SET version = :v",
            ExpressionAttributeValues = Item((":v", N("2"))),
        });

        // 3. same token, same bytes: the cancelled use did not burn it, so this COMMITS.
        await Tx(2)();
        Ensure(
            await State() == (3, 1, true),
            $"same-token retry after the fix must apply once (version 3, n 1, receipt); got {Py.Repr(await State())} " +
            "-- a token remembered from a cancelled call swallows the retry");

        // 4. replay of the committed request: success, NOT applied again.
        await Tx(2)();
        Ensure(await State() == (3, 1, true), $"replay of a committed token re-applied: {Py.Repr(await State())}");

        // 5. same token, refreshed version (different parameters): refused, nothing applies.
        error = await Error(Tx(3));
        Ensure(error.Code == ErrMismatch, $"token reuse with a refreshed version: expected {ErrMismatch}, got {Py.Repr(error.Code)}");
        Ensure(error.Message == MsgMismatch, $"token reuse with a refreshed version: expected {Py.Repr(MsgMismatch)}, got {Py.Repr(error.Message)}");
        Ensure(await State() == (3, 1, true), $"mismatched token reuse applied something: {Py.Repr(await State())}");
    }

    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-conc-ddb-opt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    /// <summary>The error of a call real AWS refuses. RAISES on unexpected success.</summary>
    private static async Task<AwsError> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        return error ?? throw Fail("real AWS refuses this call, but it SUCCEEDED");
    }

    /// <summary>A cancellation reason as boto3 hands it back: only the members present.</summary>
    private static Dictionary<string, object?> Reason(CancellationReason r)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (r.Item is not null)
        {
            d["Item"] = r.Item;
        }

        if (r.Code is not null)
        {
            d["Code"] = r.Code;
        }

        if (r.Message is not null)
        {
            d["Message"] = r.Message;
        }

        return d;
    }
}
