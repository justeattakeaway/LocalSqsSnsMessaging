// Translated from behavioral/dynamodb/check_transact_token_replay_consumed_capacity_is_a_read_not_a_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-token-replay-consumed-capacity-is-a-read-not-a-write</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactTokenReplayConsumedCapacityIsAReadNotAWrite
{
    private const string CheckId = "transact-token-replay-consumed-capacity-is-a-read-not-a-write";
    private const string ErrMismatch = "IdempotentParameterMismatchException";
    private const string MsgMismatch = "Specified idempotent token was used with different request parameters within the "
        + "idempotency window";

    private const int Racers = 24;

    /// <summary>
    /// A TransactWriteItems replay under the same ClientRequestToken bills READ capacity, 24
    /// concurrent first uses of one token collapse to one write (counter 1), a fresh token writes
    /// again, and a changed operand raises IdempotentParameterMismatchException without effect.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-token-replay-consumed-capacity-is-a-read-not-a-write")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var plain = ctx.Dynamo;
        // A 24-way barrier release through one cached client needs a connection pool wider than
        // botocore's default 10, or the race closes on the pool instead of on the service.
        // (Config(max_pool_connections=64) is ignored: the in-memory handler has no connection pool.)
        var racerClient = ctx.DynamoClient();
        var table = FidelityContext.UniqueName("sim2real-behav-d3cc-");
        await plain.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIfPresent(plain, table));
        await plain.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task<Dictionary<string, AttributeValue>?> GetCtr() =>
            (await plain.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("ctr"))), ConsistentRead = true })).Item;

        async Task<string?> Counter()
        {
            var got = await GetCtr();
            return got?["n"].N;
        }

        Task<TransactWriteItemsResponse> Transact(IAmazonDynamoDB client, string token, string operand = "1") =>
            client.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                ClientRequestToken = token,
                ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = Item(("pk", S("ctr"))),
                            UpdateExpression = "ADD n :one",
                            ExpressionAttributeValues = Item((":one", N(operand))),
                        },
                    },
                ],
            });

        // --- 24 simultaneous FIRST uses of one token ---
        var token = Guid.NewGuid().ToString();
        var arrived = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(string Kind, ConsumedCapacity? Capacity, string? Code)> Racer()
        {
            if (Interlocked.Increment(ref arrived) == Racers)
            {
                barrier.TrySetResult();
            }

            await barrier.Task;
            var (resp, error) = await FidelityContext.Capture(() => Transact(racerClient, token));
            return error is null ? ("ok", resp!.ConsumedCapacity?.FirstOrDefault(), null) : ("err", null, error.Code);
        }

        (string Kind, ConsumedCapacity? Capacity, string? Code)[] results;
        using (ctx.Concurrently())
        {
            results = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => Task.Run(Racer, cancellationToken)));
        }

        var errors = results.Where(r => r.Kind == "err").Select(r => r.Code).ToList();
        Ensure(
            errors.Count == 0,
            $"{errors.Count} of {Racers} concurrent first-uses of one ClientRequestToken failed with "
            + $"{Py.Repr(errors.Distinct().Order(StringComparer.Ordinal).ToList())}; a token race must collapse, not surface as an error");
        var capacities = results.Where(r => r.Kind == "ok" && r.Capacity is not null).Select(r => r.Capacity!).ToList();
        Ensure(
            capacities.Count == Racers,
            $"{capacities.Count} of {Racers} responses carried ConsumedCapacity; "
            + "ReturnConsumedCapacity='INDEXES' must be honoured on every one");

        var writers = capacities.Where(c => (c.WriteCapacityUnits ?? 0) > 0).ToList();
        var readers = capacities.Where(c => (c.ReadCapacityUnits ?? 0) > 0).ToList();
        Ensure(
            writers.Count == 1,
            $"{writers.Count} of {Racers} concurrent calls reported WriteCapacityUnits > 0, expected "
            + "exactly 1 — the write units are how the service names which call actually committed");
        Ensure(
            readers.Count == Racers - 1,
            $"{readers.Count} of {Racers} calls reported ReadCapacityUnits > 0, expected "
            + $"{Racers - 1}; a deduplicated replay is served by READING the ledger");
        Ensure(
            readers.All(r => (r.WriteCapacityUnits ?? 0) == 0),
            $"a replay reported both read and write units: {Py.Repr(readers.Where(r => (r.WriteCapacityUnits ?? 0) != 0).Take(1).Select(Capacity).ToList())}");
        Ensure(
            (writers[0].ReadCapacityUnits ?? 0) == 0,
            $"the single committing call reported ReadCapacityUnits too: {Py.Repr(Capacity(writers[0]))}");
        var totalWrite = capacities.Sum(c => c.WriteCapacityUnits ?? 0);
        Ensure(
            totalWrite == writers[0].WriteCapacityUnits,
            $"{Racers} calls billed {Py.Repr(totalWrite)} write capacity units in total but the one "
            + $"committing call billed {Py.Repr(writers[0].WriteCapacityUnits)}; the replays must cost no "
            + "write capacity at all");
        Ensure(
            writers[0].Table.WriteCapacityUnits == writers[0].WriteCapacityUnits,
            $"table-level and aggregate write units disagree: {Py.Repr(Capacity(writers[0]))}");
        Ensure(
            readers[0].Table.ReadCapacityUnits == readers[0].ReadCapacityUnits,
            $"table-level and aggregate read units disagree: {Py.Repr(Capacity(readers[0]))}");

        // the effect count: a NON-idempotent ADD is the only thing that can tell dedup from re-execution
        Ensure(
            await Counter() == "1",
            $"{Racers} concurrent deliveries of one token left the counter at {Py.Repr(await Counter())}, "
            + "expected '1' — two threads both seeing a ledger miss is exactly what an unlocked "
            + "setdefault produces");

        // --- a sequential replay is a read as well ---
        // ReturnConsumedCapacity is part of the idempotency fingerprint, so it is left unchanged.
        var replay = (await Transact(plain, token)).ConsumedCapacity[0];
        Ensure(
            (replay.ReadCapacityUnits ?? 0) > 0,
            $"the sequential replay reported {Py.Repr(Capacity(replay))}; a replay must bill read capacity");
        Ensure(
            (replay.WriteCapacityUnits ?? 0) == 0,
            $"the sequential replay billed write capacity: {Py.Repr(Capacity(replay))}");
        Ensure(await Counter() == "1", $"the sequential replay moved the counter to {Py.Repr(await Counter())}");

        // --- positive control: a fresh token is a write and applies the work again ---
        var fresh = (await Transact(plain, Guid.NewGuid().ToString())).ConsumedCapacity[0];
        Ensure(
            (fresh.WriteCapacityUnits ?? 0) > 0,
            $"a FRESH token reported {Py.Repr(Capacity(fresh))}; an unseen token must bill write capacity");
        Ensure(
            (fresh.ReadCapacityUnits ?? 0) == 0,
            $"a fresh token billed read capacity: {Py.Repr(Capacity(fresh))}");
        Ensure(
            await Counter() == "2",
            $"a fresh token left the counter at {Py.Repr(await Counter())}, expected '2' — dedup is scoped to the "
            + "token, and the table was writable throughout");

        // --- NEGATIVE: the first token with one changed operand is rejected, and changes nothing ---
        var mismatch = await FidelityContext.CaptureError(() => Transact(plain, token, operand: "5"))
            ?? throw Fail(
                "replaying a used ClientRequestToken with a changed operand was accepted; real AWS "
                + $"raises {ErrMismatch}");
        Ensure(
            mismatch.Code == ErrMismatch,
            $"a token replay with a changed operand raised {Py.Repr(mismatch.Code)}, expected {Py.Repr(ErrMismatch)}");
        Ensure(
            mismatch.Message == MsgMismatch,
            $"a token replay with a changed operand said {Py.Repr(mismatch.Message)}, expected {Py.Repr(MsgMismatch)}");

        // effect-absent probe
        Ensure(
            await Counter() == "2",
            $"the rejected mismatch replay moved the counter to {Py.Repr(await Counter())}; a refused call must "
            + "apply nothing");
        var attributes = (await GetCtr())!.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(
            attributes.SequenceEqual(["n", "pk"]),
            $"the item carries attributes {Py.Repr(attributes)}; the idempotency ledger must not leave "
            + "anything behind on the customer's item");

        // --- cleanup verification ---
        await plain.DeleteTableAsync(table);
        await plain.WaitForTableNotExists(table, delaySeconds: 2, maxAttempts: 100);
        var gone = await FidelityContext.CaptureError(() => plain.DescribeTableAsync(table))
            ?? throw Fail($"{table} still describable after delete_table");
        Ensure(
            gone.Code == "ResourceNotFoundException",
            $"describe_table on the deleted table raised {Py.Repr(gone.Code)}, expected "
            + "ResourceNotFoundException");
        var expected = $"Requested resource not found: Table: {table} not found";
        Ensure(
            gone.Message == expected,
            $"describe_table on the deleted table said {Py.Repr(gone.Message)}, expected {Py.Repr(expected)}");
    }

    /// <summary>Teardown for a table the check itself may already have deleted.</summary>
    private static async Task DeleteTableIfPresent(IAmazonDynamoDB ddb, string table)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    /// <summary>A ConsumedCapacity entry as boto3 hands it back.</summary>
    private static Dictionary<string, object?> Capacity(ConsumedCapacity c)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (c.TableName is not null)
        {
            result["TableName"] = c.TableName;
        }

        if (c.CapacityUnits is not null)
        {
            result["CapacityUnits"] = c.CapacityUnits;
        }

        if (c.ReadCapacityUnits is not null)
        {
            result["ReadCapacityUnits"] = c.ReadCapacityUnits;
        }

        if (c.WriteCapacityUnits is not null)
        {
            result["WriteCapacityUnits"] = c.WriteCapacityUnits;
        }

        if (c.Table is not null)
        {
            result["Table"] = Arm(c.Table);
        }

        return result;
    }

    private static Dictionary<string, object?> Arm(Capacity c)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (c.ReadCapacityUnits is not null)
        {
            result["ReadCapacityUnits"] = c.ReadCapacityUnits;
        }

        if (c.WriteCapacityUnits is not null)
        {
            result["WriteCapacityUnits"] = c.WriteCapacityUnits;
        }

        if (c.CapacityUnits is not null)
        {
            result["CapacityUnits"] = c.CapacityUnits;
        }

        return result;
    }
}
