#!/bin/bash
# Seeds the LocalSqsSnsMessaging server with sample resources and messages.
# Usage: ./seed-data.sh [base-url]

BASE=${1:-http://localhost:5050}

sqs() {
  curl -s -X POST "$BASE" \
    -H "Content-Type: application/x-amz-json-1.0" \
    -H "X-Amz-Target: AmazonSQS.$1" \
    -d "$2" > /dev/null
}

sns() {
  curl -s -X POST "$BASE" \
    -H "Content-Type: application/x-www-form-urlencoded" \
    -d "$1" > /dev/null
}

echo "Creating queues..."
sqs CreateQueue '{"QueueName":"orders-queue"}'
sqs CreateQueue '{"QueueName":"notifications-queue"}'
sqs CreateQueue '{"QueueName":"orders-dlq"}'

echo "Setting DLQ policy..."
sqs SetQueueAttributes "{\"QueueUrl\":\"$BASE/000000000000/orders-queue\",\"Attributes\":{\"RedrivePolicy\":\"{\\\"deadLetterTargetArn\\\":\\\"arn:aws:sqs:us-east-1:000000000000:orders-dlq\\\",\\\"maxReceiveCount\\\":3}\"}}"

echo "Creating topics..."
sns "Action=CreateTopic&Name=order-events"
sns "Action=CreateTopic&Name=payment-events"

echo "Subscribing queues to topics..."
sns "Action=Subscribe&TopicArn=arn:aws:sns:us-east-1:000000000000:order-events&Protocol=sqs&Endpoint=arn:aws:sqs:us-east-1:000000000000:orders-queue"
sns "Action=Subscribe&TopicArn=arn:aws:sns:us-east-1:000000000000:order-events&Protocol=sqs&Endpoint=arn:aws:sqs:us-east-1:000000000000:notifications-queue"
sns "Action=Subscribe&TopicArn=arn:aws:sns:us-east-1:000000000000:payment-events&Protocol=sqs&Endpoint=arn:aws:sqs:us-east-1:000000000000:notifications-queue"

echo "Publishing messages..."
sns 'Action=Publish&TopicArn=arn:aws:sns:us-east-1:000000000000:order-events&Message={"orderId":"ORD-001","item":"Widget","quantity":5,"total":49.95}'
sns 'Action=Publish&TopicArn=arn:aws:sns:us-east-1:000000000000:order-events&Message={"orderId":"ORD-002","item":"Gadget","quantity":2,"total":129.00}'
sns 'Action=Publish&TopicArn=arn:aws:sns:us-east-1:000000000000:payment-events&Message={"paymentId":"PAY-001","orderId":"ORD-001","amount":49.95,"status":"completed"}'

echo "Sending direct SQS message with attributes..."
sqs SendMessage "{\"QueueUrl\":\"$BASE/000000000000/orders-queue\",\"MessageBody\":\"{\\\"orderId\\\":\\\"ORD-003\\\",\\\"item\\\":\\\"Thingamajig\\\",\\\"quantity\\\":1,\\\"total\\\":19.99}\",\"MessageAttributes\":{\"priority\":{\"DataType\":\"String\",\"StringValue\":\"high\"},\"source\":{\"DataType\":\"String\",\"StringValue\":\"api-gateway\"}}}"

echo "Done! Created 3 queues, 2 topics, 3 subscriptions, 4 messages."
