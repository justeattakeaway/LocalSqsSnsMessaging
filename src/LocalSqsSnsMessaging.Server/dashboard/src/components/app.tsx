import { useState, useMemo, useEffect, useCallback } from "react";
import { useSSE } from "@/hooks/use-sse";
import { TopBar } from "@/components/top-bar";
import { ResourcesView } from "@/components/resources-view";
import { ActivityView } from "@/components/activity-view";
import type { QueueInfo, TopicInfo } from "@/types";

export function App() {
  const [currentAccount, setCurrentAccount] = useState<string | null>(null);
  const { state, connected } = useSSE(currentAccount);
  const [view, setView] = useState<"resources" | "activity">("resources");
  const [selectedArn, setSelectedArn] = useState<string | null>(null);
  const [selectedType, setSelectedType] = useState<"queue" | "topic" | null>(null);

  const selected = useMemo(() => {
    if (!selectedArn) return null;
    if (selectedType === "queue")
      return state.queues.find((q) => q.arn === selectedArn) || null;
    if (selectedType === "topic")
      return state.topics.find((t) => t.arn === selectedArn) || null;
    return null;
  }, [selectedArn, selectedType, state]);

  useEffect(() => {
    if (selectedArn && !selected) {
      setSelectedArn(null);
      setSelectedType(null);
    }
  }, [selectedArn, selected]);

  const selectQueue = useCallback((queue: QueueInfo) => {
    setSelectedArn(queue.arn);
    setSelectedType("queue");
  }, []);

  const selectTopic = useCallback((topic: TopicInfo) => {
    setSelectedArn(topic.arn);
    setSelectedType("topic");
  }, []);

  const handleAccountChange = useCallback((account: string | null) => {
    setCurrentAccount(account);
    setSelectedArn(null);
    setSelectedType(null);
  }, []);

  return (
    <>
      <TopBar
        view={view}
        setView={setView}
        state={state}
        connected={connected}
        currentAccount={currentAccount}
        onAccountChange={handleAccountChange}
      />
      {view === "resources" && (
        <ResourcesView
          state={state}
          selected={selected}
          selectedType={selectedType}
          selectQueue={selectQueue}
          selectTopic={selectTopic}
        />
      )}
      {view === "activity" && <ActivityView state={state} />}
    </>
  );
}
