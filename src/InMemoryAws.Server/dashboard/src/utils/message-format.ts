export interface SnsEnvelope {
  type: "sns-notification";
  topicArn: string;
  messageId: string;
  timestamp: string;
  subject: string | null;
  innerMessage: string;
  messageAttributes: Record<string, { Type: string; Value: string }> | null;
}

export interface RawMessage {
  type: "raw";
}

export type MessageFormat = SnsEnvelope | RawMessage;

export function detectMessageFormat(body: string): MessageFormat {
  try {
    const parsed = JSON.parse(body);
    if (
      parsed &&
      typeof parsed === "object" &&
      parsed.Type === "Notification" &&
      typeof parsed.TopicArn === "string" &&
      typeof parsed.Message === "string"
    ) {
      return {
        type: "sns-notification",
        topicArn: parsed.TopicArn,
        messageId: parsed.MessageId ?? "",
        timestamp: parsed.Timestamp ?? "",
        subject: parsed.Subject ?? null,
        innerMessage: parsed.Message,
        messageAttributes: parsed.MessageAttributes ?? null,
      };
    }
  } catch {
    // not JSON
  }
  return { type: "raw" };
}

export function getPreviewText(body: string): string {
  const format = detectMessageFormat(body);
  if (format.type === "sns-notification") {
    return format.innerMessage;
  }
  return body;
}
