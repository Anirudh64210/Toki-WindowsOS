import type { ReactNode } from "react";
import { Capsule } from "./Capsule";

export interface MeetingCardProps {
  /** Day of month, shown OUTSIDE the card. */
  day: number;
  /** Three-letter month. */
  month: string;
  title: string;
  /** "Meeting · 24 min · 2 days ago" */
  meta?: string;
  /** Two-line summary teaser. */
  snippet?: string;
  /** Open action-item count → an amber "N open" capsule. */
  openCount?: number;
  /** Attendee count → a quiet "N people" capsule. */
  peopleCount?: number;
  /** Per-meeting accent bar colour. */
  accent?: "amber" | "teal" | "blue" | "green" | "pink" | "purple";
  /** Selection fills the INNER card only - the date column never highlights. */
  selected?: boolean;
  onOpen?: () => void;
}

const ACCENTS = {
  amber: "var(--tk-amber)", teal: "var(--tk-teal)", blue: "var(--tk-blue)",
  green: "var(--tk-green)", pink: "var(--tk-pink)", purple: "var(--tk-purple)",
} as const;

/**
 * A Library list card (Figma 348:2933): date OUTSIDE on the left (16 Black over 8 Bold), then the
 * card - 3px accent bar, title, meta, a two-line snippet, and status capsules ("2 open" amber,
 * "3 people" quiet). Selection paints the inner card only. Group cards under TODAY / YESTERDAY /
 * date headers in the list.
 */
export function MeetingCard({ day, month, title, meta, snippet, openCount, peopleCount, accent = "teal", selected, onOpen }: MeetingCardProps) {
  return (
    <div className={`tk-mcard${selected ? " tk-mcard--selected" : ""}`}>
      <div className="tk-mcard__date">
        <div className="tk-mcard__daynum">{day}</div>
        <div className="tk-mcard__mon">{month.toUpperCase()}</div>
      </div>
      <div className="tk-mcard__card" onClick={onOpen} role="button" tabIndex={0}
           onKeyDown={(e) => { if (e.key === "Enter") onOpen?.(); }}>
        <span className="tk-mcard__accent" style={{ background: ACCENTS[accent] }} />
        <span style={{ minWidth: 0 }}>
          <span className="tk-mcard__title" style={{ display: "block" }}>{title}</span>
          {meta ? <span className="tk-mcard__meta" style={{ display: "block" }}>{meta}</span> : null}
          {snippet ? <span className="tk-mcard__snippet">{snippet}</span> : null}
          {(openCount || peopleCount) ? (
            <span className="tk-mcard__chips">
              {openCount ? <Capsule tone="amber">{openCount} open</Capsule> : null}
              {peopleCount ? <Capsule tone="neutral">{peopleCount} people</Capsule> : null}
            </span>
          ) : null}
        </span>
      </div>
    </div>
  );
}
