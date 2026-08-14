export interface SectionHeaderProps {
  /** The label, e.g. "DECISIONS" - rendered uppercase. */
  children: string;
  /**
   * One meaning, one colour, one icon (the redesign's mapping): decisions = green check ·
   * actions = teal checklist · questions = pink ? · transcript = amber speech · generic = quiet list.
   */
  kind?: "decisions" | "actions" | "questions" | "transcript" | "generic";
}

const KIND: Record<NonNullable<SectionHeaderProps["kind"]>, { color: string; d: string }> = {
  decisions: { color: "var(--tk-green)", d: "M2.5 6.2 L5 8.6 L9.5 3.4" },
  actions: { color: "var(--tk-teal)", d: "M2 3 H10 M2 6 H10 M2 9 H7" },
  questions: { color: "var(--tk-pink)", d: "M4 4.2 C4 2.8 5 2 6 2 C7.2 2 8 2.8 8 3.9 C8 5.4 6 5.4 6 7 M6 9.4 L6 9.5" },
  transcript: { color: "var(--tk-amber)", d: "M2.5 2.5 H9.5 V7.5 H5.5 L3.5 9.5 V7.5 H2.5 Z" },
  generic: { color: "var(--tk-text-mid)", d: "M2 3 H10 M2 6 H10 M2 9 H10" },
};

/**
 * Section header for meeting notes: a round tinted icon chip (18px, accent at 18% fill) + the
 * label in the SAME accent, 10px Bold, letter-spaced. The section's content below carries a thin
 * left rule in this colour (draw it on the content container, 2px, accent at 25%).
 */
export function SectionHeader({ children, kind = "generic" }: SectionHeaderProps) {
  const k = KIND[kind];
  return (
    <div className="tk-section" style={{ color: k.color }}>
      <span className="tk-section__chip" style={{ background: `color-mix(in srgb, ${k.color} 18%, transparent)` }}>
        <svg width="11" height="11" viewBox="0 0 12 12" aria-hidden="true">
          <path d={k.d} fill="none" stroke={k.color} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </span>
      <span>{children.toUpperCase()}</span>
    </div>
  );
}
