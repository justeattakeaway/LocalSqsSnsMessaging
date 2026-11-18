// Translated from behavioral/dynamodb/check_sdk_retry_replays_the_wire_and_only_the_token_stops_the_duplicate.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.sdk-retry-replays-the-wire-and-only-the-token-stops-the-duplicate</summary>
/// <remarks>
/// botocore's <c>before-send</c> hooks become an <see cref="HttpMessageHandler"/> under a client
/// built here: it forwards each attempt to the cloud (through <c>ctx.RawHttpClient</c>) and then
/// answers the SDK with a fabricated 503, or with the real response. botocore's retry modes map to
/// the .NET SDK's <see cref="RequestRetryMode"/>, and its <c>max_attempts</c> (retries) to
/// <see cref="ClientConfig.MaxErrorRetry"/>.
/// </remarks>
[Category("AwsEvaluationSuite")]
public sealed class SdkRetryReplaysTheWireAndOnlyTheTokenStopsTheDuplicate
{
    private const string Check = "sdk-retry-replays-the-wire-and-only-the-token-stops-the-duplicate";
    private const string ErrValidation = "ValidationException";
    private const string InjectedMessage = "injected by a before-send hook";

    // botocore's `max_attempts` counts RETRIES, so the normalized ceiling is N+1 deliveries.
    private static readonly (string Mode, int Attempts)[] ModesAndAttempts =
    [
        ("standard", 1), ("standard", 3), ("adaptive", 1), ("adaptive", 3),
        ("legacy", 1), ("legacy", 3),
    ];

    // The pairs whose effect count is measured: every mode and both attempt counts are covered, while
    // exponential backoff (rand() * min(2**(n-1), 20) s per retry) keeps the run time bounded.
    private static readonly (string Mode, int Attempts)[] EffectArms = [("standard", 1), ("standard", 3), ("adaptive", 3), ("legacy", 3)];

    /// <summary>
    /// botocore's retry mode in the .NET SDK. Version 4 of the SDK dropped its legacy mode; a
    /// "legacy" arm runs in standard mode, which retries the same 5xx the same number of times.
    /// </summary>
    private static RequestRetryMode SdkMode(string mode) => mode == "adaptive" ? RequestRetryMode.Adaptive : RequestRetryMode.Standard;

    private sealed record Delivery(int RealStatus, string? RequestId, string SdkRequest, string InvocationId);

    /// <summary>One request header as a string, or empty.</summary>
    private static string Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";

    /// <summary>Sends a copy of <paramref name="request"/> to the cloud, as botocore's hook transmits the signed request by hand.</summary>
    private static async Task<HttpResponseMessage> Transmit(HttpClient forward, HttpRequestMessage request, CancellationToken cancellationToken)
    {
#pragma warning disable CA2000 // Disposed with the response's request.
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
#pragma warning restore CA2000
        foreach (var (name, values) in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(name, values);
        }

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            copy.Content = new ByteArrayContent(body);
            foreach (var (name, values) in request.Content.Headers)
            {
                if (!string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    copy.Content.Headers.TryAddWithoutValidation(name, values);
                }
            }
        }

        return await forward.SendAsync(copy, cancellationToken);
    }

    /// <summary>
    /// A handler that really executes the request and then reports <paramref name="status"/>.
    /// Logs (real_status, x-amzn-RequestId, amz-sdk-request, amz-sdk-invocation-id) per call.
    /// </summary>
    private sealed class ForwardThenLie(HttpClient forward, HttpStatusCode status, string errType, List<Delivery> log) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var real = await Transmit(forward, request, cancellationToken);
            log.Add(new Delivery(
                (int)real.StatusCode,
                real.Headers.TryGetValues("x-amzn-RequestId", out var ids) ? ids.First() : null,
                Header(request, "amz-sdk-request"),
                Header(request, "amz-sdk-invocation-id")));
            var payload = $"{{\"__type\":\"aws#{errType}\",\"message\":\"{InjectedMessage}\"}}";
            var response = new HttpResponseMessage(status) { Content = new StringContent(payload, Encoding.UTF8), RequestMessage = request };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-amz-json-1.0");
            return response;
        }
    }

    /// <summary>A handler that records each delivery and passes the real response back.</summary>
    private sealed class Record(HttpClient forward, List<Uri?> deliveries) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            deliveries.Add(request.RequestUri);
            return await Transmit(forward, request, cancellationToken);
        }
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }

    private static AmazonDynamoDBConfig Config(FidelityContext ctx, string mode, int attempts, HttpMessageHandler? handler = null)
    {
        var config = new AmazonDynamoDBConfig
        {
            ServiceURL = $"https://dynamodb.{ctx.Region}.amazonaws.com",
            AuthenticationRegion = ctx.Region,
            RetryMode = SdkMode(mode),
            MaxErrorRetry = attempts,
        };
        if (handler is not null)
        {
            config.HttpClientFactory = new HandlerFactory(handler);
        }

        return config;
    }

    /// <summary>The client <c>ctx.client('dynamodb', config=Config(retries=...))</c> builds, its wire going through <paramref name="handler"/>.</summary>
    private static AmazonDynamoDBClient Client(FidelityContext ctx, string mode, int attempts, HttpMessageHandler handler)
    {
        var client = new AmazonDynamoDBClient(new BasicAWSCredentials("test", "test"), Config(ctx, mode, attempts, handler));
        ctx.OnCleanup(() =>
        {
            client.Dispose();
            handler.Dispose();
            return Task.CompletedTask;
        });
        return client;
    }

    /// <summary>
    /// A 503 faked after each real commit makes a token-less UpdateItem ADD land total_max_attempts
    /// times, while a TransactWriteItems with ClientRequestToken lands once, in every retry mode.
    /// A real ValidationException is delivered once and writes nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.sdk-retry-replays-the-wire-and-only-the-token-stops-the-duplicate")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var plain = ctx.Dynamo;
        var forward = ctx.RawHttpClient(InMemoryAws.Http.AwsServiceType.DynamoDb);
        var table = FidelityContext.UniqueName("sim2real-behav-d3retry-");
        await plain.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(async () =>
        {
            var error = await FidelityContext.CaptureError(() => plain.DeleteTableAsync(table));
            if (error is not null && error.Code != "ResourceNotFoundException")
            {
                throw error.Exception;
            }
        });
        await plain.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task<string?> Counter(string key)
        {
            var item = (await plain.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(key))), ConsistentRead = true })).Item;
            return item?["n"].N;
        }

        UpdateItemRequest Increment(string key) => new()
        {
            TableName = table,
            Key = Item(("pk", S(key))),
            UpdateExpression = "ADD n :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
        };

        Update IncrementUpdate(string key) => new()
        {
            TableName = table,
            Key = Item(("pk", S(key))),
            UpdateExpression = "ADD n :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
        };

        var totals = new Dictionary<(string, int), int>();
        foreach (var (mode, attempts) in ModesAndAttempts)
        {
            var got = Config(ctx, mode, attempts).MaxErrorRetry + 1;
            Ensure(
                got == attempts + 1,
                $"{mode} mode with max_attempts={attempts} normalized to total_max_attempts={Py.Repr(got)}; max_attempts counts RETRIES, so the ceiling must be {attempts + 1}");
            totals[(mode, attempts)] = got;
        }

        foreach (var (mode, attempts) in EffectArms)
        {
            var total = totals[(mode, attempts)];
            var key = $"notoken-{mode}-{attempts}";
            var log = new List<Delivery>();
            var client = Client(ctx, mode, attempts, new ForwardThenLie(forward, HttpStatusCode.ServiceUnavailable, "ServiceUnavailableException", log));
            var error = await FidelityContext.CaptureError(() => client.UpdateItemAsync(Increment(key)));
            if (error is null)
            {
                throw Fail($"{mode}/{attempts}: the injected 503 was reported as success — the fabricated AWSResponse never reached the parser");
            }

            Ensure(
                error.Code == "ServiceUnavailableException",
                $"{mode}/{attempts}: injected 503 surfaced as {Py.Repr(error.Code)}, not the error the fabricated body declares");
            Ensure(
                error.Message == InjectedMessage,
                $"{mode}/{attempts}: injected 503 surfaced the message {Py.Repr(error.Message)}, not the {Py.Repr(InjectedMessage)} the fabricated body declares");

            Ensure(
                log.Count == total,
                $"{mode}/{attempts}: the request went on the wire {log.Count} times, expected {total} (max_attempts={attempts} means {attempts} RETRIES)");
            Ensure(
                log.All(entry => entry.RealStatus == 200),
                $"{mode}/{attempts}: real service statuses were {Py.Repr(log.Select(e => e.RealStatus).ToList())}; every forwarded attempt must have COMMITTED before the 503 was fabricated");
            var count = await Counter(key);
            Ensure(
                count == total.ToString(CultureInfo.InvariantCulture),
                $"{mode}/{attempts}: token-less counter reads {Py.Repr(count)} after {total} deliveries, expected {Py.Repr(total.ToString(CultureInfo.InvariantCulture))} — the SDK duplicated the effect and nothing deduplicated it");
            var invocationIds = log.Select(e => e.InvocationId).ToHashSet(StringComparer.Ordinal);
            Ensure(
                invocationIds.Count == 1 && !invocationIds.Contains(""),
                $"{mode}/{attempts}: amz-sdk-invocation-id was {Py.Repr(invocationIds.Order(StringComparer.Ordinal).ToList())}; it must be one stable value across every attempt of one logical call");
            var requestIds = log.Select(e => e.RequestId).ToHashSet(StringComparer.Ordinal);
            Ensure(
                requestIds.Count == total && !requestIds.Contains(null),
                $"{mode}/{attempts}: the service returned {requestIds.Count} distinct x-amzn-RequestId values across {total} deliveries ({Py.Repr(requestIds.Order(StringComparer.Ordinal).ToList())}); each attempt is a separate request and must be answered as one");
            var sdkRequest = log.Select(e => e.SdkRequest).ToList();
            // botocore stamps the first attempt "attempt=1" with no max=; the .NET SDK stamps every
            // attempt, the first included, "attempt=N; max=M". The header is the client's, not the
            // service's, so the first attempt is held to the .NET SDK's own stamp.
            Ensure(
                sdkRequest[0] == $"attempt=1; max={total}",
                $"{mode}/{attempts}: first attempt stamped amz-sdk-request={Py.Repr(sdkRequest[0])}; attempt 1 carries attempt=1 and the .NET SDK's max={total}");
            for (var i = 2; i <= sdkRequest.Count; i++)
            {
                var stamp = sdkRequest[i - 1];
                Ensure(
                    stamp.Contains($"attempt={i}; max={total}", StringComparison.Ordinal),
                    $"{mode}/{attempts}: attempt {i} stamped amz-sdk-request={Py.Repr(stamp)}, expected \"attempt={i}; max={total}\"");
            }
        }

        foreach (var (mode, attempts) in EffectArms)
        {
            var total = totals[(mode, attempts)];
            var key = $"token-{mode}-{attempts}";
            var log = new List<Delivery>();
            var client = Client(ctx, mode, attempts, new ForwardThenLie(forward, HttpStatusCode.ServiceUnavailable, "ServiceUnavailableException", log));
            var error = await FidelityContext.CaptureError(() => client.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                ClientRequestToken = Guid.NewGuid().ToString(),
                TransactItems = [new TransactWriteItem { Update = IncrementUpdate(key) }],
            }));
            if (error is null)
            {
                throw Fail($"{mode}/{attempts}: the injected 503 was reported as success on the token'd arm");
            }

            Ensure(error.Code == "ServiceUnavailableException", $"{mode}/{attempts}: token'd arm surfaced {Py.Repr(error.Code)}, not the injected error");
            Ensure(
                error.Message == InjectedMessage,
                $"{mode}/{attempts}: token'd arm surfaced the message {Py.Repr(error.Message)}, not the injected {Py.Repr(InjectedMessage)}");

            Ensure(
                log.Count == total,
                $"{mode}/{attempts}: token'd request went on the wire {log.Count} times, expected {total} — the token must not change how often the SDK retries");
            var count = await Counter(key);
            Ensure(count == "1", $"{mode}/{attempts}: token'd counter reads {Py.Repr(count)} after {total} deliveries of one ClientRequestToken, expected '1'");
        }

        // Adaptive and standard differ in rate, not in delivery count.
        Ensure(
            totals[("adaptive", 3)] == totals[("standard", 3)] && totals[("standard", 3)] == 4,
            $"adaptive normalized to {Py.Repr(totals[("adaptive", 3)])} and standard to {Py.Repr(totals[("standard", 3)])} for max_attempts=3; both must be 4");
        var adaptiveCount = await Counter("notoken-adaptive-3");
        var standardCount = await Counter("notoken-standard-3");
        Ensure(
            adaptiveCount == standardCount && standardCount == "4",
            $"adaptive delivered {Py.Repr(adaptiveCount)} effects and standard {Py.Repr(standardCount)}; the two modes throttle differently but retry the same number of times");

        var deliveries = new List<Uri?>();
        var recording = Client(ctx, "standard", 3, new Record(forward, deliveries));
        var rejected = await FidelityContext.CaptureError(() => recording.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("rejected"))),
            UpdateExpression = "ADD n :one",
            ExpressionAttributeValues = Item((":one", S("not-a-number"))),
        }));
        if (rejected is null)
        {
            throw Fail("ADD with a string operand was accepted; real AWS rejects it with ValidationException");
        }

        Ensure(rejected.Code == ErrValidation, $"ADD with a string operand raised {Py.Repr(rejected.Code)}, expected {Py.Repr(ErrValidation)}");
        const string addMessage =
            "1 validation error detected: Invalid UpdateExpression: Incorrect operand " +
            "type for operator or function; operator: ADD, operand type: STRING, " +
            "typeSet: ALLOWED_FOR_ADD_OPERAND";
        Ensure(rejected.Message == addMessage, $"ADD with a string operand raised the message {Py.Repr(rejected.Message)}, expected {Py.Repr(addMessage)}");
        var status = (int)rejected.StatusCode;
        Ensure(status == 400, $"ValidationException came back with HTTP {Py.Repr(status)}, expected 400");

        Ensure(
            deliveries.Count == 1,
            $"a 400 ValidationException was put on the wire {deliveries.Count} times on a client whose total_max_attempts is {Py.Repr(recording.Config.MaxErrorRetry + 1)}; a domain error must not be retried — an emulator that answers 5xx here is retried and duplicates");
        var rejectedCount = await Counter("rejected");
        Ensure(rejectedCount is null, $"the rejected ADD left an item behind: n={Py.Repr(rejectedCount)}");

        await plain.UpdateItemAsync(Increment("rejected"));
        rejectedCount = await Counter("rejected");
        Ensure(
            rejectedCount == "1",
            $"positive control failed: a valid ADD on the same key left n={Py.Repr(rejectedCount)}, expected '1' — the table was writable all along");

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
        Ensure(
            describeError.Message == $"Requested resource not found: Table: {table} not found",
            $"describe_table on the deleted table raised the message {Py.Repr(describeError.Message)}");
    }
}
