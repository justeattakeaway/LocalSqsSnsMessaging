using System.Diagnostics;
using System.Collections.Concurrent;
using Amazon.Runtime.Telemetry;
using Amazon.Runtime.Telemetry.Tracing;

namespace LocalSqsSnsMessaging.Tests;

/// <summary>
/// Bridges the AWS SDK's own telemetry onto <see cref="Activity"/>. The SDK already names its spans
/// <c>{ServiceId}.{Operation}</c> and tags them with <c>rpc.system</c>, <c>rpc.service</c>,
/// <c>rpc.method</c>, <c>aws.request_id</c>, <c>http.status_code</c>, <c>aws.error_code</c> and the
/// exception attributes, and nests <c>HttpRequest</c> and <c>CredentialsRetrieval</c> spans inside
/// the call - so handing it an <see cref="ActivitySource"/> is all it takes to get that back.
/// </summary>
internal sealed class AwsSdkActivityTracerProvider : TracerProvider
{
    /// <summary>The SDK asks for one tracer per <c>AWSSDK.{ServiceId}</c> scope.</summary>
    internal const string SourceNamePattern = "AWSSDK.*";

    private static readonly ConcurrentDictionary<string, AwsSdkActivityTracer> Tracers = new(StringComparer.Ordinal);

    public override Tracer GetTracer(string scope) => Tracers.GetOrAdd(scope, s => new AwsSdkActivityTracer(s));
}

internal sealed class AwsSdkActivityTracer : Tracer
{
    private readonly ActivitySource _source;

    internal AwsSdkActivityTracer(string scope) => _source = new ActivitySource(scope);

    public override TraceSpan CreateSpan(
        string name,
        Attributes? initialAttributes = null,
        SpanKind spanKind = SpanKind.INTERNAL,
        SpanContext? parentContext = null)
    {
        var activity = _source.StartActivity(name, Map(spanKind));

        if (activity is not null && initialAttributes is not null)
        {
            foreach (var (key, value) in initialAttributes.AllAttributes)
            {
                activity.SetTag(key, value);
            }
        }

        return new AwsSdkActivityTraceSpan(name, activity);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }

    private static ActivityKind Map(SpanKind spanKind) => spanKind switch
    {
        SpanKind.CLIENT => ActivityKind.Client,
        SpanKind.SERVER => ActivityKind.Server,
        SpanKind.PRODUCER => ActivityKind.Producer,
        SpanKind.CONSUMER => ActivityKind.Consumer,
        _ => ActivityKind.Internal
    };
}

/// <summary>
/// One SDK span. <see cref="Activity"/> is null when nothing is listening, which the SDK still
/// expects to be able to call, so every member tolerates it.
/// </summary>
internal sealed class AwsSdkActivityTraceSpan : TraceSpan
{
    private readonly Activity? _activity;

    internal AwsSdkActivityTraceSpan(string name, Activity? activity)
    {
        Name = name;
        _activity = activity;
    }

    public override void EmitEvent(string name, Attributes? attributes = null)
    {
        if (_activity is null)
        {
            return;
        }

        var tags = new ActivityTagsCollection();
        foreach (var (key, value) in attributes?.AllAttributes ?? [])
        {
            tags[key] = value;
        }

        _activity.AddEvent(new ActivityEvent(name, tags: tags));
    }

    public override void SetAttribute(string key, object value) => _activity?.SetTag(key, value);

    public override void SetStatus(SpanStatus status) =>
        _activity?.SetStatus(status switch
        {
            SpanStatus.OK => ActivityStatusCode.Ok,
            SpanStatus.ERROR => ActivityStatusCode.Error,
            _ => ActivityStatusCode.Unset
        });

    public override void RecordException(Exception exception, Attributes? attributes = null)
    {
        if (_activity is null)
        {
            return;
        }

        _activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        EmitEvent("exception", attributes);
    }

    public override void End() => _activity?.Stop();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _activity?.Dispose();
        }

        base.Dispose(disposing);
    }
}
