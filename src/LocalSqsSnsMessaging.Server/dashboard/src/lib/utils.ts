import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";

/** The query string that scopes a dashboard API call to an account and a region. */
export function scopeQuery(account: string | null | undefined, region: string | null | undefined) {
  const params = new URLSearchParams();
  if (account) params.set("account", account);
  if (region) params.set("region", region);
  const query = params.toString();
  return query ? `?${query}` : "";
}

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
