import type { SubscriptionInfo } from "@/types";
import {
  getTopicNameFromArn,
  getQueueNameFromEndpoint,
} from "@/utils/resource-helpers";

interface SubscriptionItemProps {
  sub: SubscriptionInfo;
  nameFrom: "topic" | "queue";
}

export function SubscriptionItem({ sub, nameFrom }: SubscriptionItemProps) {
  const name =
    nameFrom === "topic"
      ? getTopicNameFromArn(sub.topicArn)
      : getQueueNameFromEndpoint(sub.endpoint);

  return (
    <div className="rounded-md border border-border bg-background p-3 mb-2">
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
