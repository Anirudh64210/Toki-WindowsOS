import type { MouseEventHandler, ReactNode } from "react";

export interface IconButtonProps {
  /** The glyph - an inline SVG (16px) or a single character. */
  children?: ReactNode;
  /** Accessible name; required because the visual is a bare glyph. */
  label: string;
  /** Keeps the glyph red even under hover - the trash can. */
  danger?: boolean;
  onClick?: MouseEventHandler<HTMLButtonElement>;
  className?: string;
}

/**
 * Small icon button (gear, back, copy, close, trash): a floating glyph that lifts on a subtle
 * rounded highlight. 34x34 - the hit target IS the highlight box, not the glyph. Compresses to
 * 0.94 on press. Group several inside a rounded container (radius 14, hairline border, padding 3)
 * to make the redesign's toolbar cluster.
 */
export function IconButton({ children, label, danger, onClick, className }: IconButtonProps) {
  return (
    <button
      type="button"
      className={`tk-iconbtn${danger ? " tk-iconbtn--danger" : ""}${className ? ` ${className}` : ""}`}
      aria-label={label}
      title={label}
      onClick={onClick}
    >
      {children}
    </button>
  );
}
