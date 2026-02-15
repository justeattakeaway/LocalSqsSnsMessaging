import { Button } from "@/components/ui/button";
import type { BusState } from "@/types";
import { cn } from "@/lib/utils";

interface TopBarProps {
  view: "resources" | "activity";
  setView: (view: "resources" | "activity") => void;
  state: BusState;
  connected: boolean;
  currentAccount: string | null;
  onAccountChange: (account: string | null) => void;
}

export function TopBar({
  view,
  setView,
  state,
  connected,
  currentAccount,
  onAccountChange,
}: TopBarProps) {
  const accounts = state.accounts || [];
  const displayAccount = currentAccount || state.currentAccount || "";
  const opCount = (state.recentOperations || []).length;

  return (
    <div className="h-12 bg-card border-b border-border flex items-center justify-between px-5">
      <div className="flex items-center gap-5">
        <div className="text-sm font-semibold text-foreground">
          LocalSqsSnsMessaging{" "}
          <span className="font-normal text-muted-foreground">Dashboard</span>
        </div>

        <div className="flex gap-0.5 bg-background rounded-md p-0.5 border border-border">
          <Button
            variant="ghost"
            size="xs"
            className={cn(
              "rounded px-3.5 text-xs",
              view === "resources" &&
                "bg-card text-foreground shadow-sm"
            )}
            onClick={() => setView("resources")}
          >
            Resources
          </Button>
          <Button
            variant="ghost"
            size="xs"
            className={cn(
              "rounded px-3.5 text-xs",
              view === "activity" &&
                "bg-card text-foreground shadow-sm"
            )}
            onClick={() => setView("activity")}
          >
            Activity
            {opCount > 0 && (
              <span className="ml-0.5 opacity-60">({opCount})</span>
            )}
          </Button>
        </div>

        <div className="flex gap-4 text-xs text-muted-foreground">
          <span>
            <span className="font-semibold text-foreground">
              {state.topics.length}
            </span>{" "}
            topics
          </span>
          <span>
            <span className="font-semibold text-foreground">
              {state.queues.length}
            </span>{" "}
            queues
          </span>
          <span>
            <span className="font-semibold text-foreground">
              {state.subscriptions.length}
            </span>{" "}
            subs
          </span>
        </div>
      </div>

      <div className="flex items-center gap-4 text-sm">
        {accounts.length > 1 && (
          <select
            className="px-2 py-1 text-xs font-mono bg-background text-foreground border border-border rounded cursor-pointer outline-none hover:border-muted-foreground/30 focus:border-highlight appearance-none pr-6 bg-[url('data:image/svg+xml,%3Csvg%20xmlns%3D%22http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%22%20width%3D%2212%22%20height%3D%2212%22%20fill%3D%22%238b90a0%22%3E%3Cpath%20d%3D%22M3%205l3%203%203-3%22%2F%3E%3C%2Fsvg%3E')] bg-no-repeat bg-[right_6px_center]"
            value={displayAccount}
            onChange={(e) => onAccountChange(e.target.value || null)}
          >
            {accounts.map((acc) => (
              <option key={acc} value={acc}>
                {acc}
              </option>
            ))}
          </select>
        )}
        {accounts.length <= 1 && displayAccount && (
          <span className="text-xs font-mono text-muted-foreground">
            {displayAccount}
          </span>
        )}

        <div
          className={cn(
            "flex items-center gap-1.5 text-xs",
            connected ? "text-success" : "text-muted-foreground"
          )}
        >
          <span
            className={cn(
              "w-[7px] h-[7px] rounded-full",
              connected
                ? "bg-success shadow-[0_0_6px_rgba(52,211,153,0.5)]"
                : "bg-muted-foreground"
            )}
          />
          <span>{connected ? "Live" : "Connecting..."}</span>
        </div>
      </div>
    </div>
  );
}
