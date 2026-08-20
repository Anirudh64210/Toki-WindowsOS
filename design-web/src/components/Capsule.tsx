import type { ReactNode } from "react";

export interface CapsuleProps {
  /** Chip text ("2 open", "3 people", "Priya", "Tomorrow"). */
  children?: ReactNode;
  /**
   * One meaning, one colour: amber = counts/time ("2 open") · teal = actions · blue = people/owners ·
   * green = decisions/success · pink = questions · neutral = quiet facts ("3 people").
   */
  tone?: "amber" | "teal" | "blue" | "green" | "pink" | "neutral";
}

const TONES: Record<NonNullable<CapsuleProps["tone"]>, string> = {
  amber: "var(--tk-amber)",
  teal: "var(--tk-teal)",
  blue: "var(--tk-blue)",
  green: "var(--tk-green)",
  pink: "var(--tk-pink)",
  neutral: "var(--tk-text-mid)",
};

/**
 * Status capsule - the redesign's outlined chip: tinted fill (10%), tinted 1px rim (30%), accent
 * text at 9.5px SemiBold. Used in meta rows ("2 open" · "3 people"), under action items (owner /
 * due), and on Library cards. Radius 8.
 */
export function Capsule({ children, tone = "neutral" }: CapsuleProps) {
  const c = TONES[tone];
  return (
    <span
      className="tk-capsule"
      style={{
        color: c,
        borderColor: `color-mix(in srgb, ${c} 30%, transparent)`,
        background: `color-mix(in srgb, ${c} 10%, transparent)`,
      }}
    >
      {children}
    </span>
  );
}
