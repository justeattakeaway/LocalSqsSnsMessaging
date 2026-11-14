using System.Net;
using System.Net.Http.Headers;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Posts SNS messages to HTTP/S subscription endpoints using the same headers and JSON envelope
/// real SNS sends, so endpoint code written against AWS works unchanged.
/// See https://docs.aws.amazon.com/sns/latest/dg/sns-message-and-json-formats.html
/// </summary>
internal static class SnsHttpDelivery
{
    public enum Outcome
    {
        /// <summary>The endpoint returned a 2xx response.</summary>
        Delivered,

        /// <summary>The endpoint was unreachable or returned 5xx/429; the delivery policy decides whether to retry.</summary>
        Retryable,

        /// <summary>The endpoint returned a non-retryable error (any other status); the message is not retried.</summary>
        Failed
    }

    public const string MessageTypeHeader = "x-amz-sns-message-type";
    public const string MessageIdHeader = "x-amz-sns-message-id";
    public const string TopicArnHeader = "x-amz-sns-topic-arn";
    public const string SubscriptionArnHeader = "x-amz-sns-subscription-arn";
    public const string RawDeliveryHeader = "x-amz-sns-rawdelivery";
    private const string UserAgent = "Amazon Simple Notification Service Agent";

    // SNS writes ARNs into its URLs unescaped: an ARN's characters are all valid in a query string.

    /// <summary>The <c>SubscribeURL</c> of a confirmation: a GET that confirms the subscription.</summary>
    public static string SubscribeUrl(InMemoryAwsBus bus, SnsSubscription subscription) =>
        $"{BaseUrl(bus)}/?Action=ConfirmSubscription&TopicArn={subscription.TopicArn}&Token={subscription.ConfirmationToken}";

    /// <summary>The <c>UnsubscribeURL</c> of a notification: a GET that unsubscribes <paramref name="subscription"/>.</summary>
    public static string UnsubscribeUrl(InMemoryAwsBus bus, SnsSubscription subscription) =>
        $"{BaseUrl(bus)}/?Action=Unsubscribe&SubscriptionArn={subscription.SubscriptionArn}";

    /// <summary>
    /// The <c>SigningCertURL</c> of a signed message: a GET of it serves <paramref name="certificate"/>
    /// in PEM form, through the SNS <see cref="Http.InMemoryAwsHttpMessageHandler"/> or the server.
    /// </summary>
    public static string SigningCertUrl(InMemoryAwsBus bus, SnsSigningCertificate certificate) =>
        $"{BaseUrl(bus)}/{certificate.FileName}";

    private static string BaseUrl(InMemoryAwsBus bus) =>
        bus.ServiceUrl?.ToString().TrimEnd('/') ?? $"https://sns.{bus.CurrentRegion}.amazonaws.com";

    /// <summary>
    /// Sends the <c>SubscriptionConfirmation</c> message real SNS posts when an HTTP/S endpoint is
    /// subscribed. The subscription is already confirmed, so the outcome is ignored; this exists so
    /// endpoints that expect the handshake still see it.
    /// </summary>
    public static void SendSubscriptionConfirmation(InMemoryAwsBus bus, SnsSubscription subscription)
    {
        var messageId = Guid.NewGuid().ToString();
        var signatureVersion = SnsEnvelope.DefaultSignatureVersion;
        if (bus.Topics.TryGetValue(SnsArn.TopicName(subscription.TopicArn), out var topic))
        {
            lock (topic.SubscriptionGate)
            {
                signatureVersion = topic.Attributes.GetValueOrDefault(SnsTopicAttributes.SignatureVersion, signatureVersion);
            }
        }

        var body = SnsEnvelope.SubscriptionConfirmation(
            bus,
            signatureVersion,
            messageId,
            subscription.ConfirmationToken,
            subscription.TopicArn,
            $"You have chosen to subscribe to the topic {subscription.TopicArn}.\nTo confirm the subscription, visit the SubscribeURL included in this message.",
            SubscribeUrl(bus, subscription));

        _ = PostAsync(bus, subscription, "SubscriptionConfirmation", messageId, body, SnsDeliveryPolicy.DefaultContentType, raw: false);
    }

    public static string Timestamp(InMemoryAwsBus bus) =>
        bus.TimeProvider.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", DateTimeFormatInfo.InvariantInfo);

    /// <summary>Makes a single delivery attempt to the subscription's endpoint. Never throws.</summary>
    public static async Task<Outcome> PostAsync(
        InMemoryAwsBus bus,
        SnsSubscription subscription,
        string messageType,
        string messageId,
        string body,
        string contentType,
        bool raw)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.EndPoint);
            request.Content = new StringContent(body, Encoding.UTF8);
            if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
            {
                request.Content.Headers.ContentType = mediaType;
            }
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation(MessageTypeHeader, messageType);
            request.Headers.TryAddWithoutValidation(MessageIdHeader, messageId);
            request.Headers.TryAddWithoutValidation(TopicArnHeader, subscription.TopicArn);
            request.Headers.TryAddWithoutValidation(SubscriptionArnHeader, subscription.SubscriptionArn);
            if (raw)
            {
                request.Headers.TryAddWithoutValidation(RawDeliveryHeader, "true");
            }

            using var response = await bus.HttpClient.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return Outcome.Delivered;
            }

            // SNS treats 5xx and 429 as server-side errors subject to the retry policy; everything
            // else is a client-side error and is failed immediately.
            var status = (int)response.StatusCode;
            return status >= 500 || response.StatusCode == (HttpStatusCode)429
                ? Outcome.Retryable
                : Outcome.Failed;
        }
        catch (HttpRequestException)
        {
            return Outcome.Retryable;
        }
        catch (OperationCanceledException)
        {
            // HttpClient surfaces its timeout as a cancellation.
            return Outcome.Retryable;
        }
        catch (InvalidOperationException)
        {
            // The endpoint isn't an absolute URI the HttpClient can send to.
            return Outcome.Failed;
        }
    }
}
