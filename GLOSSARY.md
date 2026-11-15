# LocalSqsSnsMessaging

An in-memory emulator of AWS SQS, SNS, EventBridge and DynamoDB, answering requests and delivering messages as those services do.

## Delivery

**Send**:
A message a caller puts on a queue with `SendMessage` or `SendMessageBatch`, subject to the caller's identity and the queue's policy.
_Avoid_: enqueue (for the caller's act)

**Service delivery**:
A message an AWS service (SNS for a subscription, EventBridge for a rule's target) puts on a queue on behalf of one of its resources, admitted or refused by the queue's policy for that service and source.
_Avoid_: publish to queue, forward

**Redrive**:
A message moved from a source queue to its dead-letter queue, either automatically after `maxReceiveCount` receives or by a move task. A redriven message is not a new send: it claims no deduplication id.
_Avoid_: requeue, retry

**Dead-letter queue**:
The queue a failed message goes to: for an SQS source queue, the one its redrive policy names; for an SNS subscription or an EventBridge target, the one its own dead-letter configuration names.
_Avoid_: DLQ (in prose), error queue

## Resources

**ARN resolution**:
Finding the resource an ARN names, in the region and account the ARN names, at the moment it is used. A resource deleted and recreated under the same ARN is the same target.
_Avoid_: lookup by name

**Resource policy**:
The IAM policy document a queue, topic or event bus holds, saying which callers and services may act on it. Each service names its actions with its own prefix and writes its documents in its own way, but the document and its meaning are the same concept for all three.
_Avoid_: queue policy, topic policy, access policy (for the concept in general); permission (for the whole document)

**Statement id**:
The `Sid` that names one statement in a resource policy; permission operations add, replace and remove statements by it. SQS and SNS call it a label, EventBridge a statement id.
_Avoid_: label (outside the SQS and SNS parameter name)

## DynamoDB

**Canonical form**:
The one way DynamoDB stores a value: a number without redundant zeros or an exponent (`1.50` and `15E-1` are both `1.5`), every number in a set canonical. A request's value is kept as sent until it is canonicalized, because whether a bad number is refused, and how, depends on where it appears.
_Avoid_: normalised value

**Validation envelope**:
The `1 validation error detected: ...` (or `N validation errors detected: ...; ...`) wrapping AWS puts around some refusals but not others. Request-model constraint violations, and values an item or condition carries, are enveloped; values a statement spells or an expression computes, and filter, projection and key-condition refusals, are stated bare.
_Avoid_: prefix

**Item change**:
One write as streams, `ReturnValues` and point-in-time recovery see it: the item before and after, either of which may be absent.
_Avoid_: event (EventBridge's term)
