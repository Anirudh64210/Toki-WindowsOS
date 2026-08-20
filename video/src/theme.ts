/**
 * The app's own tokens, so the film is not "in Toki's style" but literally in Toki's colours and on
 * Toki's timing curves. Ported by hand from Theme/ThemeManager.cs and Theme/Motion.cs - the same
 * hand-port the design-web mirror does, and it drifts the same way, so check it after a theme change.
 */

export const c = {
  ink: "#161314",          // behind everything
  glass: "#2B2828",        // GlassOpaque - the notch card
  rim: "rgba(255,255,255,0.10)",   // GlassBorder
  textHi: "rgba(255,255,255,0.90)",
  textMid: "rgba(255,255,255,0.65)",
  textLow: "rgba(255,255,255,0.45)",
  blue: "#01A4FF",         // Toki
  blueFoot: "#F0FAF9",
  amber: "#F5A05B",
  teal: "#31C7D3",
  coral: "#FF7A66",
  stop: "#FF453A",         // Apple system red (dark); light is #FF3B30
};

/**
 * Motion.cs, verbatim. ArriveStrong is the workhorse ease-out, Drawer is the Dynamic-Island settle
 * the notch morph rides, Depart is how things leave. Using these is the whole reason the film moves
 * like the product instead of like a slideshow.
 */
export const ease = {
  arrive: [0.23, 1.0, 0.32, 1.0] as const,
  depart: [0.3, 0.0, 0.8, 0.15] as const,
  drawer: [0.32, 0.72, 0.0, 1.0] as const,
  inOut: [0.77, 0.0, 0.175, 1.0] as const,
};

// Segoe UI Variable IS installed on this machine, so the film's type is the app's type. The fallbacks
// only matter if this is ever rendered somewhere else.
export const font =
  '"Segoe UI Variable Display", "Segoe UI Variable", "Segoe UI", Inter, system-ui, sans-serif';

export const FPS = 60;
