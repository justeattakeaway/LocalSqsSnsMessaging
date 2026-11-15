// Translated from behavioral/dynamodb/check_conc_ddb_optimistic_transact_check_and_update_same_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.conc-ddb-optimistic-transact-check-and-update-same-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcDdbOptimisticTransactCheckAndUpdateSameKeyRejected
{
    private const string Check = "conc-ddb-optimistic-transact-check-and-update-same-key-rejected";
    private const string ErrValidation = "ValidationException";
    private const string ErrCanceled = "TransactionCanceledException";

    /// <summary>
    /// A transaction with a ConditionCheck and an Update on the same item fails with ValidationException
    /// "... multiple operations on one item" before any member applies, the folded Update-with-condition
    /// commits, and the same folded form with a stale version is cancelled and applies nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.conc-ddb-optimistic-transact-check-and-update-same-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await MakeTable(ctx);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("order")));
        await ddb.PutItemAsync(table, Item(("pk", S("order")), ("version", N("1"))));

        async Task<Dictionary<string, AttributeValue>?> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        Func<Task<TransactWriteItemsResponse>> Folded(int expected, string marker) => () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Key(),
                        UpdateExpression = "SET version = :next",
                        ConditionExpression = "version = :expected",
                        ExpressionAttributeValues = Item(
                            (":expected", N(expected.ToString(CultureInfo.InvariantCulture))),
                            (":next", N((expected + 1).ToString(CultureInfo.InvariantCulture)))),
                    },
                },
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S(marker))) } },
            ],
        });

        // --- the refused shape: a TRUE condition, split from its update onto the same key ----------
        var resp = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = Key(),
                        ConditionExpression = "version = :expected",
                        ExpressionAttributeValues = Item((":expected", N("1"))),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Key(),
                        UpdateExpression = "SET version = :next",
                        ExpressionAttributeValues = Item((":next", N("2"))),
                    },
                },
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("marker-split"))) } },
            ],
        }));
        Ensure(resp.Code == ErrValidation, $"ConditionCheck+Update on one key: expected {ErrValidation}, got {Py.Repr(resp.Code)}");
        Ensure(
            resp.Message == "Transaction request cannot include multiple operations on one item",
            $"ConditionCheck+Update on one key: got message {Py.Repr(resp.Message)}");
        Ensure(
            resp.CancellationReasons() is null,
            "a request-level validation refusal must not report per-item reasons; got " +
            $"{Py.Repr(resp.CancellationReasons()?.Select(Reason))}");
        Ensure(
            Py.Equal(await Get("order"), Item(("pk", S("order")), ("version", N("1")))),
            $"the rejected transaction moved the version: {Py.Repr(await Get("order"))}");
        Ensure(await Get("marker-split") is null, $"the rejected transaction wrote its unrelated Put: {Py.Repr(await Get("marker-split"))}");

        // --- positive control: condition folded into the Update commits, both members apply ---------
        var ddbResp = await Folded(1, "marker-folded")();
        Ensure((int)ddbResp.HttpStatusCode == 200, $"folded transaction: {Py.Repr((int)ddbResp.HttpStatusCode)}");
        Ensure(Py.Equal((await Get("order"))!["version"], N("2")), $"folded commit did not bump: {Py.Repr(await Get("order"))}");
        Ensure(await Get("marker-folded") is not null, "folded commit did not write its second member");

        // --- and the folded form with a STALE version is cancelled (a different refusal) ------------
        resp = await Error(Folded(1, "marker-stale"));
        Ensure(resp.Code == ErrCanceled, $"stale folded transaction: expected {ErrCanceled}, got {Py.Repr(resp.Code)}");
        const string expectedMsg = "Transaction cancelled, please refer cancellation reasons for specific reasons " +
                                   "[ConditionalCheckFailed, None]";
        Ensure(resp.Message == expectedMsg, $"stale folded transaction: expected {Py.Repr(expectedMsg)}, got {Py.Repr(resp.Message)}");
        var reasons = (resp.CancellationReasons() ?? []).Select(Reason).ToList();
        Ensure(
            Py.Equal(reasons, new[]
            {
                new Dictionary<string, object?> { ["Code"] = "ConditionalCheckFailed", ["Message"] = "The conditional request failed" },
                new Dictionary<string, object?> { ["Code"] = "None" },
            }),
            $"stale folded reasons {Py.Repr(reasons)}");
        Ensure(
            Py.Equal((await Get("order"))!["version"], N("2")) && await Get("marker-stale") is null,
            $"stale cancelled transaction applied something: order={Py.Repr(await Get("order"))} " +
            $"marker={Py.Repr(await Get("marker-stale"))}");
    }

    /// <summary>The error of a call real AWS refuses. RAISES on unexpected success.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS refuses this transaction, but it SUCCEEDED and returned {Py.Repr(result)}");
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
