import type { ReactNode } from "react";

// Regex to match JSON tokens on a single line of pretty-printed JSON.
// Matches in order: strings, numbers, booleans/null, structural chars.
const TOKEN_RE =
  /("(?:[^"\\]|\\.)*")\s*:|("(?:[^"\\]|\\.)*")|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|(\btrue\b|\bfalse\b|\bnull\b)|([{}[\],])/g;

function highlightLine(line: string, lineIdx: number): ReactNode {
  const parts: ReactNode[] = [];
  let lastIndex = 0;

  for (const match of line.matchAll(TOKEN_RE)) {
    const idx = match.index!;
    // Push any preceding whitespace/text as-is
    if (idx > lastIndex) {
      parts.push(line.slice(lastIndex, idx));
    }

    const [fullMatch, key, strVal, num, bool, punct] = match;

    if (key !== undefined) {
      // Key: "something":
      const colonIdx = fullMatch.lastIndexOf(":");
      parts.push(
        <span key={`${lineIdx}-${idx}`} className="text-sqs">
          {fullMatch.slice(0, colonIdx)}
        </span>,
        fullMatch.slice(colonIdx),
      );
    } else if (strVal !== undefined) {
      parts.push(
        <span key={`${lineIdx}-${idx}`} className="text-green-400">
          {strVal}
        </span>,
      );
    } else if (num !== undefined) {
      parts.push(
        <span key={`${lineIdx}-${idx}`} className="text-sns">
          {num}
        </span>,
      );
    } else if (bool !== undefined) {
      parts.push(
        <span key={`${lineIdx}-${idx}`} className="text-purple-400">
          {bool}
        </span>,
      );
    } else if (punct !== undefined) {
      parts.push(
        <span key={`${lineIdx}-${idx}`} className="text-muted-foreground">
          {punct}
        </span>,
      );
    }

    lastIndex = idx + fullMatch.length;
  }

  // Trailing text
  if (lastIndex < line.length) {
    parts.push(line.slice(lastIndex));
  }

  return parts.length > 0 ? parts : line;
}

interface JsonHighlightProps {
  json: string;
  className?: string;
}

export function JsonHighlight({ json, className }: JsonHighlightProps) {
  const lines = json.split("\n");

  return (
    <pre
      className={`font-mono text-xs text-foreground whitespace-pre-wrap break-all leading-relaxed ${className ?? ""}`}
    >
      {lines.map((line, i) => (
        <span key={i}>
          {highlightLine(line, i)}
          {i < lines.length - 1 ? "\n" : ""}
        </span>
      ))}
    </pre>
  );
}
