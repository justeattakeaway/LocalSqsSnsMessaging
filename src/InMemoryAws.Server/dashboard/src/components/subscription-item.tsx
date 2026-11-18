import type { SubscriptionInfo } from "@/types";
import {
  getTopicNameFromArn,
  getQueueNameFromEndpoint,
} from "@/utils/resource-helpers";
import { cn } from "@/lib/utils";

interface SubscriptionItemProps {
  sub: SubscriptionInfo;
  nameFrom: "topic" | "queue";
  onClick?: () => void;
}

export function SubscriptionItem({ sub, nameFrom, onClick }: SubscriptionItemProps) {
  const name =
    nameFrom === "topic"
      ? getTopicNameFromArn(sub.topicArn)
      : getQueueNameFromEndpoint(sub.endpoint);

  return (
    <div
      className={cn(
        "rounded-md border border-border bg-background p-3 mb-2",
        onClick && "cursor-pointer hover:border-muted-foreground/30 hover:bg-surface-hover transition-colors"
      )}
      onClick={onClick}
    >
      <div className="text-sm font-medium text-foreground">{name}</div>
      <div className="mt-0.5 text-xs text-muted-foreground">
        <span>{sub.raw ? "Raw delivery" : "SNS envelope"}</span>
        {sub.filterPolicy && (
          <span>
            {" "}
            &middot; Filter: <span>{sub.filterPolicy}</span>
          </span>
        )}
      </div>
    </div>
  );
}
