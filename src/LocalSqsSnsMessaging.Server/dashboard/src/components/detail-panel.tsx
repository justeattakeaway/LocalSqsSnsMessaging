import React from "react";
import { QueueDetail } from "@/components/queue-detail";
import { TopicDetail } from "@/components/topic-detail";
import type { BusState, QueueInfo, TopicInfo, SubscriptionInfo } from "@/types";
import {
  getTopicNameFromArn,
  getQueueNameFromEndpoint,
} from "@/utils/resource-helpers";

interface DetailPanelProps {
  state: BusState;
  selected: QueueInfo | TopicInfo | null;
  selectedType: "queue" | "topic" | "subscription" | null;
  selectedSubscription: SubscriptionInfo | null;
  selectSubscription: (sub: SubscriptionInfo) => void;
}

export function DetailPanel({
  state,
  selected,
  selectedType,
  selectedSubscription,
  selectSubscription,
}: DetailPanelProps) {
  if (selectedType === "subscription" && selectedSubscription) {
    return (
      <div className="flex-1 min-w-0 bg-card border-l border-border flex flex-col overflow-hidden">
        <div className="p-4 border-b border-border">
          <div className="text-sm font-semibold text-foreground break-all">
            Subscription
          </div>
          <div className="text-xs text-muted-foreground mt-0.5 break-all font-mono">
            {selectedSubscription.subscriptionArn}
          </div>
        </div>
        <div className="flex-1 overflow-y-auto overflow-x-hidden p-4 min-w-0">
          <div className="text-sm grid grid-cols-[100px_1fr]">
            {([
              ["Topic", getTopicNameFromArn(selectedSubscription.topicArn), false],
              ["Topic ARN", selectedSubscription.topicArn, true],
              ["Queue", getQueueNameFromEndpoint(selectedSubscription.endpoint), false],
              ["Endpoint", selectedSubscription.endpoint, true],
              ["Protocol", selectedSubscription.protocol, false],
              ["Delivery", selectedSubscription.raw ? "Raw delivery" : "SNS envelope", false],
              ...(selectedSubscription.filterPolicy
                ? [["Filter", selectedSubscription.filterPolicy, true] as [string, string, boolean]]
                : []),
            ] as [string, string | number | null, boolean][]).map(
              ([label, value, mono]) => (
                <React.Fragment key={label}>
                  <div className="py-1.5 text-muted-foreground border-b border-border">
                    {label}
                  </div>
                  <div
                    className={`py-1.5 border-b border-border overflow-hidden break-all${
                      mono ? " font-mono text-xs" : ""
                    }`}
                  >
                    {value}
                  </div>
                </React.Fragment>
              )
            )}
          </div>
        </div>
      </div>
    );
  }

  if (!selected) {
    return (
      <div className="flex-1 min-w-0 bg-card border-l border-border flex items-center justify-center">
        <span className="text-sm text-muted-foreground">
          Select a resource to view details
        </span>
      </div>
    );
  }

  return (
    <div className="flex-1 min-w-0 bg-card border-l border-border flex flex-col overflow-hidden">
      <div className="p-4 border-b border-border">
        <div className="text-sm font-semibold text-foreground break-all">
          {selected.name}
        </div>
        <div className="text-xs text-muted-foreground mt-0.5 break-all font-mono">
          {selected.arn}
        </div>
      </div>
      {selectedType === "queue" ? (
        <QueueDetail
          key={selected.arn}
          state={state}
          queue={selected as QueueInfo}
          selectSubscription={selectSubscription}
        />
      ) : (
        <TopicDetail
          key={selected.arn}
          state={state}
          topic={selected as TopicInfo}
          selectSubscription={selectSubscription}
        />
      )}
    </div>
  );
}
