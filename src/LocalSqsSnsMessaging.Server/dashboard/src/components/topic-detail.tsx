import { useState } from "react";
import { SubscriptionItem } from "@/components/subscription-item";
import type { BusState, TopicInfo } from "@/types";
import { getTopicSubscriptions } from "@/utils/resource-helpers";
import { cn } from "@/lib/utils";

interface TopicDetailProps {
  state: BusState;
  topic: TopicInfo;
}

export function TopicDetail({ state, topic }: TopicDetailProps) {
  const [activeTab, setActiveTab] = useState("info");
  const subs = getTopicSubscriptions(state, topic);

  const tabs = [
    { id: "info", label: "Info" },
    { id: "subscriptions", label: `Subscriptions (${subs.length})` },
  ];

  return (
    <div className="flex flex-col flex-1 overflow-hidden">
      <div className="flex border-b border-border">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            className={cn(
              "px-4 py-2.5 text-xs font-medium border-b-2 transition-colors",
              activeTab === tab.id
                ? "text-foreground border-highlight"
                : "text-muted-foreground border-transparent hover:text-foreground"
            )}
            onClick={() => setActiveTab(tab.id)}
          >
            {tab.label}
          </button>
        ))}
      </div>

      <div className="flex-1 overflow-y-auto overflow-x-hidden p-4 min-w-0">
        {activeTab === "info" && (
          <div className="text-sm grid grid-cols-[100px_1fr]">
            <div className="py-1.5 text-muted-foreground border-b border-border">ARN</div>
            <div className="py-1.5 border-b border-border overflow-hidden break-all font-mono text-xs">{topic.arn}</div>
            <div className="py-1.5 text-muted-foreground border-b border-border">Subscriptions</div>
            <div className="py-1.5 border-b border-border">{subs.length}</div>
          </div>
        )}

        {activeTab === "subscriptions" && (
          <>
            {subs.length === 0 && (
              <div className="text-center text-muted-foreground text-sm py-8">
                No subscriptions from this topic
              </div>
            )}
            {subs.map((sub) => (
              <SubscriptionItem
                key={sub.subscriptionArn}
                sub={sub}
                nameFrom="queue"
              />
            ))}
          </>
        )}
      </div>
    </div>
  );
}
