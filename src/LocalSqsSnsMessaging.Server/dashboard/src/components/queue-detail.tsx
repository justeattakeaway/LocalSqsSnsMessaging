import React, { useState, useEffect, useCallback } from "react";
import { MessageItem } from "@/components/message-item";
import { SubscriptionItem } from "@/components/subscription-item";
import type { BusState, QueueInfo, QueueMessages } from "@/types";
import { getQueueSubscriptions } from "@/utils/resource-helpers";
import { cn } from "@/lib/utils";

interface QueueDetailProps {
  state: BusState;
  queue: QueueInfo;
}

export function QueueDetail({ state, queue }: QueueDetailProps) {
  const [messages, setMessages] = useState<QueueMessages | null>(null);
  const [expandedMsg, setExpandedMsg] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState("info");

  const loadMessages = useCallback(async () => {
    try {
      const accountParam = state.currentAccount
        ? `?account=${encodeURIComponent(state.currentAccount)}`
        : "";
      const resp = await fetch(
        `/_ui/api/queues/${encodeURIComponent(queue.name)}/messages${accountParam}`
      );
      if (resp.ok) setMessages(await resp.json());
    } catch {
      setMessages({ queueName: queue.name, pendingMessages: [], inFlightMessages: [] });
    }
  }, [queue.name, state.currentAccount]);

  useEffect(() => {
    loadMessages();
  }, [loadMessages]);

  useEffect(() => {
    if (activeTab === "messages") loadMessages();
  }, [state, activeTab, loadMessages]);

  const toggleMsg = useCallback((id: string) => {
    setExpandedMsg((prev) => (prev === id ? null : id));
  }, []);

  const subs = getQueueSubscriptions(state, queue);
  const totalMessages = queue.messagesAvailable + queue.messagesInFlight;

  const tabs = [
    { id: "info", label: "Info" },
    { id: "messages", label: `Messages${totalMessages > 0 ? ` (${totalMessages})` : ""}` },
    { id: "subscriptions", label: "Subscriptions" },
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
            onClick={() => {
              setActiveTab(tab.id);
              if (tab.id === "messages") loadMessages();
            }}
          >
            {tab.label}
          </button>
        ))}
      </div>

      <div className="flex-1 overflow-y-auto overflow-x-hidden p-4 min-w-0">
        {activeTab === "info" && (
          <div className="text-sm grid grid-cols-[100px_1fr]">
            {([
              ["URL", queue.url, true],
              ["ARN", queue.arn, true],
              ["Type", queue.isFifo ? "FIFO" : "Standard", false],
              ["Messages", queue.messagesAvailable, false],
              ["In Flight", queue.messagesInFlight, false],
              ["Visibility", `${queue.visibilityTimeoutSeconds}s`, false],
              ...(queue.hasDeadLetterQueue ? [["DLQ", queue.deadLetterQueueName, false]] : []),
              ...(queue.maxReceiveCount ? [["Max Receives", queue.maxReceiveCount, false]] : []),
            ] as [string, string | number | null, boolean][]).map(([label, value, mono]) => (
              <React.Fragment key={label}>
                <div className="py-1.5 text-muted-foreground border-b border-border">{label}</div>
                <div className={cn("py-1.5 border-b border-border overflow-hidden break-all", mono && "font-mono text-xs")}>{value}</div>
              </React.Fragment>
            ))}
          </div>
        )}

        {activeTab === "messages" && (
          <>
            {messages === null && (
              <div className="text-center text-muted-foreground text-sm py-8">Loading...</div>
            )}
            {messages !== null &&
              messages.pendingMessages.length === 0 &&
              messages.inFlightMessages.length === 0 && (
                <div className="text-center text-muted-foreground text-sm py-8">
                  No messages in queue
                </div>
              )}
            {messages !== null &&
              (messages.pendingMessages.length > 0 ||
                messages.inFlightMessages.length > 0) && (
                <div>
                  {messages.pendingMessages.map((msg) => (
                    <MessageItem
                      key={msg.messageId}
                      msg={msg}
                      status="pending"
                      expanded={expandedMsg === msg.messageId}
                      onToggle={toggleMsg}
                    />
                  ))}
                  {messages.inFlightMessages.map((msg) => (
                    <MessageItem
                      key={msg.messageId}
                      msg={msg}
                      status="in-flight"
                      expanded={expandedMsg === msg.messageId}
                      onToggle={toggleMsg}
                    />
                  ))}
                </div>
              )}
          </>
        )}

        {activeTab === "subscriptions" && (
          <>
            {subs.length === 0 && (
              <div className="text-center text-muted-foreground text-sm py-8">
                No subscriptions to this queue
              </div>
            )}
            {subs.map((sub) => (
              <SubscriptionItem
                key={sub.subscriptionArn}
                sub={sub}
                nameFrom="topic"
              />
            ))}
          </>
        )}
      </div>
    </div>
  );
}
