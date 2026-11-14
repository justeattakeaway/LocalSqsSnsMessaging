import { useState, useEffect } from "react";
import type { BusState } from "@/types";
import { scopeQuery } from "@/lib/utils";

const emptyState: BusState = {
  accounts: [],
  currentAccount: "",
  regions: [],
  currentRegion: "",
  queues: [],
  topics: [],
  subscriptions: [],
  moveTasks: [],
  recentOperations: [],
};

export function useSSE(account: string | null, region: string | null) {
  const [state, setState] = useState<BusState>(emptyState);
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    const params = scopeQuery(account, region);
    const es = new EventSource(`/_ui/api/state/stream${params}`);
    es.addEventListener("state", (event) => {
      setConnected(true);
      setState(JSON.parse(event.data));
    });
    es.onerror = () => setConnected(false);
    return () => es.close();
  }, [account, region]);

  return { state, connected };
}
