#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sns.Model;
using Message = LocalSqsSnsMessaging.Sqs.Model.Message;
using SqsMessageAttributeValue = LocalSqsSnsMessaging.Sqs.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging;

/// <summary>The JSON envelope SNS wraps a notification in.</summary>
internal sealed partial class SnsPublishAction
{
    private JsonObject CreateSnsEnvelope(SnsSubscription subscription, OutboundMessage message)
    {
        var snsMessage = new JsonObject
        {
            ["Type"] = "Notification",
            ["MessageId"] = message.MessageId,
            ["TopicArn"] = message.TopicArn,
            ["Message"] = message.Body,
            ["Timestamp"] = SnsHttpDelivery.Timestamp(_bus),
            ["SignatureVersion"] = "1",
            ["Signature"] = "EXAMPLE",
            ["SigningCertURL"] = "EXAMPLE",
            ["UnsubscribeURL"] = SnsHttpDelivery.UnsubscribeUrl(_bus, subscription)
        };

        if (message.SequenceNumber is not null)
        {
            snsMessage["SequenceNumber"] = message.SequenceNumber;
        }

        if (message.Subject is not null)
        {
            snsMessage["Subject"] = message.Subject;
        }

        if (message.Attributes is not null && message.Attributes.Count > 0)
        {
            var messageAttributes = new JsonObject();
            foreach (var (key, value) in message.Attributes)
            {
                messageAttributes[key] = new JsonObject
                {
                    ["Type"] = value.DataType,
                    ["Value"] = value.StringValue ?? Convert.ToBase64String(value.BinaryValue.ToArray())
                };
            }
            snsMessage["MessageAttributes"] = messageAttributes;
        }

        return snsMessage;
    }
}
