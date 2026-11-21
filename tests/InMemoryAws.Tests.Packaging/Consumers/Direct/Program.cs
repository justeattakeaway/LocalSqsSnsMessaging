using Amazon.SimpleNotificationService.Model;
using InMemoryAws;

var cloud = new InMemoryAwsCloud();
using var sqs = cloud.CreateSqsClient("eu-west-1");
using var sns = cloud.CreateSnsClient("eu-west-1");
var queueUrl = (await sqs.CreateQueueAsync("orders")).QueueUrl;
var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).QueueARN;
var topicArn = (await sns.CreateTopicAsync("orders")).TopicArn;
await sns.SubscribeAsync(new SubscribeRequest(topicArn, "sqs", queueArn) { Attributes = new() { ["RawMessageDelivery"] = "true" } });
await sns.PublishAsync(topicArn, "placed");

Console.WriteLine($"{queueUrl} {(await sqs.ReceiveMessageAsync(queueUrl)).Messages[0].Body}");
