import { useRef, useEffect } from "react";
import { ResourceCard } from "@/components/resource-card";
import type { BusState, QueueInfo, TopicInfo } from "@/types";
import { getQueueNameFromEndpoint } from "@/utils/resource-helpers";

interface GraphPanelProps {
  state: BusState;
  selected: QueueInfo | TopicInfo | null;
  selectQueue: (queue: QueueInfo) => void;
  selectTopic: (topic: TopicInfo) => void;
}

export function GraphPanel({
  state,
  selected,
  selectQueue,
  selectTopic,
}: GraphPanelProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const svgRef = useRef<SVGSVGElement>(null);

  useEffect(() => {
    const draw = () => {
      const container = containerRef.current;
      const svg = svgRef.current;
      if (!container || !svg) return;

      const containerRect = container.getBoundingClientRect();
      let svgContent = "";

      for (const sub of state.subscriptions) {
        const topicEl = container.querySelector(
          `[data-arn="${CSS.escape(sub.topicArn)}"]`
        );
        const queueName = getQueueNameFromEndpoint(sub.endpoint);
        const queue = state.queues.find((q) => q.name === queueName);
        if (!topicEl || !queue) continue;

        const queueEl = container.querySelector(
          `[data-arn="${CSS.escape(queue.arn)}"]`
        );
        if (!queueEl) continue;

        const topicRect = topicEl.getBoundingClientRect();
        const queueRect = queueEl.getBoundingClientRect();

        const x1 = topicRect.right - containerRect.left;
        const y1 = topicRect.top + topicRect.height / 2 - containerRect.top;
        const x2 = queueRect.left - containerRect.left;
        const y2 = queueRect.top + queueRect.height / 2 - containerRect.top;

        const midX = (x1 + x2) / 2;
        const pathD = `M ${x1} ${y1} C ${midX} ${y1}, ${midX} ${y2}, ${x2} ${y2}`;
        // Arrow points right at endpoint (tangent is horizontal at end of this curve)
        const arrow = `${x2 - 12},${y2 - 5} ${x2},${y2} ${x2 - 12},${y2 + 5}`;
        svgContent += `<g class="conn-sub"><path d="${pathD}" stroke="transparent" stroke-width="16" fill="none" class="conn-hit" /><path d="${pathD}" stroke="var(--color-sns)" stroke-width="1.5" fill="none" stroke-dasharray="8 6" opacity="0.4" class="conn-line" /><polygon points="${arrow}" fill="var(--color-sns)" opacity="0.6" class="conn-arrow" /></g>`;
      }

      for (const queue of state.queues) {
        if (!queue.hasDeadLetterQueue) continue;
        const dlq = state.queues.find(
          (q) => q.name === queue.deadLetterQueueName
        );
        if (!dlq) continue;

        const queueEl = container.querySelector(
          `[data-arn="${CSS.escape(queue.arn)}"]`
        );
        const dlqEl = container.querySelector(
          `[data-arn="${CSS.escape(dlq.arn)}"]`
        );
        if (!queueEl || !dlqEl) continue;

        const queueRect = queueEl.getBoundingClientRect();
        const dlqRect = dlqEl.getBoundingClientRect();

        const x1 =
          queueRect.left + queueRect.width / 2 - containerRect.left;
        const y1 = queueRect.bottom - containerRect.top;
        const x2 = dlqRect.left + dlqRect.width / 2 - containerRect.left;
        const y2 = dlqRect.top - containerRect.top;

        const midY = (y1 + y2) / 2;
        const pathD = `M ${x1} ${y1} C ${x1} ${midY}, ${x2} ${midY}, ${x2} ${y2}`;
        // Arrow points down at endpoint (tangent is vertical at end of this curve)
        const arrow = `${x2 - 5},${y2 - 12} ${x2},${y2} ${x2 + 5},${y2 - 12}`;
        svgContent += `<g class="conn-dlq"><path d="${pathD}" stroke="transparent" stroke-width="16" fill="none" class="conn-hit" /><path d="${pathD}" stroke="var(--color-dlq)" stroke-width="1.5" fill="none" stroke-dasharray="6 4" opacity="0.5" class="conn-line" /><polygon points="${arrow}" fill="var(--color-dlq)" opacity="0.6" class="conn-arrow" /></g>`;
      }

      svg.innerHTML = svgContent;
    };

    draw();

    const handler = () => draw();
    window.addEventListener("resize", handler);
    const ro = new ResizeObserver(handler);
    if (containerRef.current) ro.observe(containerRef.current);

    return () => {
      window.removeEventListener("resize", handler);
      ro.disconnect();
    };
  });

  const hasResources = state.topics.length > 0 || state.queues.length > 0;

  return (
    <div className="flex-1 p-6 overflow-auto relative">
      <div className="relative min-h-full" ref={containerRef}>
        <svg
          className="absolute inset-0 w-full h-full z-0"
          ref={svgRef}
        />

        {!hasResources && (
          <div className="flex items-center justify-center min-h-[300px] text-sm text-muted-foreground">
            No resources yet. Create queues and topics to see them here.
          </div>
        )}

        {hasResources && (
          <div className="flex gap-40 justify-center py-5 relative z-[1] pointer-events-none">
            {state.topics.length > 0 && (
              <div className="flex flex-col gap-4 min-w-[220px]">
                <div className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground px-1 pb-2">
                  SNS Topics
                </div>
                {state.topics.map((topic) => (
                  <ResourceCard
                    key={topic.arn}
                    type="topic"
                    resource={topic}
                    selected={selected?.arn === topic.arn}
                    state={state}
                    onClick={() => selectTopic(topic)}
                  />
                ))}
              </div>
            )}
            {state.queues.length > 0 && (
              <div className="flex flex-col gap-4 min-w-[220px]">
                <div className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground px-1 pb-2">
                  SQS Queues
                </div>
                {state.queues.map((queue) => (
                  <ResourceCard
                    key={queue.arn}
                    type="queue"
                    resource={queue}
                    selected={selected?.arn === queue.arn}
                    state={state}
                    onClick={() => selectQueue(queue)}
                  />
                ))}
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
