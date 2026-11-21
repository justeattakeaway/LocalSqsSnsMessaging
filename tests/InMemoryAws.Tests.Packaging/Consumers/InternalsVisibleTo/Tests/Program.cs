using InMemoryAws;

using var sqs = new InMemoryAwsCloud().CreateSqsClient();
Console.WriteLine((await sqs.CreateQueueAsync("orders")).QueueUrl);
