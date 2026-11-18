import type { CloudState, QueueInfo, TopicInfo, SubscriptionInfo, MoveTaskInfo } from "@/types";

export function getTopicSubCount(state: CloudState, topic: TopicInfo): number {
  return state.subscriptions.filter((s) => s.topicArn === topic.arn).length;
}

export function getTopicSubscriptions(
  state: CloudState,
  topic: TopicInfo
): SubscriptionInfo[] {
  return state.subscriptions.filter((s) => s.topicArn === topic.arn);
}

export function getQueueSubscriptions(
  state: CloudState,
  queue: QueueInfo
): SubscriptionInfo[] {
  return state.subscriptions.filter(
    (s) => s.endpoint === queue.arn || s.endpoint === queue.url
  );
}

export function getTopicNameFromArn(arn: string): string {
  const parts = arn.split(":");
  return parts[parts.length - 1];
}

export function getQueueNameFromEndpoint(endpoint: string): string {
  if (endpoint.startsWith("arn:")) {
    const parts = endpoint.split(":");
    return parts[parts.length - 1];
  }
  const parts = endpoint.split("/");
  return parts[parts.length - 1];
}

export function isDlqTarget(state: CloudState, queue: QueueInfo): boolean {
  return state.queues.some((q) => q.deadLetterQueueName === queue.name);
}

export function getActiveMoveTask(
  state: CloudState,
  queue: QueueInfo
): MoveTaskInfo | null {
  return (
    state.moveTasks?.find(
      (t) => t.sourceArn === queue.arn && t.status === "RUNNING"
    ) ?? null
  );
}
