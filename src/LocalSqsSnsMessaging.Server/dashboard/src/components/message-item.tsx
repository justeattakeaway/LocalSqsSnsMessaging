import { useState, useMemo, useCallback, useRef } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { JsonHighlight } from "@/components/json-highlight";
import type { MessageInfo } from "@/types";
import { formatBody, isJson } from "@/utils/format";
import {
  detectMessageFormat,
  getPreviewText,
  type SnsEnvelope,
} from "@/utils/message-format";

interface MessageItemProps {
  msg: MessageInfo;
  status: "pending" | "in-flight";
  expanded: boolean;
  onToggle: (id: string) => void;
  onDelete?: (messageId: string) => Promise<void>;
}

function simplifyTopicArn(arn: string): string {
  const parts = arn.split(":");
  return parts[parts.length - 1];
}

function SnsStructuredView({ envelope }: { envelope: SnsEnvelope }) {
  const formattedInner = formatBody(envelope.innerMessage);
  const innerIsJson = isJson(envelope.innerMessage);

  return (
    <div className="space-y-2">
      <div className="text-[11px] grid grid-cols-[80px_1fr] gap-x-2">
        <span className="text-muted-foreground">Topic</span>
        <span className="font-mono text-sns">
          {simplifyTopicArn(envelope.topicArn)}
        </span>
        {envelope.timestamp && (
          <>
            <span className="text-muted-foreground">Timestamp</span>
            <span className="font-mono">{envelope.timestamp}</span>
          </>
        )}
        {envelope.subject && (
          <>
            <span className="text-muted-foreground">Subject</span>
            <span className="font-mono">{envelope.subject}</span>
          </>
        )}
      </div>

      <div>
        <div className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground mb-1">
          Message
        </div>
        <div className="rounded bg-card p-3 max-h-[250px] overflow-auto">
          {innerIsJson ? (
            <JsonHighlight json={formattedInner} />
          ) : (
            <pre className="font-mono text-xs text-foreground whitespace-pre-wrap break-all leading-relaxed">
              {envelope.innerMessage}
            </pre>
          )}
        </div>
      </div>

      {envelope.messageAttributes &&
        Object.keys(envelope.messageAttributes).length > 0 && (
          <div>
            <div className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground mb-1">
              SNS Message Attributes
            </div>
            {Object.entries(envelope.messageAttributes).map(([key, val]) => (
              <div key={key}>
                <dt className="text-[11px] text-muted-foreground">{key}</dt>
                <dd className="font-mono text-xs text-foreground mb-1">
                  {val.Value} <span className="text-muted-foreground">({val.Type})</span>
                </dd>
              </div>
            ))}
          </div>
        )}
    </div>
  );
}

function RawBodyView({ body }: { body: string }) {
  const formatted = formatBody(body);
  const bodyIsJson = isJson(body);

  return (
    <div className="rounded bg-card p-3 max-h-[300px] overflow-auto">
      {bodyIsJson ? (
        <JsonHighlight json={formatted} />
      ) : (
        <pre className="font-mono text-xs text-foreground whitespace-pre-wrap break-all leading-relaxed">
          {formatted}
        </pre>
      )}
    </div>
  );
}

export function MessageItem({
  msg,
  status,
  expanded,
  onToggle,
  onDelete,
}: MessageItemProps) {
  const [viewMode, setViewMode] = useState<"structured" | "raw">("structured");
  const [copied, setCopied] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const copiedTimer = useRef<ReturnType<typeof setTimeout>>();
  const format = useMemo(() => detectMessageFormat(msg.body), [msg.body]);
  const isSns = format.type === "sns-notification";
  const previewText = useMemo(() => getPreviewText(msg.body), [msg.body]);

  const handleCopy = useCallback((e: React.MouseEvent) => {
    e.stopPropagation();
    const text = isSns && viewMode === "structured"
      ? (format as SnsEnvelope).innerMessage
      : msg.body;
    navigator.clipboard.writeText(text);
    setCopied(true);
    clearTimeout(copiedTimer.current);
    copiedTimer.current = setTimeout(() => setCopied(false), 1500);
  }, [isSns, viewMode, format, msg.body]);

  const handleDelete = useCallback(async (e: React.MouseEvent) => {
    e.stopPropagation();
    if (!onDelete || deleting) return;
    setDeleting(true);
    await onDelete(msg.messageId);
  }, [onDelete, deleting, msg.messageId]);

  return (
    <div
      className={`rounded-md border bg-background p-3 mb-2 cursor-pointer transition-colors ${
        expanded ? "border-highlight" : "border-border hover:border-muted-foreground/30"
      }`}
      onClick={() => onToggle(msg.messageId)}
    >
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <span className="font-mono text-xs text-muted-foreground" title={msg.messageId}>
            {(msg.messageId || "").substring(0, 8)}...
          </span>
          {isSns && (
            <Badge
              variant="outline"
              className="border-sns/40 bg-sns/10 text-sns text-[10px] px-1.5 py-0"
            >
              SNS
            </Badge>
          )}
        </div>
        <Badge
          variant="outline"
          className={
            status === "pending"
              ? "border-sqs/40 bg-sqs/10 text-sqs text-[10px] px-1.5 py-0"
              : "border-sns/40 bg-sns/10 text-sns text-[10px] px-1.5 py-0"
          }
        >
          {status}
        </Badge>
      </div>

      {!expanded && (
        <div className="mt-1.5 truncate font-mono text-xs text-muted-foreground leading-relaxed">
          {previewText.substring(0, 120)}
          {previewText.length > 120 ? "..." : ""}
        </div>
      )}

      {expanded && (
        <div>
          <div className="relative mt-2">
            <div className="flex items-center justify-between mb-1.5">
              {isSns && (
                <div className="flex gap-1">
                  <button
                    type="button"
                    className={`text-[11px] px-2 py-0.5 rounded transition-colors ${
                      viewMode === "structured"
                        ? "bg-secondary text-foreground"
                        : "text-muted-foreground hover:text-foreground"
                    }`}
                    onClick={(e) => {
                      e.stopPropagation();
                      setViewMode("structured");
                    }}
                  >
                    Structured
                  </button>
                  <button
                    type="button"
                    className={`text-[11px] px-2 py-0.5 rounded transition-colors ${
                      viewMode === "raw"
                        ? "bg-secondary text-foreground"
                        : "text-muted-foreground hover:text-foreground"
                    }`}
                    onClick={(e) => {
                      e.stopPropagation();
                      setViewMode("raw");
                    }}
                  >
                    Raw
                  </button>
                </div>
              )}
              {!isSns && <div />}
              <div className="flex gap-1">
                <Button
                  variant="outline"
                  size="xs"
                  className="text-[11px]"
                  onClick={handleCopy}
                >
                  {copied ? "Copied!" : "Copy"}
                </Button>
                {onDelete && (
                  <Button
                    variant="outline"
                    size="xs"
                    className="text-[11px] text-destructive hover:bg-destructive hover:text-white"
                    onClick={handleDelete}
                    disabled={deleting}
                  >
                    {deleting ? "Deleting..." : "Delete"}
                  </Button>
                )}
              </div>
            </div>

            {isSns && viewMode === "structured" ? (
              <SnsStructuredView envelope={format as SnsEnvelope} />
            ) : (
              <RawBodyView body={msg.body} />
            )}
          </div>

          {msg.messageGroupId && (
            <div className="mt-2">
              <div className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground mb-1">
                Message Group
              </div>
              <div className="font-mono text-xs text-foreground">
                {msg.messageGroupId}
              </div>
            </div>
          )}

          {msg.messageAttributes &&
            Object.keys(msg.messageAttributes).length > 0 && (
              <div className="mt-2">
                <div className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground mb-1">
                  Message Attributes
                </div>
                {Object.entries(msg.messageAttributes).map(([key, val]) => (
                  <div key={key}>
                    <dt className="text-[11px] text-muted-foreground">{key}</dt>
                    <dd className="font-mono text-xs text-foreground mb-1">
                      {val}
                    </dd>
                  </div>
                ))}
              </div>
            )}

          {msg.attributes && Object.keys(msg.attributes).length > 0 && (
            <div className="mt-2">
              <div className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground mb-1">
                System Attributes
              </div>
              {Object.entries(msg.attributes).map(([key, val]) => (
                <div key={key}>
                  <dt className="text-[11px] text-muted-foreground">{key}</dt>
                  <dd className="font-mono text-xs text-foreground mb-1">
                    {val}
                  </dd>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
