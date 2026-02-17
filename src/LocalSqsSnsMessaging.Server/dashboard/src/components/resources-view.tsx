import { GraphPanel } from "@/components/graph-panel";
import { DetailPanel } from "@/components/detail-panel";
import type { BusState, QueueInfo, TopicInfo, SubscriptionInfo } from "@/types";

interface ResourcesViewProps {
  state: BusState;
  selected: QueueInfo | TopicInfo | null;
  selectedType: "queue" | "topic" | "subscription" | null;
  selectQueue: (queue: QueueInfo) => void;
  selectTopic: (topic: TopicInfo) => void;
  selectedSubscription: SubscriptionInfo | null;
  selectSubscription: (sub: SubscriptionInfo) => void;
}

export function ResourcesView({
  state,
  selected,
  selectedType,
  selectQueue,
  selectTopic,
  selectedSubscription,
  selectSubscription,
}: ResourcesViewProps) {
  return (
    <div className="flex h-[calc(100vh-48px)]">
      <GraphPanel
        state={state}
        selected={selected}
        selectQueue={selectQueue}
        selectTopic={selectTopic}
        selectedSubscription={selectedSubscription}
        selectSubscription={selectSubscription}
      />
      <DetailPanel
        state={state}
        selected={selected}
        selectedType={selectedType}
        selectedSubscription={selectedSubscription}
        selectSubscription={selectSubscription}
      />
    </div>
  );
}
