import { useState, useCallback, useRef } from "react";
import { SubscriptionItem } from "@/components/subscription-item";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import type { BusState, TopicInfo, SubscriptionInfo } from "@/types";
import { getTopicSubscriptions } from "@/utils/resource-helpers";
import { cn } from "@/lib/utils";

interface TopicDetailProps {
  state: BusState;
  topic: TopicInfo;
  selectSubscription: (sub: SubscriptionInfo) => void;
}

export function TopicDetail({ state, topic, selectSubscription }: TopicDetailProps) {
  const [activeTab, setActiveTab] = useState("info");
  const [message, setMessage] = useState("");
  const [subject, setSubject] = useState("");
  const [publishing, setPublishing] = useState(false);
  const [published, setPublished] = useState(false);
  const publishedTimer = useRef<ReturnType<typeof setTimeout>>();

  const subs = getTopicSubscriptions(state, topic);

  const handlePublish = useCallback(async () => {
    if (!message.trim() || publishing) return;
    setPublishing(true);
    try {
      const accountParam = state.currentAccount
        ? `?account=${encodeURIComponent(state.currentAccount)}`
        : "";
      const resp = await fetch(
        `/_ui/api/topics/${encodeURIComponent(topic.name)}/publish${accountParam}`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            message: message,
            subject: subject || undefined,
          }),
        }
      );
      if (resp.ok) {
        setPublished(true);
        setMessage("");
        setSubject("");
        clearTimeout(publishedTimer.current);
        publishedTimer.current = setTimeout(() => setPublished(false), 2000);
      }
    } finally {
      setPublishing(false);
    }
  }, [message, subject, publishing, state.currentAccount, topic.name]);

  const tabs = [
    { id: "info", label: "Info" },
    { id: "subscriptions", label: `Subscriptions (${subs.length})` },
    { id: "publish", label: "Publish" },
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
                onClick={() => selectSubscription(sub)}
              />
            ))}
          </>
        )}

        {activeTab === "publish" && (
          <div className="space-y-4 max-w-lg">
            <div className="space-y-1.5">
              <Label htmlFor="message" className="text-xs text-muted-foreground">
                Message body
              </Label>
              <Textarea
                id="message"
                value={message}
                onChange={(e) => setMessage(e.target.value)}
                placeholder='{"key": "value"}'
                rows={10}
                className="font-mono text-xs resize-y"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="subject" className="text-xs text-muted-foreground">
                Subject (optional)
              </Label>
              <Input
                id="subject"
                value={subject}
                onChange={(e) => setSubject(e.target.value)}
                placeholder="Optional message subject"
                className="text-xs"
              />
            </div>
            <Button
              size="sm"
              disabled={!message.trim() || publishing}
              onClick={handlePublish}
            >
              {publishing ? "Publishing..." : published ? "Published!" : "Publish"}
            </Button>
          </div>
        )}
      </div>
    </div>
  );
}
