import type { ReactNode } from "react";

export interface GlanceRowProps {
  /** Day of month, e.g. 14. */
  day: number;
  /** Three-letter month, e.g. "AUG". */
  month: string;
  /** Meeting title. */
  title: string;
  /** Time line, e.g. "2:00 PM · 30 min". */
  meta?: string;
  /** Amber countdown when it's soon ("in 12 min"). */
  countdown?: string;
  /** Show the white Join pill (the meeting has a link). */
  joinable?: boolean;
  /** Per-meeting accent for the 3px bar (defaults to teal). */
  accent?: "amber" | "teal" | "blue" | "green" | "pink" | "purple";
  onJoin?: () => void;
}

const ACCENTS = {
  amber: "var(--tk-amber)", teal: "var(--tk-teal)", blue: "var(--tk-blue)",
  green: "var(--tk-green)", pink: "var(--tk-pink)", purple: "var(--tk-purple)",
} as const;

/**
 * A COMING UP row (Figma Home 293:1830): the date column OUTSIDE the capsule (16 Black over
 * 8 Bold), then a glass capsule with a 3px accent bar, title + time, an amber countdown when the
 * meeting is close, and the white Join pill only when there is actually a link to open.
 */
export function GlanceRow({ day, month, title, meta, countdown, joinable, accent = "teal", onJoin }: GlanceRowProps) {
  return (
    <div className="tk-glance">
      <div className="tk-glance__date">
        <div className="tk-glance__daynum">{day}</div>
        <div className="tk-glance__mon">{month.toUpperCase()}</div>
      </div>
      <div className="tk-glance__card">
        <span className="tk-glance__accent" style={{ background: ACCENTS[accent] }} />
        <span style={{ minWidth: 0 }}>
          <span className="tk-glance__title" style={{ display: "block" }}>{title}</span>
          {meta ? <span className="tk-glance__meta" style={{ display: "block" }}>{meta}</span> : null}
        </span>
        {countdown ? <span className="tk-glance__count">{countdown}</span> : <span style={{ marginLeft: "auto" }} />}
        {joinable ? (
          <button type="button" className="tk-glance__join" onClick={onJoin}>Join</button>
        ) : null}
      </div>
    </div>
  );
}

export interface UpcomingListProps {
  children?: ReactNode;
}

/** Stacks GlanceRows with the list's 8px rhythm under a "COMING UP" overline. */
export function UpcomingList({ children }: UpcomingListProps) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      <div style={{ fontFamily: "var(--tk-font)", fontSize: 9.5, fontWeight: 600, letterSpacing: "0.08em", color: "var(--tk-text-mid)" }}>
        COMING UP
      </div>
      {children}
    </div>
  );
}
