// Translated from behavioral/dynamodb/check_mid_flight_socket_kill_recovery_probe_proves_exactly_once.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.mid-flight-socket-kill-recovery-probe-proves-exactly-once</summary>
[Category("AwsEvaluationSuite")]
public sealed class MidFlightSocketKillRecoveryProbeProvesExactlyOnce
{
    private const string CheckId = "mid-flight-socket-kill-recovery-probe-proves-exactly-once";

    // botocore's max_attempts counts RETRIES, so this is 3 deliveries, all of them destroyed.
    private const int MaxAttempts = 2;
    private const int TotalAttempts = MaxAttempts + 1;

    /// <summary>
    /// The Python check's <c>before-send</c> hook: for the operations being killed, the request is
    /// delivered to the bus and its response discarded, then the connection is reported lost, so
    /// the SDK never reads a response and its retry policy sees a connection closed mid-flight.
    /// Each killed delivery records its <c>amz-sdk-request</c> header.
    /// </summary>
    private sealed class KillAfterSendHandler(HttpClient bus) : HttpMessageHandler
    {
        public Dictionary<string, List<string>> Logs { get; } = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri);
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            forwarded.Content = new ByteArrayContent(body);
            foreach (var header in request.Headers)
            {
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            var target = request.Headers.TryGetValues("X-Amz-Target", out var targets) ? targets.First() : "";
            var operation = target[(target.IndexOf('.', StringComparison.Ordinal) + 1)..];
            var response = await bus.SendAsync(forwarded, cancellationToken);
            List<string>? log;
            lock (Logs)
            {
                Logs.TryGetValue(operation, out log);
            }

            if (log is null)
            {
                return response;
            }

            // The server finished the delivery; the connection is destroyed before the SDK reads it.
            response.Dispose();
            log.Add(request.Headers.TryGetValues("amz-sdk-request", out var stamps) ? string.Join(",", stamps) : "");
            throw new HttpRequestException("connection reset by peer", new IOException("connection reset by peer"));
        }

        public List<string> Register(string operation)
        {
            var log = new List<string>();
            lock (Logs)
            {
                Logs[operation] = log;
            }

            return log;
        }

        public void Unregister(string operation)
        {
            lock (Logs)
            {
                Logs.Remove(operation);
            }
        }
    }

    private sealed class KillerHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }

    /// <summary>Teardown for a table the check itself deletes on its success path.</summary>
    private static async Task DeleteTableIfPresent(IAmazonDynamoDB client, string table)
    {
        try
        {
            await client.DeleteTableAsync(table);
        }
        catch (ResourceNotFoundException)
        {
        }
    }

    /// <summary>
    /// With every delivery destroyed mid-flight, a token'd TransactWriteItems ADD commits once, a
    /// same-token replay stays at 1 and a fresh token moves it to 2, while the token-less UpdateItem
    /// control commits once per delivery; outcomes are read back with consistent GetItem.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.mid-flight-socket-kill-recovery-probe-proves-exactly-once")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var plain = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-d3kill-");
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

        // ctx.client("dynamodb", config=Config(retries={"max_attempts": 2, "mode": "standard"})),
        // with the check's before-send hook in front of the bus.
        using var killHandler = new KillAfterSendHandler(ctx.RawHttpClient(LocalSqsSnsMessaging.Http.AwsServiceType.DynamoDb));
        using var killer = new AmazonDynamoDBClient(new BasicAWSCredentials("test", "test"), new AmazonDynamoDBConfig
        {
            ServiceURL = $"https://dynamodb.{ctx.Region}.amazonaws.com",
            AuthenticationRegion = ctx.Region,
            RetryMode = RequestRetryMode.Standard,
            MaxErrorRetry = MaxAttempts,
            HttpClientFactory = new KillerHttpClientFactory(killHandler),
        });
        var total = killer.Config.MaxErrorRetry + 1;
        Ensure(
            total == TotalAttempts,
            $"max_attempts={MaxAttempts} normalized to total_max_attempts={Py.Repr(total)}; max_attempts counts RETRIES, so the ceiling must be {TotalAttempts}");

        async Task<string?> Counter(string key)
        {
            var item = (await plain.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(key))), ConsistentRead = true })).Item;
            return item?["n"].N;
        }

        Update IncrementUpdate(string key) => new()
        {
            TableName = table,
            Key = Item(("pk", S(key))),
            UpdateExpression = "ADD n :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
        };

        UpdateItemRequest IncrementRequest(string key) => new()
        {
            TableName = table,
            Key = Item(("pk", S(key))),
            UpdateExpression = "ADD n :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
        };

        // --- arm A: token'd, every delivery destroyed mid-flight ---
        var token = Guid.NewGuid().ToString();
        var log = killHandler.Register("TransactWriteItems");
        try
        {
            await killer.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                ClientRequestToken = token,
                TransactItems = [new TransactWriteItem { Update = IncrementUpdate("tokened") }],
            });
            throw Fail("transact_write_items returned a result although every socket was destroyed before a response byte could be read");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // botocore's ConnectionClosedError: the .NET SDK rethrows the lost connection's IOException.
        }
        finally
        {
            killHandler.Unregister("TransactWriteItems");
        }

        Ensure(
            log.Count == total,
            $"the token'd request was transmitted {log.Count} times, expected {total}; the SDK must retry a connection lost mid-flight up to its ceiling");
        // botocore stamps the first attempt "attempt=1" with no max=; the .NET SDK stamps every
        // attempt "attempt=N; max=M", so only the attempt number is graded on the first delivery.
        Ensure(
            log[0].Contains("attempt=1", StringComparison.Ordinal),
            $"first delivery stamped amz-sdk-request={Py.Repr(log[0])}; attempt 1 carries attempt=1");
        for (var i = 2; i <= log.Count; i++)
        {
            var stamp = log[i - 1];
            Ensure(
                stamp.Contains($"attempt={i}; max={total}", StringComparison.Ordinal),
                $"delivery {i} stamped amz-sdk-request={Py.Repr(stamp)}, expected \"attempt={i}; max={total}\"");
        }

        // the recovery probe: the ONLY way this outcome is readable
        Ensure(
            await Counter("tokened") == "1",
            $"after {total} destroyed deliveries of one ClientRequestToken the counter reads {Py.Repr(await Counter("tokened"))}, expected '1' — a ledger consulted at commit time is what makes this exactly-once, and a response cache cannot supply it because no response was read");

        // Parameters must be identical: even ReturnConsumedCapacity is part of the idempotency
        // fingerprint and would raise IdempotentParameterMismatchException.
        await killer.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            ClientRequestToken = token,
            TransactItems = [new TransactWriteItem { Update = IncrementUpdate("tokened") }],
        });
        Ensure(
            await Counter("tokened") == "1",
            $"a clean replay of the same token moved the counter to {Py.Repr(await Counter("tokened"))}; the killed deliveries had already established the ledger entry");

        // positive control: a fresh token applies the work a second time, so the table is writable
        await killer.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            ClientRequestToken = Guid.NewGuid().ToString(),
            TransactItems = [new TransactWriteItem { Update = IncrementUpdate("tokened") }],
        });
        Ensure(
            await Counter("tokened") == "2",
            $"a FRESH token left the counter at {Py.Repr(await Counter("tokened"))}, expected '2' — dedup must be scoped to the token, not to the request shape");

        // --- arm B (control): token-LESS, every delivery destroyed the same way ---
        var logB = killHandler.Register("UpdateItem");
        try
        {
            await killer.UpdateItemAsync(IncrementRequest("tokenless"));
            throw Fail("update_item returned a result although every socket was destroyed before a response byte could be read");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // botocore's ConnectionClosedError: the .NET SDK rethrows the lost connection's IOException.
        }
        finally
        {
            killHandler.Unregister("UpdateItem");
        }

        Ensure(logB.Count == total, $"the token-less request was transmitted {logB.Count} times, expected {total}");
        var totalText = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Ensure(
            await Counter("tokenless") == totalText,
            $"the token-less counter reads {Py.Repr(await Counter("tokenless"))} after {total} destroyed deliveries, expected {Py.Repr(totalText)}; if this is not {total} the kill did not actually deliver every attempt and arm A's '1' proves nothing about the service");

        // --- cleanup verification ---
        await plain.DeleteTableAsync(table);
        await plain.WaitForTableNotExists(table, delaySeconds: 2, maxAttempts: 100);
        var describeError = await FidelityContext.CaptureError(() => plain.DescribeTableAsync(table));
        if (describeError is null)
        {
            throw Fail($"{table} still describable after delete_table");
        }

        Ensure(
            describeError.Code == "ResourceNotFoundException",
            $"describe_table on the deleted table raised {Py.Repr(describeError.Code)}, expected ResourceNotFoundException");
        var expected = $"Requested resource not found: Table: {table} not found";
        Ensure(describeError.Message == expected, $"describe_table on the deleted table answered {Py.Repr(describeError.Message)}, expected {Py.Repr(expected)}");

        var updateError = await FidelityContext.CaptureError(() => plain.UpdateItemAsync(IncrementRequest("tokened")));
        if (updateError is null)
        {
            throw Fail("update_item succeeded against a table already deleted");
        }

        Ensure(
            updateError.Code == "ResourceNotFoundException",
            $"update_item on the deleted table raised {Py.Repr(updateError.Code)}, expected ResourceNotFoundException");
        Ensure(
            updateError.Message == "Requested resource not found",
            $"update_item on the deleted table answered {Py.Repr(updateError.Message)}, expected 'Requested resource not found'");
    }
}
