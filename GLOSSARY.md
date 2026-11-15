# LocalSqsSnsMessaging

An in-memory emulator of AWS SQS, SNS and EventBridge, answering requests and delivering messages as those services do.

## Delivery

**Send**:
A message a caller puts on a queue with `SendMessage` or `SendMessageBatch`, subject to the caller's identity and the queue's policy.
_Avoid_: enqueue (for the caller's act)

**Service delivery**:
A message an AWS service (SNS for a subscription, EventBridge for a rule's target) puts on a queue on behalf of one of its resources, admitted or refused by the queue's policy for that service and source.
_Avoid_: publish to queue, forward

**Fan-out**:
A topic handing one published message to each of its confirmed subscriptions whose filter policy matches it, through a service delivery for a queue or a POST for an HTTP/S endpoint. A subscription still pending confirmation takes no part in it.
_Avoid_: broadcast, publish action

**Replay**:
A FIFO topic delivering again, to one subscription, the archived messages its replay policy selects, before any live message that follows them.
_Avoid_: redelivery, redrive (which is about dead-letter queues)

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
