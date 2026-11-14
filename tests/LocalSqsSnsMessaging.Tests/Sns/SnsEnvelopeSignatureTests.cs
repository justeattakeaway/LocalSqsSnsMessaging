using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using LocalSqsSnsMessaging.Http;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Sns;

/// <summary>
/// A notification delivered to SQS is signed as SNS signs it, and its <c>SigningCertURL</c> serves
/// the certificate that verifies it, through the SNS HTTP handler.
/// See https://docs.aws.amazon.com/sns/latest/dg/sns-verify-signature-of-message.html
/// </summary>
public sealed class SnsEnvelopeSignatureTests : IDisposable
{
    private readonly InMemoryAwsBus _bus = new();
    private readonly AmazonSimpleNotificationServiceClient _sns;
    private readonly AmazonSQSClient _sqs;

    public SnsEnvelopeSignatureTests()
    {
        _sns = _bus.CreateSnsClient();
        _sqs = _bus.CreateSqsClient();
    }

    public void Dispose()
    {
        _sns.Dispose();
        _sqs.Dispose();
    }

    [Test]
    [Arguments(null, "SHA1")]
    [Arguments("1", "SHA1")]
    [Arguments("2", "SHA256")]
    public async Task NotificationSignatureVerifiesWithTheServedCertificate(string? signatureVersion, string digest)
    {
        var topicArn = (await _sns.CreateTopicAsync(new CreateTopicRequest { Name = "signed" })).TopicArn;
        if (signatureVersion is not null)
        {
            await _sns.SetTopicAttributesAsync(topicArn, "SignatureVersion", signatureVersion);
        }

        var queueUrl = (await _sqs.CreateQueueAsync("signed-queue")).QueueUrl;
        var queueArn = (await _sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).Attributes["QueueArn"];
        await _sns.SubscribeAsync(topicArn, "sqs", queueArn);
        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "hello \"signed\" world", Subject = "greeting" });

        var body = (await _sqs.ReceiveMessageAsync(queueUrl)).Messages.ShouldHaveSingleItem().Body;
        using var envelope = JsonDocument.Parse(body);
        var root = envelope.RootElement;
        root.GetProperty("SignatureVersion").GetString().ShouldBe(signatureVersion ?? "1");

        using var handler = new InMemoryAwsHttpMessageHandler(_bus, AwsServiceType.Sns);
        using var http = new HttpClient(handler, disposeHandler: false);
        var pem = await http.GetStringAsync(new Uri(root.GetProperty("SigningCertURL").GetString()!));
        using var certificate = X509Certificate2.CreateFromPem(pem);
        using var key = certificate.GetRSAPublicKey()!;

        var canonical = new StringBuilder();
        foreach (var name in new[] { "Message", "MessageId", "Subject", "Timestamp", "TopicArn", "Type" })
        {
            canonical.Append(name).Append('\n').Append(root.GetProperty(name).GetString()).Append('\n');
        }

        var signature = Convert.FromBase64String(root.GetProperty("Signature").GetString()!);
        var data = Encoding.UTF8.GetBytes(canonical.ToString());
        key.VerifyData(data, signature, new HashAlgorithmName(digest), RSASignaturePadding.Pkcs1).ShouldBeTrue();
        data[0] ^= 1;
        key.VerifyData(data, signature, new HashAlgorithmName(digest), RSASignaturePadding.Pkcs1).ShouldBeFalse();
    }

    [Test]
    [Arguments("+5", "5")]
    [Arguments(".5", "0.5")]
    [Arguments("1.", "1")]
    [Arguments("1.500", "1.5")]
    [Arguments("42", "42")]
    [Arguments("100", "100")]
    [Arguments("-0.0", "0")]
    [Arguments("-012.50", "-12.5")]
    [Arguments("1e3", "1000")]
    [Arguments("1.5E-3", "0.0015")]
    [Arguments("not-a-number", "not-a-number")]
    public void RawDeliveryWritesNumbersInCanonicalForm(string published, string delivered) =>
        SnsRawMessageAttributes.CanonicalNumber(published).ShouldBe(delivered);
}
