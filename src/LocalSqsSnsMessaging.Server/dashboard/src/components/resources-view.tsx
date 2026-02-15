import { GraphPanel } from "@/components/graph-panel";
import { DetailPanel } from "@/components/detail-panel";
import type { BusState, QueueInfo, TopicInfo } from "@/types";

interface ResourcesViewProps {
  state: BusState;
  selected: QueueInfo | TopicInfo | null;
  selectedType: "queue" | "topic" | null;
  selectQueue: (queue: QueueInfo) => void;
  selectTopic: (topic: TopicInfo) => void;
}

export function ResourcesView({
  state,
  selected,
  selectedType,
  selectQueue,
  selectTopic,
}: ResourcesViewProps) {
  return (
    <div className="flex h-[calc(100vh-48px)]">
      <GraphPanel
        state={state}
        selected={selected}
        selectQueue={selectQueue}
        selectTopic={selectTopic}
      />
      <DetailPanel
        state={state}
        selected={selected}
        selectedType={selectedType}
      />
    </div>
  );
}
