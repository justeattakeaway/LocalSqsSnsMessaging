// Translated from behavioral/dynamodb/check_transact_get_items_never_observes_a_torn_transfer.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-get-items-never-observes-a-torn-transfer</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactGetItemsNeverObservesATornTransfer
{
    private const int Moves = 40;
    private const int Reads = 40;
    private const int Total = 100;
    private const int Amount = 10;
    private const string ErrTransactionCanceled = "TransactionCanceledException";

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// While one thread alternates TransactWriteItems transfers between two items, every concurrent
    /// TransactGetItems sees the invariant total; the final balances also return to their start.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-get-items-never-observes-a-torn-transfer")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-torn-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var name in new[] { "A", "B" })
        {
            await ddb.PutItemAsync(table, Item(("pk", S(name)), ("bal", N(Str(Total / 2)))));
        }

        // One transfer, retried only while the service reports a transaction CONFLICT.
        async Task<string?> Move(string src, string dst)
        {
            for (var attempt = 0; attempt < 30; attempt++)
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
                                Key = Item(("pk", S(src))),
                                UpdateExpression = "SET bal = bal - :d",
                                ConditionExpression = "bal >= :d",
                                ExpressionAttributeValues = Item((":d", N(Str(Amount)))),
                            },
                        },
                        new TransactWriteItem
                        {
                            Update = new Update
                            {
                                TableName = table,
                                Key = Item(("pk", S(dst))),
                                UpdateExpression = "SET bal = bal + :d",
                                ExpressionAttributeValues = Item((":d", N(Str(Amount)))),
                            },
                        },
                    ],
                }));
                if (error is null)
                {
                    return null;
                }

                if (error.Code != ErrTransactionCanceled)
                {
                    return $"{src}->{dst}: {error.Code}";
                }

                var reasons = (error.CancellationReasons() ?? []).Select(r => r.Code).ToList();
                if (reasons.Contains("ConditionalCheckFailed"))
                {
                    return $"{src}->{dst}: balance went negative, reasons {Py.Repr(reasons)}";
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(0.05 * (attempt + 1)));
            }

            return $"{src}->{dst}: still conflicting after 30 attempts";
        }

        async Task<List<string>> Writer()
        {
            var output = new List<string>();
            for (var i = 0; i < Moves; i++)
            {
                var (src, dst) = i % 2 == 0 ? ("A", "B") : ("B", "A");
                var err = await Move(src, dst);
                if (err is not null)
                {
                    output.Add(err);
                }
            }

            return output;
        }

        async Task<List<(int A, int B)>> Reader()
        {
            var pairs = new List<(int A, int B)>();
            for (var read = 0; read < Reads; read++)
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    var (resp, error) = await FidelityContext.Capture(() => ddb.TransactGetItemsAsync(new TransactGetItemsRequest
                    {
                        TransactItems =
                        [
                            new TransactGetItem { Get = new Get { TableName = table, Key = Item(("pk", S("A"))) } },
                            new TransactGetItem { Get = new Get { TableName = table, Key = Item(("pk", S("B"))) } },
                        ],
                    }));
                    if (error is null)
                    {
                        pairs.Add((
                            int.Parse(resp!.Responses[0].Item["bal"].N, CultureInfo.InvariantCulture),
                            int.Parse(resp.Responses[1].Item["bal"].N, CultureInfo.InvariantCulture)));
                        break;
                    }

                    if (error.Code != ErrTransactionCanceled)
                    {
                        throw error.Exception;
                    }

                    await FidelityTime.Delay(TimeSpan.FromSeconds(0.05 * (attempt + 1)));
                }
            }

            return pairs;
        }

        List<string> writerErrors;
        List<(int A, int B)> observed;
        using (ctx.Concurrently())
        {
            var writerTask = Task.Run(Writer, cancellationToken);
            var readerTask = Task.Run(Reader, cancellationToken);
            writerErrors = await writerTask;
            observed = await readerTask;
        }

        var torn = observed.Where(p => p.A + p.B != Total).ToList();
        Ensure(
            torn.Count == 0,
            $"a transactional read observed a torn transfer: {Py.Repr(torn)} out of {observed.Count} observations did not sum to {Total}");
        Ensure(
            writerErrors.Count == 0,
            $"the writer transactions failed for a reason other than contention: {Py.Repr(writerErrors)}");
        Ensure(
            observed.Count == Reads,
            $"the reader did not complete its reads: {observed.Count} of {Reads}");

        var final = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var n in new[] { "A", "B" })
        {
            final[n] = int.Parse(
                (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(n))), ConsistentRead = true })).Item["bal"].N,
                CultureInfo.InvariantCulture);
        }

        Ensure(
            (int)final["A"]! + (int)final["B"]! == Total,
            $"the final balances violate the conservation invariant: {Py.Repr(final)}");
        // An even number of alternating moves must land the value back where it started, so a
        // half-applied final transfer is caught even if every intermediate read got lucky.
        Ensure(
            (int)final["A"]! == Total / 2 && (int)final["B"]! == Total / 2,
            $"{Moves} alternating transfers must return both balances to {Total / 2}, got {Py.Repr(final)}");
    }
}
