import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  Table,
  TableHeader,
  TableBody,
  TableHead,
  TableRow,
  TableCell,
} from "@/components/ui/table";
import type { BusState } from "@/types";
import { formatTime, simplifyArn } from "@/utils/format";

interface ActivityViewProps {
  state: BusState;
}

export function ActivityView({ state }: ActivityViewProps) {
  const [filter, setFilter] = useState<"all" | "sqs" | "sns">("all");

  const ops = state.recentOperations || [];
  const filtered =
    filter === "all" ? ops : ops.filter((o) => o.service === filter);

  return (
    <div className="flex h-[calc(100vh-48px)]">
      <div className="flex-1 flex flex-col overflow-hidden">
        <div className="flex items-center justify-between px-5 py-3 border-b border-border">
          <div className="flex items-center gap-3">
            <span className="text-sm font-semibold text-foreground">
              API Activity
            </span>
            <span className="text-xs text-muted-foreground">
              {filtered.length} operations
            </span>
          </div>
          <div className="flex gap-1">
            {(["all", "sqs", "sns"] as const).map((f) => (
              <Button
                key={f}
                variant={filter === f ? "outline" : "ghost"}
                size="xs"
                className={
                  filter === f ? "border-highlight text-foreground" : ""
                }
                onClick={() => setFilter(f)}
              >
                {f === "all" ? "All" : f.toUpperCase()}
              </Button>
            ))}
          </div>
        </div>

        <div className="flex-1 overflow-auto">
          {filtered.length === 0 && (
            <div className="flex items-center justify-center h-[200px] text-sm text-muted-foreground">
              {ops.length === 0
                ? "No API operations recorded yet. Usage tracking is enabled."
                : "No operations match the current filter."}
            </div>
          )}

          {filtered.length > 0 && (
            <Table>
              <TableHeader className="sticky top-0 bg-card z-[1]">
                <TableRow>
                  <TableHead className="w-[90px] text-[11px] uppercase tracking-wide">
                    Time
                  </TableHead>
                  <TableHead className="w-[50px] text-[11px] uppercase tracking-wide">
                    Service
                  </TableHead>
                  <TableHead className="w-[180px] text-[11px] uppercase tracking-wide">
                    Action
                  </TableHead>
                  <TableHead className="text-[11px] uppercase tracking-wide">
                    Resource
                  </TableHead>
                  <TableHead className="w-[30px]" />
                </TableRow>
              </TableHeader>
              <TableBody>
                {filtered.map((op, idx) => (
                  <TableRow key={idx}>
                    <TableCell className="font-mono text-[11px] text-muted-foreground whitespace-nowrap">
                      {formatTime(op.timestamp)}
                    </TableCell>
                    <TableCell>
                      <Badge
                        variant="outline"
                        className={`text-[10px] font-bold tracking-wide uppercase px-1.5 py-0 rounded ${
                          op.service === "sqs"
                            ? "border-sqs/40 bg-sqs/10 text-sqs"
                            : "border-sns/40 bg-sns/10 text-sns"
                        }`}
                      >
                        {op.service.toUpperCase()}
                      </Badge>
                    </TableCell>
                    <TableCell className="text-foreground font-medium text-xs">
                      {op.action}
                    </TableCell>
                    <TableCell
                      className="font-mono text-[11px] text-muted-foreground max-w-[300px] truncate"
                      title={op.resourceArn ?? undefined}
                    >
                      {simplifyArn(op.resourceArn)}
                    </TableCell>
                    <TableCell>
                      <span
                        className={`text-[11px] font-semibold ${
                          op.success ? "text-success" : "text-dlq"
                        }`}
                      >
                        {op.success ? "\u2713" : "\u2717"}
                      </span>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </div>
      </div>
    </div>
  );
}
