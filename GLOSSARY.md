# InMemoryAws

An in-memory emulator of AWS SQS, SNS, EventBridge and DynamoDB, answering requests and delivering messages as those services do.

## The emulator

**Cloud**:
One simulated AWS account across every region (`InMemoryAwsCloud`): all of its resources (queues, topics, event buses, tables) and the settings they run on (the clock, the account and current region, the `HttpClient` that outbound calls use). Each SDK client a test creates talks to one cloud; the server keeps one per account.
_Avoid_: bus (an EventBridge event bus is a resource inside a cloud), store, context

## Requests

**Wire protocol**:
How a service's requests and responses are written over HTTP (AWS JSON 1.0 or 1.1, AWS Query, or S3's REST-XML), including how it states its errors and which headers it answers with. SQS speaks two; every other service speaks one.
_Avoid_: transport, handler (for the concept)

## Delivery

**Send**:
A message a caller puts on a queue with `SendMessage` or `SendMessageBatch`, subject to the caller's identity and the queue's policy.
_Avoid_: enqueue (for the caller's act)

**Service delivery**:
A message an AWS service (SNS for a subscription, EventBridge for a rule's target, S3 for a bucket's event notification) puts on a queue on behalf of one of its resources, admitted or refused by the queue's policy for that service and source.
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

**Move task**:
A task started on a dead-letter queue that moves its visible messages, as redriven messages, back to the queues they were dead-lettered from or to one destination queue, until it completes, fails or is cancelled.
_Avoid_: redrive task, DLQ redrive

## Message lifecycle

**Message lifecycle**:
The stages a message passes through on one queue: delayed, visible, in flight, and gone (deleted, expired past the retention period, purged, redriven or moved). The queue alone moves a message between them.
_Avoid_: message state

**Visible**:
Waiting on the queue to be received, its delay (if any) over and not in flight.
_Avoid_: available, pending

**In flight**:
Received and neither deleted nor visible again: hidden from other receives until its visibility timeout ends.
_Avoid_: inflight, not visible, locked (for a message)

**Locked message group**:
A FIFO message group with a message in flight, from which the queue delivers nothing further until that message leaves flight.
_Avoid_: blocked group

**Lapsed receipt handle**:
The latest receipt handle of a message whose visibility timeout has ended: it still deletes the message or hides it again.

**Stale receipt handle**:
An earlier receipt handle of a message received again since, under a newer one: a FIFO queue refuses it as expired.

## Resources

**ARN resolution**:
Finding the resource an ARN names, in the region and account the ARN names, at the moment it is used. A resource deleted and recreated under the same ARN is the same target.
_Avoid_: lookup by name

**Resource policy**:
The IAM policy document a queue, topic, event bus, DynamoDB table or S3 bucket holds, saying which callers and services may act on it. Each service names its actions with its own prefix and writes its documents in its own way, but the document and its meaning are the same concept for all of them.
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

**Vector index**:
An index over one attribute holding a list of numbers (a vector), searched by `SearchVectors` for the items nearest a query vector by its distance function. Its search schema names the attributes a search can be scoped by: one `HASH` attribute a search must name, and `INLINE_FILTER` attributes it may.
_Avoid_: embedding index, KNN index

**Item change**:
One write as streams, `ReturnValues` and point-in-time recovery see it: the item before and after, either of which may be absent.
_Avoid_: event (EventBridge's term)
