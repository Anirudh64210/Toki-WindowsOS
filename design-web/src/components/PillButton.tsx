import type { MouseEventHandler, ReactNode } from "react";

export interface PillButtonProps {
  /** Button label (text, or text + a small leading icon). */
  children?: ReactNode;
  /**
   * default = neutral chip fill · white = the primary CTA / Join pill · coral = the record CTA ·
   * stop = the red "Stop & summarize" pill · ghost = transparent until hovered (secondary/cancel) ·
   * glass = capsule fill on a glass card (the notch's twin home buttons)
   */
  variant?: "default" | "white" | "coral" | "stop" | "ghost" | "glass";
  /** Red text on the neutral shape - destructive actions that aren't the final confirm. */
  dangerText?: boolean;
  disabled?: boolean;
  onClick?: MouseEventHandler<HTMLButtonElement>;
  className?: string;
}

/**
 * The pill button - Toki's one text-button shape. The whole rounded box is the hit target and the
 * whole box compresses on press (scale 0.97, springs back) - the design system's haptic-feel rule.
 * Radius 13, min 32px tall. Bold red fill is reserved for "stop"; destructive elsewhere is red TEXT.
 */
export function PillButton({ children, variant = "default", dangerText, disabled, onClick, className }: PillButtonProps) {
  const v = variant === "default" ? "" : ` tk-btn--${variant}`;
  return (
    <button
      type="button"
      className={`tk-btn${v}${dangerText ? " tk-btn--danger-text" : ""}${className ? ` ${className}` : ""}`}
      disabled={disabled}
      onClick={onClick}
      style={disabled ? { opacity: 0.45, cursor: "default" } : undefined}
    >
      {children}
    </button>
  );
}
