import { useState, useMemo, useCallback, useRef } from "react";
import { Badge } from "@/components/ui/badge";
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
}

function simplifyTopicArn(arn: string): string {
  const parts = arn.split(":");
  return parts[parts.length - 1];
}

const WELL_KNOWN_ATTRS = new Set([
  "ApproximateReceiveCount",
  "SentTimestamp",
  "ApproximateFirstReceiveTimestamp",
]);

function formatTimestamp(ts: string): string {
  const ms = parseInt(ts, 10);
  if (isNaN(ms)) return ts;
  return new Date(ms).toLocaleString();
}

function CopyIcon({ className }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 16 16"
      fill="currentColor"
      className={className}
    >
      <path d="M10.5 1h-7A1.5 1.5 0 0 0 2 2.5v9a.5.5 0 0 0 1 0v-9a.5.5 0 0 1 .5-.5h7a.5.5 0 0 0 0-1z" />
      <path d="M12.5 3h-7A1.5 1.5 0 0 0 4 4.5v9A1.5 1.5 0 0 0 5.5 15h7a1.5 1.5 0 0 0 1.5-1.5v-9A1.5 1.5 0 0 0 12.5 3zm.5 10.5a.5.5 0 0 1-.5.5h-7a.5.5 0 0 1-.5-.5v-9a.5.5 0 0 1 .5-.5h7a.5.5 0 0 1 .5.5z" />
    </svg>
  );
}

function CheckIcon({ className }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 16 16"
      fill="currentColor"
      className={className}
    >
      <path d="M13.78 4.22a.75.75 0 0 1 0 1.06l-7.25 7.25a.75.75 0 0 1-1.06 0L2.22 9.28a.75.75 0 0 1 1.06-1.06L6 10.94l6.72-6.72a.75.75 0 0 1 1.06 0z" />
    </svg>
  );
}

function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);
  const copiedTimer = useRef<ReturnType<typeof setTimeout>>();

  const handleCopy = useCallback(
    (e: React.MouseEvent) => {
      e.stopPropagation();
      navigator.clipboard.writeText(text);
      setCopied(true);
      clearTimeout(copiedTimer.current);
      copiedTimer.current = setTimeout(() => setCopied(false), 1500);
    },
    [text]
  );

  return (
    <button
      type="button"
      className="absolute top-2 right-2 p-1 rounded text-muted-foreground hover:text-foreground transition-colors"
      onClick={handleCopy}
      title="Copy to clipboard"
    >
      {copied ? (
        <CheckIcon className="size-3.5 text-success" />
      ) : (
        <CopyIcon className="size-3.5" />
      )}
    </button>
  );
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
        <div className="relative rounded bg-card p-3 pr-8 max-h-[250px] overflow-auto">
          <CopyButton text={envelope.innerMessage} />
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
    <div className="relative rounded bg-card p-3 pr-8 max-h-[300px] overflow-auto">
      <CopyButton text={body} />
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
}: MessageItemProps) {
  const [viewMode, setViewMode] = useState<"structured" | "raw">("structured");
  const format = useMemo(() => detectMessageFormat(msg.body), [msg.body]);
  const isSns = format.type === "sns-notification";
  const previewText = useMemo(() => getPreviewText(msg.body), [msg.body]);

  return (
    <div
      className={`rounded-md border bg-background mb-2 transition-colors ${
        expanded ? "border-highlight" : "border-border"
      }`}
    >
      <div
        className="flex items-center justify-between p-3 cursor-pointer hover:bg-surface-hover transition-colors rounded-t-md"
        onClick={() => onToggle(msg.messageId)}
      >
        <div className="flex items-center gap-2 min-w-0">
          <span className="font-mono text-xs text-muted-foreground shrink-0">
            {msg.messageId}
          </span>
          {isSns && (
            <Badge
              variant="outline"
              className="border-sns/40 bg-sns/10 text-sns text-[10px] px-1.5 py-0 shrink-0"
            >
              SNS
            </Badge>
          )}
        </div>
        <Badge
          variant="outline"
          className={`shrink-0 ml-2 ${
            status === "pending"
              ? "border-sqs/40 bg-sqs/10 text-sqs text-[10px] px-1.5 py-0"
              : "border-sns/40 bg-sns/10 text-sns text-[10px] px-1.5 py-0"
          }`}
        >
          {status}
        </Badge>
      </div>

      {!expanded && (
        <div
          className="px-3 pb-3 truncate font-mono text-xs text-muted-foreground leading-relaxed cursor-pointer"
          onClick={() => onToggle(msg.messageId)}
        >
          {previewText.substring(0, 120)}
          {previewText.length > 120 ? "..." : ""}
        </div>
      )}

      {expanded && (
        <div className="px-3 pb-3">
          {(msg.attributes?.ApproximateReceiveCount ||
            msg.attributes?.SentTimestamp ||
            msg.attributes?.ApproximateFirstReceiveTimestamp ||
            msg.messageGroupId) && (
            <div className="flex flex-wrap gap-x-4 gap-y-0.5 text-[11px] text-muted-foreground mb-2">
              {msg.attributes?.ApproximateReceiveCount && (
                <span>
                  Receives:{" "}
                  <span className="text-foreground font-mono">
                    {msg.attributes.ApproximateReceiveCount}
                  </span>
                </span>
              )}
              {msg.attributes?.SentTimestamp && (
                <span>
                  Sent:{" "}
                  <span className="text-foreground font-mono">
                    {formatTimestamp(msg.attributes.SentTimestamp)}
                  </span>
                </span>
              )}
              {msg.attributes?.ApproximateFirstReceiveTimestamp && (
                <span>
                  First received:{" "}
                  <span className="text-foreground font-mono">
                    {formatTimestamp(msg.attributes.ApproximateFirstReceiveTimestamp)}
                  </span>
                </span>
              )}
              {msg.messageGroupId && (
                <span>
                  Group:{" "}
                  <span className="text-foreground font-mono">
                    {msg.messageGroupId}
                  </span>
                </span>
              )}
            </div>
          )}

          <div className="relative mt-1">
            {isSns && (
              <div className="flex gap-1 mb-1.5">
                <button
                  type="button"
                  className={`text-[11px] px-2 py-0.5 rounded transition-colors ${
                    viewMode === "structured"
                      ? "bg-secondary text-foreground"
                      : "text-muted-foreground hover:text-foreground"
                  }`}
                  onClick={() => setViewMode("structured")}
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
                  onClick={() => setViewMode("raw")}
                >
                  Raw
                </button>
              </div>
            )}

            {isSns && viewMode === "structured" ? (
              <SnsStructuredView envelope={format as SnsEnvelope} />
            ) : (
              <RawBodyView body={msg.body} />
            )}
          </div>

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

          {msg.attributes &&
            Object.entries(msg.attributes).filter(([k]) => !WELL_KNOWN_ATTRS.has(k)).length > 0 && (
            <div className="mt-2">
              <div className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground mb-1">
                System Attributes
              </div>
              {Object.entries(msg.attributes)
                .filter(([k]) => !WELL_KNOWN_ATTRS.has(k))
                .map(([key, val]) => (
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
