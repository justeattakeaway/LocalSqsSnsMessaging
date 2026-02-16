import { QueueDetail } from "@/components/queue-detail";
import { TopicDetail } from "@/components/topic-detail";
import type { BusState, QueueInfo, TopicInfo } from "@/types";

interface DetailPanelProps {
  state: BusState;
  selected: QueueInfo | TopicInfo | null;
  selectedType: "queue" | "topic" | null;
}

export function DetailPanel({ state, selected, selectedType }: DetailPanelProps) {
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
        <QueueDetail key={selected.arn} state={state} queue={selected as QueueInfo} />
      ) : (
        <TopicDetail key={selected.arn} state={state} topic={selected as TopicInfo} />
      )}
    </div>
  );
}
