#!/bin/bash
# Seeds the InMemoryAws server with sample resources and messages
# across multiple accounts to demonstrate multi-account support.
# Usage: ./seed-data.sh [base-url]

BASE=${1:-http://localhost:5050}

sqs() {
  local account=$1 op=$2 body=$3
  curl -s -X POST "$BASE" \
    -H "Content-Type: application/x-amz-json-1.0" \
    -H "X-Amz-Target: AmazonSQS.$op" \
    -H "Authorization: AWS4-HMAC-SHA256 Credential=$account/20250101/us-east-1/sqs/aws4_request, SignedHeaders=host, Signature=fake" \
    -d "$body" > /dev/null
}

sns() {
  local account=$1 body=$2
  curl -s -X POST "$BASE" \
    -H "Content-Type: application/x-www-form-urlencoded" \
    -H "Authorization: AWS4-HMAC-SHA256 Credential=$account/20250101/us-east-1/sns/aws4_request, SignedHeaders=host, Signature=fake" \
    -d "$body" > /dev/null
}

ddb() {
  local account=$1 op=$2 body=$3
  curl -s -X POST "$BASE" \
    -H "Content-Type: application/x-amz-json-1.0" \
    -H "X-Amz-Target: DynamoDB_20120810.$op" \
    -H "Authorization: AWS4-HMAC-SHA256 Credential=$account/20250101/us-east-1/dynamodb/aws4_request, SignedHeaders=host, Signature=fake" \
    -d "$body" > /dev/null
}

# === Account 1: 000000000000 (default - e-commerce) ===
ACCT1=000000000000
echo "=== Account $ACCT1 (e-commerce) ==="

echo "Creating queues..."
sqs $ACCT1 CreateQueue '{"QueueName":"orders-queue"}'
sqs $ACCT1 CreateQueue '{"QueueName":"notifications-queue"}'
sqs $ACCT1 CreateQueue '{"QueueName":"orders-dlq"}'

echo "Setting DLQ policy..."
sqs $ACCT1 SetQueueAttributes "{\"QueueUrl\":\"$BASE/$ACCT1/orders-queue\",\"Attributes\":{\"RedrivePolicy\":\"{\\\"deadLetterTargetArn\\\":\\\"arn:aws:sqs:us-east-1:$ACCT1:orders-dlq\\\",\\\"maxReceiveCount\\\":3}\"}}"

echo "Creating topics..."
sns $ACCT1 "Action=CreateTopic&Name=order-events"
sns $ACCT1 "Action=CreateTopic&Name=payment-events"

echo "Subscribing queues to topics..."
sns $ACCT1 "Action=Subscribe&TopicArn=arn:aws:sns:us-east-1:$ACCT1:order-events&Protocol=sqs&Endpoint=arn:aws:sqs:us-east-1:$ACCT1:orders-queue"
sns $ACCT1 "Action=Subscribe&TopicArn=arn:aws:sns:us-east-1:$ACCT1:order-events&Protocol=sqs&Endpoint=arn:aws:sqs:us-east-1:$ACCT1:notifications-queue"
sns $ACCT1 "Action=Subscribe&TopicArn=arn:aws:sns:us-east-1:$ACCT1:payment-events&Protocol=sqs&Endpoint=arn:aws:sqs:us-east-1:$ACCT1:notifications-queue"

echo "Publishing messages..."
sns $ACCT1 'Action=Publish&TopicArn=arn:aws:sns:us-east-1:000000000000:order-events&Message={"orderId":"ORD-001","item":"Widget","quantity":5,"total":49.95}'
sns $ACCT1 'Action=Publish&TopicArn=arn:aws:sns:us-east-1:000000000000:order-events&Message={"orderId":"ORD-002","item":"Gadget","quantity":2,"total":129.00}'
sns $ACCT1 'Action=Publish&TopicArn=arn:aws:sns:us-east-1:000000000000:payment-events&Message={"paymentId":"PAY-001","orderId":"ORD-001","amount":49.95,"status":"completed"}'

echo "Sending direct SQS message with attributes..."
sqs $ACCT1 SendMessage "{\"QueueUrl\":\"$BASE/$ACCT1/orders-queue\",\"MessageBody\":\"{\\\"orderId\\\":\\\"ORD-003\\\",\\\"item\\\":\\\"Thingamajig\\\",\\\"quantity\\\":1,\\\"total\\\":19.99}\",\"MessageAttributes\":{\"priority\":{\"DataType\":\"String\",\"StringValue\":\"high\"},\"source\":{\"DataType\":\"String\",\"StringValue\":\"api-gateway\"}}}"

# === Account 2: 111111111111 (analytics) ===
ACCT2=111111111111
echo ""
echo "=== Account $ACCT2 (analytics) ==="

echo "Creating queues..."
sqs $ACCT2 CreateQueue '{"QueueName":"clickstream-queue"}'
sqs $ACCT2 CreateQueue '{"QueueName":"metrics-queue"}'

echo "Creating topics..."
sns $ACCT2 "Action=CreateTopic&Name=user-activity"

echo "Subscribing queues to topics..."
sns $ACCT2 "Action=Subscribe&TopicArn=arn:aws:sns:us-east-1:$ACCT2:user-activity&Protocol=sqs&Endpoint=arn:aws:sqs:us-east-1:$ACCT2:clickstream-queue"
sns $ACCT2 "Action=Subscribe&TopicArn=arn:aws:sns:us-east-1:$ACCT2:user-activity&Protocol=sqs&Endpoint=arn:aws:sqs:us-east-1:$ACCT2:metrics-queue"

echo "Publishing messages..."
sns $ACCT2 'Action=Publish&TopicArn=arn:aws:sns:us-east-1:111111111111:user-activity&Message={"userId":"U-100","event":"page_view","page":"/products","timestamp":"2025-01-15T10:30:00Z"}'
sns $ACCT2 'Action=Publish&TopicArn=arn:aws:sns:us-east-1:111111111111:user-activity&Message={"userId":"U-101","event":"add_to_cart","item":"Widget","timestamp":"2025-01-15T10:31:00Z"}'

# === Account 3: 222222222222 (email service) ===
ACCT3=222222222222
echo ""
echo "=== Account $ACCT3 (email service) ==="

echo "Creating queues..."
sqs $ACCT3 CreateQueue '{"QueueName":"email-send-queue"}'
sqs $ACCT3 CreateQueue '{"QueueName":"email-bounce-queue"}'

echo "Sending messages..."
sqs $ACCT3 SendMessage "{\"QueueUrl\":\"$BASE/$ACCT3/email-send-queue\",\"MessageBody\":\"{\\\"to\\\":\\\"user@example.com\\\",\\\"subject\\\":\\\"Order Confirmation\\\",\\\"template\\\":\\\"order-confirm\\\"}\"}"
sqs $ACCT3 SendMessage "{\"QueueUrl\":\"$BASE/$ACCT3/email-send-queue\",\"MessageBody\":\"{\\\"to\\\":\\\"admin@example.com\\\",\\\"subject\\\":\\\"Daily Report\\\",\\\"template\\\":\\\"daily-report\\\"}\"}"

echo "Creating a DynamoDB table with a global secondary index..."
ddb $ACCT1 CreateTable '{"TableName":"orders","BillingMode":"PAY_PER_REQUEST","KeySchema":[{"AttributeName":"customerId","KeyType":"HASH"},{"AttributeName":"orderId","KeyType":"RANGE"}],"AttributeDefinitions":[{"AttributeName":"customerId","AttributeType":"S"},{"AttributeName":"orderId","AttributeType":"S"},{"AttributeName":"status","AttributeType":"S"}],"GlobalSecondaryIndexes":[{"IndexName":"by-status","KeySchema":[{"AttributeName":"status","KeyType":"HASH"}],"Projection":{"ProjectionType":"ALL"}}],"StreamSpecification":{"StreamEnabled":true,"StreamViewType":"NEW_AND_OLD_IMAGES"}}'
ddb $ACCT1 PutItem '{"TableName":"orders","Item":{"customerId":{"S":"cust-1"},"orderId":{"S":"ord-001"},"status":{"S":"PENDING"},"total":{"N":"49.99"},"items":{"L":[{"M":{"sku":{"S":"WIDGET-42"},"qty":{"N":"2"}}}]}}}'
ddb $ACCT1 PutItem '{"TableName":"orders","Item":{"customerId":{"S":"cust-1"},"orderId":{"S":"ord-002"},"status":{"S":"SHIPPED"},"total":{"N":"12.50"}}}'
ddb $ACCT1 PutItem '{"TableName":"orders","Item":{"customerId":{"S":"cust-2"},"orderId":{"S":"ord-003"},"status":{"S":"PENDING"},"total":{"N":"99"},"tags":{"SS":["gift","express"]}}}'

echo ""
echo "Done! Seeded 3 accounts:"
echo "  $ACCT1: 3 queues, 2 topics, 3 subscriptions, 4 messages, 1 DynamoDB table with 3 items"
echo "  $ACCT2: 2 queues, 1 topic, 2 subscriptions, 2 messages"
echo "  $ACCT3: 2 queues, 0 topics, 0 subscriptions, 2 messages"
