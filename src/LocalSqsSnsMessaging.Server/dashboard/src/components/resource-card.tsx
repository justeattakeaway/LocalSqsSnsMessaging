import { Badge } from "@/components/ui/badge";
import { SnsIcon, SqsIcon } from "@/components/aws-icons";
import type { BusState, QueueInfo, TopicInfo } from "@/types";
import { getTopicSubCount, isDlqTarget } from "@/utils/resource-helpers";
import { cn } from "@/lib/utils";

interface ResourceCardProps {
  type: "topic" | "queue";
  resource: QueueInfo | TopicInfo;
  selected: boolean;
  state: BusState;
  onClick: () => void;
}

export function ResourceCard({
  type,
  resource,
  selected,
  state,
  onClick,
}: ResourceCardProps) {
  const isDlq = type === "queue" && isDlqTarget(state, resource as QueueInfo);
  const queue = type === "queue" ? (resource as QueueInfo) : null;

  return (
    <div
      className={cn(
        "rounded-lg border bg-card p-3 cursor-pointer transition-all pointer-events-auto",
        "hover:bg-surface-hover hover:border-muted-foreground/30",
        selected && "border-highlight shadow-[0_0_0_1px_var(--color-highlight)]",
        !selected && "border-border",
        type === "topic" && "border-l-[3px] border-l-sns",
        type === "queue" && !isDlq && "border-l-[3px] border-l-sqs",
        type === "queue" && isDlq && "border-l-[3px] border-l-dlq"
      )}
      data-arn={resource.arn}
      onClick={onClick}
    >
      <div className="flex items-center gap-2">
        {type === "topic" ? (
          <SnsIcon className="size-6 shrink-0 text-sns" />
        ) : (
          <SqsIcon className={cn("size-6 shrink-0", isDlq ? "text-dlq" : "text-sqs")} />
        )}
        <div>
          <div className="text-sm font-semibold text-foreground break-all leading-snug">
            {resource.name}
          </div>
          <div className="text-[11px] text-muted-foreground mt-0.5">
            {type === "topic"
              ? "SNS Topic"
              : isDlq
                ? "Dead Letter Queue"
                : "SQS Queue"}
          </div>
        </div>
      </div>
      <div className="flex gap-1.5 mt-2 flex-wrap">
        {type === "topic" && (
          <Badge
            variant="outline"
            className="border-sns/40 bg-sns/10 text-sns text-[11px] font-mono px-1.5 py-0 rounded"
          >
            {getTopicSubCount(state, resource as TopicInfo)} sub
            {getTopicSubCount(state, resource as TopicInfo) !== 1 ? "s" : ""}
          </Badge>
        )}
        {queue && (
          <>
            <Badge
              variant="outline"
              className="border-sqs/40 bg-sqs/10 text-sqs text-[11px] font-mono px-1.5 py-0 rounded"
            >
              {queue.messagesAvailable} msgs
            </Badge>
            {queue.messagesInFlight > 0 && (
              <Badge
                variant="outline"
                className="border-sns/40 bg-sns/10 text-sns text-[11px] font-mono px-1.5 py-0 rounded"
              >
                {queue.messagesInFlight} in-flight
              </Badge>
            )}
            {queue.isFifo && (
              <Badge
                variant="outline"
                className="border-purple-500/25 bg-purple-500/10 text-purple-400 text-[11px] font-mono px-1.5 py-0 rounded"
              >
                FIFO
              </Badge>
            )}
            {queue.hasDeadLetterQueue && (
              <Badge
                variant="outline"
                className="border-dlq/25 bg-dlq/10 text-dlq text-[11px] font-mono px-1.5 py-0 rounded"
              >
                DLQ
              </Badge>
            )}
          </>
        )}
      </div>
    </div>
  );
}
