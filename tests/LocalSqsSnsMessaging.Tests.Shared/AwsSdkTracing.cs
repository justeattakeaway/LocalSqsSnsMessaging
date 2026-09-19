using Amazon;
using Amazon.Runtime.Internal;

namespace LocalSqsSnsMessaging.Tests;

/// <summary>
/// A small stand-in for OpenTelemetry.Instrumentation.AWS: points the AWS SDK's own telemetry at
/// <see cref="System.Diagnostics.Activity"/> so traces still show up in the test viewer, tags the
/// resulting spans with the messaging semantic conventions, and propagates trace context into
/// outgoing messages the way the real instrumentation does.
/// </summary>
/// <remarks>
/// The reason for owning this rather than calling <c>AddAWSInstrumentation()</c> is
/// <see cref="SuppressPropagation"/>. Propagation costs message size - a <c>traceparent</c> message
/// attribute is 72 bytes - which quietly eats into the size limits that some tests exist to pin down.
/// The real instrumentation gives no way to hold that back: it registers its customizer on the SDK's
/// process-wide pipeline registry, applying to every client built from then on and surviving disposal
/// of the <c>TracerProvider</c>, and neither <c>SuppressInstrumentationScope</c> nor a Drop sampler
/// stops the injection (a dropped span still carries propagation data).
/// </remarks>
internal static class AwsSdkTracing
{
    /// <summary>The activity sources the SDK's spans come from, one per service.</summary>
    internal const string SourceNamePattern = AwsSdkActivityTracerProvider.SourceNamePattern;

    private static readonly AsyncLocal<bool> PropagationSuppressed = new();

    internal static bool PropagationIsSuppressed => PropagationSuppressed.Value;

    /// <summary>
    /// Registers the <see cref="Activity"/> bridge for SDK spans, and the handler that tags and
    /// propagates, with every AWS SDK client built from here on.
    /// </summary>
    internal static void Install()
    {
        AWSConfigs.TelemetryProvider.RegisterTracerProvider(new AwsSdkActivityTracerProvider());
        RuntimePipelineCustomizerRegistry.Instance.Register(new AwsSdkTracingCustomizer());
    }

    /// <summary>
    /// Holds trace context out of outgoing messages until the returned scope is disposed, so a test
    /// sends exactly the bytes it wrote. Spans and their attributes are unaffected - only the
    /// <c>traceparent</c> message attribute and <c>AWSTraceHeader</c> system attribute are held back.
    /// </summary>
    public static IDisposable SuppressPropagation() => new PropagationScope();

    private sealed class PropagationScope : IDisposable
    {
        private readonly bool _previous = PropagationSuppressed.Value;

        public PropagationScope() => PropagationSuppressed.Value = true;

        public void Dispose() => PropagationSuppressed.Value = _previous;
    }
}
