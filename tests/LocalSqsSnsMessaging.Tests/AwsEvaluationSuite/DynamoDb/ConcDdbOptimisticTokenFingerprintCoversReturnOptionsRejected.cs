// Translated from behavioral/dynamodb/check_conc_ddb_optimistic_token_fingerprint_covers_return_options_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.conc-ddb-optimistic-token-fingerprint-covers-return-options-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcDdbOptimisticTokenFingerprintCoversReturnOptionsRejected
{
    private const string Check = "conc-ddb-optimistic-token-fingerprint-covers-return-options-rejected";
    private const string ErrMismatch = "IdempotentParameterMismatchException";

    /// <summary>
    /// A TransactWriteItems token replay that changes ReturnConsumedCapacity or
    /// ReturnItemCollectionMetrics is refused IdempotentParameterMismatchException; the exact replay
    /// succeeds billed as a read, and the ADD stays applied exactly once.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.conc-ddb-optimistic-token-fingerprint-covers-return-options-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await MakeTable(ctx);
        await ddb.PutItemAsync(table, Item(("pk", S("ctr")), ("n", N("0"))));
        var token = Guid.NewGuid().ToString();
        List<TransactWriteItem> Items() =>
        [
            new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = table,
                    Key = Item(("pk", S("ctr"))),
                    UpdateExpression = "ADD n :one",
                    ExpressionAttributeValues = Item((":one", N("1"))),
                },
            },
        ];

        async Task<AttributeValue> Ncount() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("ctr"))), ConsistentRead = true })).Item["n"];

        Func<Task<TransactWriteItemsResponse>> Tx(ReturnConsumedCapacity? returnConsumedCapacity = null, ReturnItemCollectionMetrics? returnItemCollectionMetrics = null) =>
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                ClientRequestToken = token,
                TransactItems = Items(),
                ReturnConsumedCapacity = returnConsumedCapacity,
                ReturnItemCollectionMetrics = returnItemCollectionMetrics,
            });

        var first = await Tx(ReturnConsumedCapacity.TOTAL)();
        var firstCc = first.ConsumedCapacity[0];
        Ensure((firstCc.WriteCapacityUnits ?? 0) > 0, $"first use was not billed as a write: {Py.Repr((firstCc.ReadCapacityUnits, firstCc.WriteCapacityUnits))}");
        Ensure(Py.Equal(await Ncount(), N("1")), $"first use did not apply the ADD: {Py.Repr(await Ncount())}");

        // Positive control: the exact replay is accepted, billed as a read, and does not re-apply.
        var replay = await Tx(ReturnConsumedCapacity.TOTAL)();
        var replayCc = replay.ConsumedCapacity[0];
        Ensure(
            replayCc.WriteCapacityUnits is null && (replayCc.ReadCapacityUnits ?? 0) > 0,
            $"an exact replay must be billed as a read with no write units; got {Py.Repr((replayCc.ReadCapacityUnits, replayCc.WriteCapacityUnits))}");
        Ensure(Py.Equal(await Ncount(), N("1")), $"exact replay re-applied the ADD: {Py.Repr(await Ncount())}");

        // Same TransactItems, capacity reporting turned OFF: a different request.
        var (code, message) = await ErrorCode(Tx(ReturnConsumedCapacity.NONE));
        Ensure(code == ErrMismatch, $"ReturnConsumedCapacity changed on replay: expected {ErrMismatch}, got {Py.Repr(code)}");
        Ensure(
            message == "Specified idempotent token was used with different request parameters within the idempotency window",
            $"expected AWS's exact IdempotentParameterMismatchException message, got {Py.Repr(message)}");
        Ensure(Py.Equal(await Ncount(), N("1")), $"mismatched replay applied the ADD: {Py.Repr(await Ncount())}");

        // Same TransactItems and capacity option, collection metrics ADDED: also a different request.
        (code, message) = await ErrorCode(Tx(ReturnConsumedCapacity.TOTAL, ReturnItemCollectionMetrics.SIZE));
        Ensure(code == ErrMismatch, $"ReturnItemCollectionMetrics added on replay: expected {ErrMismatch}, got {Py.Repr(code)}");
        Ensure(
            message == "Specified idempotent token was used with different request parameters within the idempotency window",
            $"expected AWS's exact IdempotentParameterMismatchException message, got {Py.Repr(message)}");
        Ensure(Py.Equal(await Ncount(), N("1")), $"mismatched replay applied the ADD: {Py.Repr(await Ncount())}");

        // And a fresh token with the same items is new work: it applies.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { ClientRequestToken = Guid.NewGuid().ToString(), TransactItems = Items() });
        Ensure(Py.Equal(await Ncount(), N("2")), $"a fresh token must apply the ADD again: {Py.Repr(await Ncount())}");
    }

    /// <summary>The error Code and Message for a call real AWS refuses. RAISES on unexpected success.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS refuses this replay, but it SUCCEEDED and returned {Py.Repr(result)}");
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
}
