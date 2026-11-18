// Translated from behavioral/dynamodb/check_plain_write_racing_a_transaction_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Collections.Concurrent;
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.plain-write-racing-a-transaction-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PlainWriteRacingATransactionRejected
{
    private const string CheckId = "plain-write-racing-a-transaction-rejected";
    private const string ErrConflict = "TransactionConflictException";
    private const int Transactors = 8;
    private const int PlainWriters = 8;

    /// <summary>Contention rounds run before the check settles for the counter assertion alone.</summary>
    private const int MaxRounds = 25;

    /// <summary>Wall bound on the contention rounds; the run budget caps it further.</summary>
    private const double ContentionS = 120;

    /// <summary>
    /// A wait's timeout: cap_s, bounded by the runner's remaining time less 30 s so the final
    /// reads and the asserts still run.
    /// </summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 30.0));

    /// <summary>
    /// A singleton UpdateItem racing TransactWriteItems on one key is refused only with
    /// TransactionConflictException, a losing transaction is cancelled with the exact [TransactionConflict]
    /// message, and the final counter equals exactly the accepted writes.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.plain-write-racing-a-transaction-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-txrace-");
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

        var key = Item(("pk", S("CONTENDED")));
        await ddb.PutItemAsync(table, Item(("pk", S("CONTENDED")), ("hits", N("0"))));

        var transactErrors = new ConcurrentQueue<AwsError>();

        async Task<(string Kind, string? Code)> Transact()
        {
            var error = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = Item(("pk", S("CONTENDED"))),
                            UpdateExpression = "ADD hits :one",
                            ExpressionAttributeValues = Item((":one", N("1"))),
                        },
                    },
                ],
            }));
            if (error is null)
            {
                return ("transact", null);
            }

            transactErrors.Enqueue(error);
            return ("transact", error.Code);
        }

        async Task<(string Kind, string? Code)> Plain()
        {
            var error = await FidelityContext.CaptureError(() => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("CONTENDED"))),
                UpdateExpression = "ADD hits :one",
                ExpressionAttributeValues = Item((":one", N("1"))),
            }));
            return ("plain", error?.Code);
        }

        var accepted = 0;
        var plainCodes = new List<string>();
        var transactCodes = new List<string>();
        var rounds = 0;
        var clock = FidelityTime.StartNew();
        var budget = BudgetS(ctx, ContentionS);

        // A run with no refused plain write still passes: the exact counter catches lost writes.
        while (rounds < MaxRounds && !plainCodes.Contains(ErrConflict)
               && (rounds == 0 || clock.Elapsed.TotalSeconds < budget))
        {
            rounds++;
            var calls = Enumerable.Repeat<Func<Task<(string Kind, string? Code)>>>(Transact, Transactors)
                .Concat(Enumerable.Repeat<Func<Task<(string Kind, string? Code)>>>(Plain, PlainWriters))
                .ToList();
            (string Kind, string? Code)[] outcomes;
            using (ctx.Concurrently())
            {
                outcomes = await Task.WhenAll(calls.Select(fn => Task.Run(fn, cancellationToken)));
            }

            accepted += outcomes.Count(o => o.Code is null);
            plainCodes.AddRange(outcomes.Where(o => o.Kind == "plain" && o.Code is not null).Select(o => o.Code!));
            transactCodes.AddRange(outcomes.Where(o => o.Kind == "transact" && o.Code is not null).Select(o => o.Code!));
        }

        var unexpected = plainCodes.Where(c => c != ErrConflict).ToHashSet();
        Ensure(
            unexpected.Count == 0,
            $"a singleton write that loses to a transaction is refused with {ErrConflict} and nothing else; also saw {Py.Repr(unexpected.Order(StringComparer.Ordinal).ToList())}");
        Ensure(
            transactCodes.All(c => c == "TransactionCanceledException"),
            $"a transaction that loses is CANCELLED, not conflicted at the top level; got {Py.Repr(transactCodes.ToHashSet().Order(StringComparer.Ordinal).ToList())}");
        const string cancelled = "Transaction cancelled, please refer cancellation reasons for specific reasons [TransactionConflict]";
        var messages = transactErrors.Select(e => e.Message).ToList();
        Ensure(
            messages.All(m => m == cancelled),
            $"a transaction that loses the race is cancelled for TransactionConflict; got {Py.Repr(messages.ToHashSet().Order(StringComparer.Ordinal).ToList())}");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            final["hits"].N == accepted.ToString(CultureInfo.InvariantCulture),
            $"the counter must equal the accepted writes exactly: a refused write must add nothing and an accepted one must not be lost. Got {Py.Repr(final["hits"].N)}, expected {accepted} with {plainCodes.Count} refusals and {transactCodes.Count} cancellations");
    }
}
