export interface BusState {
  accounts: string[];
  currentAccount: string;
  queues: QueueInfo[];
  topics: TopicInfo[];
  subscriptions: SubscriptionInfo[];
  recentOperations: OperationInfo[] | null;
}

export interface QueueInfo {
  name: string;
  arn: string;
  url: string;
  isFifo: boolean;
  messagesAvailable: number;
  messagesInFlight: number;
  visibilityTimeoutSeconds: number;
  hasDeadLetterQueue: boolean;
  deadLetterQueueName: string | null;
  maxReceiveCount: number | null;
}

export interface TopicInfo {
  name: string;
  arn: string;
}

export interface SubscriptionInfo {
  subscriptionArn: string;
  topicArn: string;
  endpoint: string;
  protocol: string;
  raw: boolean;
  filterPolicy: string | null;
}

export interface QueueMessages {
  queueName: string;
  pendingMessages: MessageInfo[];
  inFlightMessages: MessageInfo[];
}

export interface MessageInfo {
  messageId: string;
  body: string;
  inFlight: boolean;
  messageGroupId: string | null;
  attributes: Record<string, string> | null;
  messageAttributes: Record<string, string> | null;
}

export interface OperationInfo {
  service: string;
  action: string;
  resourceArn: string | null;
  timestamp: string;
  success: boolean;
}
