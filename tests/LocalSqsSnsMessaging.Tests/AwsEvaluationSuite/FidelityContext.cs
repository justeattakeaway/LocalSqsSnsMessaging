using Amazon.Runtime;
using Amazon.SQS;
using LocalSqsSnsMessaging.Http;
using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite;

/// <summary>
/// The C# counterpart of the <c>ctx</c> object the AWS emulator evaluation suite's runner hands each
/// <c>check_*.py</c> (see <c>fidelity_tests_runner/context.py</c>). Each test gets its own
/// <see cref="InMemoryAwsBus"/>, so checks run in parallel without sharing state.
/// </summary>
/// <remarks>
/// <para>
/// Clients are real <see cref="AmazonSQSClient"/> instances wired to the bus through
/// <see cref="InMemoryAwsHttpMessageHandler"/>, so every call goes through the same JSON-protocol
/// serialization a production SDK client would use.
/// </para>
/// <para>
/// Each check runs on virtual time: the bus's clock is a <see cref="FakeTimeProvider"/>, and the
/// check waits and measures through <see cref="FidelityTime"/> instead of <c>Task.Delay</c> and
/// <c>Stopwatch</c>. Time stands still while the check is working and moves only while something is
/// waiting on it: a <see cref="FidelityTime.Delay"/>, or a <c>ReceiveMessage</c> blocked in a long
/// poll. While either is pending, a pump advances the clock by <see cref="PumpTick"/> about every
/// millisecond, so a check that waits out SQS's five-minute deduplication interval takes seconds.
/// Every request also costs <see cref="RequestLatency"/>, so a polling loop with no wait in it
/// still gets to its deadline.
/// </para>
/// </remarks>
public sealed class FidelityContext : IAsyncDisposable
{
    /// <summary>The runner's default region (<c>DEFAULT_REGION</c> in <c>harness.py</c>).</summary>
    public const string DefaultRegion = "us-west-2";

    /// <summary>The runner's per-check budget for an ordinary check, in virtual time.</summary>
    public const int BudgetMilliseconds = 60_000;

    /// <summary>The runner's per-check budget for a <c>timing:real-time</c> check, in virtual time.</summary>
    public const int RealTimeBudgetMilliseconds = 600_000;

    /// <summary>
    /// The real-time limit on each test. A check finishes in well under a second of real time
    /// however much virtual time it waits out, so this only stops a hung check holding up the run.
    /// </summary>
    public const int HangTimeoutMilliseconds = 30_000;

    /// <summary>How far the pump moves virtual time on each step while something waits on it.</summary>
    public static readonly TimeSpan PumpTick = TimeSpan.FromMilliseconds(100);

    /// <summary>The virtual time each request takes, as a stand-in for the network round trip.</summary>
    public static readonly TimeSpan RequestLatency = TimeSpan.FromMilliseconds(1);

    private static readonly AsyncLocal<FidelityContext?> CurrentContext = new();

    private readonly List<Func<Task>> _cleanups = [];
    private readonly Dictionary<string, AmazonSQSClient> _clients = new(StringComparer.Ordinal);
    private readonly FidelityStopwatch _clock;
    private readonly TimeSpan _budget;
    private readonly CancellationTokenSource _stopPump = new();
    private readonly Task _pump;
    private readonly RequestLatencyHandler _handler;
    private int _waitingDelays;

    public FidelityContext(int budgetMilliseconds = BudgetMilliseconds, CancellationToken cancellationToken = default)
    {
        _budget = TimeSpan.FromMilliseconds(budgetMilliseconds);
        CancellationToken = cancellationToken;
        Time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _clock = new FidelityStopwatch(Time);
        Bus = new InMemoryAwsBus
        {
            CurrentRegion = DefaultRegion,
            TimeProvider = Time,
        };
        _handler = new RequestLatencyHandler(this);
        CurrentContext.Value = this;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>The context of the check running on this async flow, for <see cref="FidelityTime"/>.</summary>
    internal static FidelityContext Current =>
        CurrentContext.Value ?? throw new InvalidOperationException("No FidelityContext has been created on this async flow.");

    /// <summary>The check's virtual clock, which its bus runs on.</summary>
    public FakeTimeProvider Time { get; }

    public InMemoryAwsBus Bus { get; }

    /// <summary><c>ctx.region</c>.</summary>
    public string Region => Bus.CurrentRegion;

    /// <summary><c>ctx.account</c>, and what <c>sts.get_caller_identity()["Account"]</c> answers.</summary>
    public string Account => Bus.CurrentAccountId;

    /// <summary>Cancelled when the test's real-time <see cref="HangTimeoutMilliseconds"/> elapses.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary><c>ctx.client("sqs")</c>: the default client, in <see cref="Region"/>.</summary>
    public AmazonSQSClient Sqs => Client();

    /// <summary>
    /// <c>ctx.client("sqs", region=..., endpoint_url=...)</c>. Clients are cached per combination,
    /// like the runner's. Every client talks to this context's single bus.
    /// </summary>
    /// <param name="region">The client's signing region; defaults to <see cref="Region"/>.</param>
    /// <param name="serviceUrl">An endpoint override, e.g. the scheme and host of a QueueUrl.</param>
    /// <param name="unsigned">True for <c>Config(signature_version=UNSIGNED)</c>.</param>
    public AmazonSQSClient Client(string? region = null, string? serviceUrl = null, bool unsigned = false)
    {
        region ??= Region;
        serviceUrl ??= $"https://sqs.{region}.amazonaws.com";
        var key = $"{region}|{serviceUrl}|{unsigned}";
        if (_clients.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var config = new AmazonSQSConfig
        {
            ServiceURL = serviceUrl,
            AuthenticationRegion = region,
            MaxErrorRetry = 0,
            HttpClientFactory = new VirtualTimeHttpClientFactory(this),
        };
        AWSCredentials credentials = unsigned
            ? new AnonymousAWSCredentials()
            : new BasicAWSCredentials("test", "test");
        var client = new AmazonSQSClient(credentials, config);
        _clients[key] = client;
        return client;
    }

    /// <summary>
    /// An <see cref="HttpClient"/> that sends raw HTTP requests straight to the bus's SQS handler,
    /// for checks that POST hand-built requests instead of using an SDK client.
    /// </summary>
    public HttpClient RawHttpClient()
    {
#pragma warning disable CA2000 // The HttpClient owns and disposes the handler.
        var client = new HttpClient(new RequestLatencyHandler(this))
#pragma warning restore CA2000
        {
            BaseAddress = new Uri($"https://sqs.{Region}.amazonaws.com/"),
        };
        OnCleanup(() =>
        {
            client.Dispose();
            return Task.CompletedTask;
        });
        return client;
    }

    /// <summary><c>ctx.unique_name(prefix)</c>: the prefix plus 12 random hex characters.</summary>
    public static string UniqueName(string prefix = "sim2real-") => prefix + Guid.NewGuid().ToString("N")[..12];

    /// <summary><c>ctx.on_cleanup(fn)</c>: runs after the check, last registered first; errors are ignored.</summary>
    public void OnCleanup(Func<Task> cleanup) => _cleanups.Add(cleanup);

    /// <summary>Waits <paramref name="delay"/> of virtual time, moving the clock on while waiting.</summary>
    public async Task Delay(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay <= TimeSpan.Zero)
        {
            await Task.Yield();
            return;
        }

        // Completed off the pump's thread, so the check never runs inside a clock advance.
        var due = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = Time.CreateTimer(_ => due.TrySetResult(), null, delay, Timeout.InfiniteTimeSpan);
        await using var registration = cancellationToken.Register(() => due.TrySetCanceled(cancellationToken));
        Interlocked.Increment(ref _waitingDelays);
        try
        {
            await due.Task;
        }
        finally
        {
            Interlocked.Decrement(ref _waitingDelays);
        }
    }

    /// <summary>Moves virtual time on while a <see cref="Delay"/> or a long poll is waiting on it.</summary>
    private async Task PumpAsync()
    {
        while (!_stopPump.IsCancellationRequested)
        {
            if (Volatile.Read(ref _waitingDelays) > 0 || Bus.WaitingReceiveCount > 0)
            {
                Time.Advance(PumpTick);
            }

            try
            {
                await Task.Delay(1, _stopPump.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary><c>ctx.time_left()</c>: virtual time until the runner's budget for this check runs out.</summary>
    public TimeSpan TimeLeft()
    {
        var left = _budget - _clock.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary><c>ctx.time_left()</c> in seconds, as the Python checks use it.</summary>
    public double TimeLeftSeconds => TimeLeft().TotalSeconds;

    /// <summary>
    /// Runs <paramref name="call"/> and returns the AWS error it raised, or <see langword="null"/>
    /// when it succeeded: the equivalent of catching botocore's <c>ClientError</c> and reading
    /// <c>exc.response["Error"]</c>.
    /// </summary>
    public static async Task<AwsError?> CaptureError(Func<Task> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            await call();
            return null;
        }
        catch (AmazonServiceException ex)
        {
            return new AwsError(ex.ErrorCode, ex.Message, ex.StatusCode, ex);
        }
    }

    /// <summary>
    /// Like <see cref="CaptureError(Func{Task})"/>, also returning the response when the call succeeded.
    /// </summary>
    public static async Task<(T? Response, AwsError? Error)> Capture<T>(Func<Task<T>> call)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            return (await call(), null);
        }
        catch (AmazonServiceException ex)
        {
            return (null, new AwsError(ex.ErrorCode, ex.Message, ex.StatusCode, ex));
        }
    }

    public async ValueTask DisposeAsync()
    {
        for (var i = _cleanups.Count - 1; i >= 0; i--)
        {
            try
            {
                await _cleanups[i]();
            }
#pragma warning disable CA1031 // Cleanup failures never change a check's verdict, as in the runner.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        await _stopPump.CancelAsync();
        await _pump;
        _stopPump.Dispose();
        _handler.Dispose();
    }

    /// <summary>Charges each request <see cref="RequestLatency"/> of virtual time, then hands it to the bus.</summary>
    private sealed class RequestLatencyHandler(FidelityContext context)
        : DelegatingHandler(new InMemoryAwsHttpMessageHandler(context.Bus, AwsServiceType.Sqs))
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            context.Time.Advance(RequestLatency);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Gives each SDK client an <see cref="HttpClient"/> over this context's
    /// <see cref="RequestLatencyHandler"/>. The SDK's process-wide HttpClient cache is bypassed:
    /// it would hand one check's clients another check's handler, bus and clock.
    /// </summary>
    private sealed class VirtualTimeHttpClientFactory(FidelityContext context) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(context._handler, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }
}

/// <summary>What botocore puts in <c>ClientError.response["Error"]</c>: the wire error code and message.</summary>
public sealed record AwsError(string? Code, string? Message, System.Net.HttpStatusCode StatusCode, AmazonServiceException Exception)
{
    public override string ToString() => $"({Code}, {Message})";
}

/// <summary>A Python <c>assert</c> in a translated check failed.</summary>
public sealed class FidelityAssertionException : Exception
{
    public FidelityAssertionException() { }

    public FidelityAssertionException(string message) : base(message) { }

    public FidelityAssertionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The Python <c>assert condition, message</c> statement. Import with
/// <c>using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;</c>.
/// </summary>
public static class FidelityAssert
{
    /// <summary><c>assert condition, message</c>.</summary>
    public static void Ensure([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new FidelityAssertionException(message);
        }
    }

    /// <summary><c>raise AssertionError(message)</c>: <c>throw Fail("...")</c>.</summary>
    public static FidelityAssertionException Fail(string message) => new(message);
}
