using System.Diagnostics;
using Amazon.Runtime;
using Amazon.SQS;
using LocalSqsSnsMessaging.Http;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite;

/// <summary>
/// The C# counterpart of the <c>ctx</c> object the AWS emulator evaluation suite's runner hands each
/// <c>check_*.py</c> (see <c>fidelity_tests_runner/context.py</c>). Each test gets its own
/// <see cref="InMemoryAwsBus"/> on the real clock, so checks run in parallel without sharing state.
/// </summary>
/// <remarks>
/// Clients are real <see cref="AmazonSQSClient"/> instances wired to the bus through
/// <see cref="InMemoryAwsHttpMessageHandler"/>, so every call goes through the same JSON-protocol
/// serialization a production SDK client would use.
/// </remarks>
public sealed class FidelityContext : IAsyncDisposable
{
    /// <summary>The runner's default region (<c>DEFAULT_REGION</c> in <c>harness.py</c>).</summary>
    public const string DefaultRegion = "us-west-2";

    /// <summary>The runner's per-check budget for an ordinary check.</summary>
    public const int BudgetMilliseconds = 60_000;

    /// <summary>The runner's per-check budget for a <c>timing:real-time</c> check.</summary>
    public const int RealTimeBudgetMilliseconds = 600_000;

    /// <summary>
    /// The category of checks that wait out a real SQS period of a minute or more (a retention
    /// minimum, the deduplication interval, the purge window). <c>build.ps1</c> leaves them out so
    /// CI stays inside its time limit; run them with <c>--treenode-filter "/**[Category=LongWait]"</c>.
    /// </summary>
    public const string LongWaitCategory = "LongWait";

    private readonly List<Func<Task>> _cleanups = [];
    private readonly Dictionary<string, AmazonSQSClient> _clients = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _budget;

    public FidelityContext(int budgetMilliseconds = BudgetMilliseconds, CancellationToken cancellationToken = default)
    {
        _budget = TimeSpan.FromMilliseconds(budgetMilliseconds);
        CancellationToken = cancellationToken;
        Bus = new InMemoryAwsBus
        {
            CurrentRegion = DefaultRegion,
        };
    }

    public InMemoryAwsBus Bus { get; }

    /// <summary><c>ctx.region</c>.</summary>
    public string Region => Bus.CurrentRegion;

    /// <summary><c>ctx.account</c>, and what <c>sts.get_caller_identity()["Account"]</c> answers.</summary>
    public string Account => Bus.CurrentAccountId;

    /// <summary>Cancelled when the test's timeout elapses.</summary>
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
            HttpClientFactory = Bus.CreateAwsHttpClientFactory(),
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
        var client = new HttpClient(new InMemoryAwsHttpMessageHandler(Bus, AwsServiceType.Sqs))
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

    /// <summary><c>ctx.time_left()</c>: time until the runner's budget for this check runs out.</summary>
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
