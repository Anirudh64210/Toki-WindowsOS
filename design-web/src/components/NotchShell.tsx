import type { ReactNode } from "react";
import { GlassPanel } from "./GlassPanel";
import { TokiMascot, type TokiMascotProps } from "./TokiMascot";

export interface NotchShellProps {
  /** The card's content. */
  children?: ReactNode;
  /** Card width in px. */
  width?: number;
  /**
   * Where Toki floats, PER STATE (each Figma frame draws him differently):
   * header = level with a tall panel's title row · center = vertically centred on a compact pill ·
   * corner = overlapping the bottom-left corner of the expanded recording panel.
   */
  mascot?: "header" | "center" | "corner" | "none";
  /** Toki's mood while this state shows. */
  mood?: TokiMascotProps["mood"];
  /** True glass card (over a wallpaper) vs the opaque stand-in. */
  translucent?: boolean;
  /** Inner padding of the card (default 14). */
  padding?: number;
}

/**
 * The notch architecture (Figma 288:4): the panel HOVERS a little below the top of the screen,
 * and Toki floats BESIDE it in his bubble - outside the card, in a 56px left gutter. Recording
 * states hug the screen's top edge instead (2px, non-invasive); every other state hangs at 12px.
 * Use this to compose any notch state: prompt pills, the recording bar, home, notes.
 */
export function NotchShell({ children, width = 344, mascot = "header", mood = "idle", translucent, padding = 14 }: NotchShellProps) {
  const pos =
    mascot === "center" ? { top: "50%", transform: "translateY(-50%)" } :
    mascot === "corner" ? { bottom: -8, top: "auto" as const } :
    { top: 6 };
  return (
    <div className="tk-notch" style={{ minHeight: 64 }}>
      {mascot !== "none" && (
        <span className="tk-notch__mascot" style={pos}>
          <TokiMascot mood={mood} size={40} />
        </span>
      )}
      <div className="tk-notch__card">
        <GlassPanel width={width} translucent={translucent} padding={padding}>
          {children}
        </GlassPanel>
      </div>
    </div>
  );
}
