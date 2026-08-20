import type { CSSProperties, ReactNode } from "react";

export interface GlassPanelProps {
  /** Card content. */
  children?: ReactNode;
  /** True glass (rgba tint + backdrop blur, for over-wallpaper contexts); false = the opaque stand-in. */
  translucent?: boolean;
  /** Fixed width in px; omit for fluid. */
  width?: number;
  /** Inner padding in px (default 16). */
  padding?: number;
  style?: CSSProperties;
  className?: string;
}

/**
 * The hovering card - Toki's core surface (Figma 288:4): warm dark glass, 24px radius on ALL
 * corners, a 1px white-10% rim with a top-edge highlight, and a soft layered shadow. Everything
 * the notch shows lives on one of these. Never nest two GlassPanels; inner rows use the capsule
 * tokens instead (concentric radii: inner = outer - padding).
 */
export function GlassPanel({ children, translucent, width, padding = 16, style, className }: GlassPanelProps) {
  return (
    <div
      className={`tk-glass-panel${translucent ? " tk-glass-panel--translucent" : ""}${className ? ` ${className}` : ""}`}
      style={{ width, padding, ...style }}
    >
      {children}
    </div>
  );
}
