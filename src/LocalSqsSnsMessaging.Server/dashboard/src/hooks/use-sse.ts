import { useState, useEffect } from "react";
import type { BusState } from "@/types";

const emptyState: BusState = {
  accounts: [],
  currentAccount: "",
  queues: [],
  topics: [],
  subscriptions: [],
  recentOperations: [],
};

export function useSSE(account: string | null) {
  const [state, setState] = useState<BusState>(emptyState);
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    const params = account
      ? `?account=${encodeURIComponent(account)}`
      : "";
    const es = new EventSource(`/_ui/api/state/stream${params}`);
    es.addEventListener("state", (event) => {
      setConnected(true);
      setState(JSON.parse(event.data));
    });
    es.onerror = () => setConnected(false);
    return () => es.close();
  }, [account]);

  return { state, connected };
}
