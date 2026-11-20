using System.Net;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using System.Xml.Linq;
using InMemoryAws.Http;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the S3 notification ports share: botocore's <c>ClientError.response["Error"]</c> for a
/// PutBucketNotificationConfiguration refusal, with every element of S3's error document. The .NET
/// SDK keeps only the code and message, but S3 names the refused argument in further elements
/// (<c>ArgumentName</c>, <c>ArgumentValue</c>, or <c>ArgumentName1</c>/<c>ArgumentValue1</c>, ... for
/// each destination it could not validate), so these requests go over raw HTTP.
/// </summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// Sends PutBucketNotificationConfiguration with <paramref name="configurationXml"/> (the
    /// elements inside <c>NotificationConfiguration</c>) and returns the refusal's error document
    /// as botocore reports it (<c>Code</c>, <c>Message</c>, <c>ArgumentName</c>, ...), with the HTTP
    /// status; <see langword="null"/> when the call succeeded.
    /// </summary>
    public static async Task<NotificationError?> PutNotificationConfigurationRaw(FidelityContext ctx, string bucket, string configurationXml)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var body = "<NotificationConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" + configurationXml + "</NotificationConfiguration>";
        using var content = new StringContent(body, Encoding.UTF8, "application/xml");
        using var response = await ctx.RawHttpClient(AwsServiceType.S3).PutAsync(new Uri(bucket + "?notification", UriKind.Relative), content);
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var fields = document.Root!.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value, StringComparer.Ordinal);
        return new NotificationError(fields, response.StatusCode);
    }

    /// <summary>
    /// The policy the notification checks put on a destination: <c>s3.amazonaws.com</c> may perform
    /// <paramref name="action"/> on <paramref name="resourceArn"/> for the bucket <paramref name="bucket"/>
    /// (<c>ArnEquals aws:SourceArn</c>), as JSON.
    /// </summary>
    public static string NotificationAllowS3Policy(string resourceArn, string bucket, string action = "sqs:SendMessage") =>
        new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "s3.amazonaws.com" },
                ["Action"] = action,
                ["Resource"] = resourceArn,
                ["Condition"] = new JsonObject
                {
                    ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = $"arn:aws:s3:::{bucket}" },
                },
            }),
        }.ToJsonString();

    /// <summary><c>sqs.get_queue_attributes(QueueUrl=url, AttributeNames=["QueueArn"])["Attributes"]["QueueArn"]</c>.</summary>
    public static async Task<string> NotificationQueueArn(IAmazonSQS sqs, string queueUrl)
    {
        ArgumentNullException.ThrowIfNull(sqs);
        var attributes = await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] });
        return attributes.Attributes["QueueArn"];
    }

    /// <summary>The checks' <c>_delete_queue</c>: deletes a queue, ignoring one already gone.</summary>
    public static async Task NotificationDeleteQueue(IAmazonSQS sqs, string queueUrl)
    {
        ArgumentNullException.ThrowIfNull(sqs);
        try
        {
            await sqs.DeleteQueueAsync(queueUrl);
        }
        catch (AmazonSQSException ex) when (ex.ErrorCode is "AWS.SimpleQueueService.NonExistentQueue" or "QueueDoesNotExist")
        {
        }
    }

    /// <summary>The checks' <c>_delete_topic</c>: deletes a topic, ignoring one already gone.</summary>
    public static async Task NotificationDeleteTopic(Amazon.SimpleNotificationService.IAmazonSimpleNotificationService sns, string topicArn)
    {
        ArgumentNullException.ThrowIfNull(sns);
        try
        {
            await sns.DeleteTopicAsync(topicArn);
        }
        catch (Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceException ex) when (ex.ErrorCode == "NotFound")
        {
        }
    }

    /// <summary>A queue configuration's XML: <c>&lt;QueueConfiguration&gt;</c> with its id, ARN, events and prefix/suffix filter rules.</summary>
    public static string QueueConfigurationXml(string queueArn, IEnumerable<string> events, string? id = null, IEnumerable<(string Name, string Value)>? filterRules = null) =>
        ConfigurationXml("QueueConfiguration", "Queue", queueArn, events, id, filterRules);

    /// <summary>A topic configuration's XML: <c>&lt;TopicConfiguration&gt;</c> with its id, ARN, events and prefix/suffix filter rules.</summary>
    public static string TopicConfigurationXml(string topicArn, IEnumerable<string> events, string? id = null, IEnumerable<(string Name, string Value)>? filterRules = null) =>
        ConfigurationXml("TopicConfiguration", "Topic", topicArn, events, id, filterRules);

    private static string ConfigurationXml(string element, string arnElement, string arn, IEnumerable<string> events, string? id, IEnumerable<(string Name, string Value)>? filterRules)
    {
        var xml = new StringBuilder();
        xml.Append('<').Append(element).Append('>');
        if (id is not null)
        {
            xml.Append("<Id>").Append(SecurityElement.Escape(id)).Append("</Id>");
        }

        xml.Append('<').Append(arnElement).Append('>').Append(SecurityElement.Escape(arn)).Append("</").Append(arnElement).Append('>');
        foreach (var name in events)
        {
            xml.Append("<Event>").Append(SecurityElement.Escape(name)).Append("</Event>");
        }

        if (filterRules is not null)
        {
            xml.Append("<Filter><S3Key>");
            foreach (var (name, value) in filterRules)
            {
                xml.Append("<FilterRule><Name>").Append(SecurityElement.Escape(name)).Append("</Name><Value>")
                    .Append(SecurityElement.Escape(value)).Append("</Value></FilterRule>");
            }

            xml.Append("</S3Key></Filter>");
        }

        return xml.Append("</").Append(element).Append('>').ToString();
    }
}

/// <summary>An S3 error document's elements, as botocore puts them in <c>response["Error"]</c>, and the HTTP status.</summary>
public sealed record NotificationError(IReadOnlyDictionary<string, string> Fields, HttpStatusCode StatusCode)
{
    /// <summary><c>err.get(name)</c>: an element's text, or <see langword="null"/>.</summary>
    public string? Get(string name) => Fields.TryGetValue(name, out var value) ? value : null;

    public string? Code => Get("Code");

    public string? Message => Get("Message");

    public override string ToString() => "{" + string.Join(", ", Fields.Select(f => $"'{f.Key}': '{f.Value}'")) + "}";
}
