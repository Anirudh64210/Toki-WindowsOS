export interface TokiMascotProps {
  /**
   * What Toki is DOING, shown on the character (the Figma Characters row):
   * idle = breathing + blinking · thinking = a dots bubble at his shoulder ·
   * writing = a pen scribbling at his side · happy = squinted ^^ eyes + sparkles.
   */
  mood?: "idle" | "thinking" | "writing" | "happy";
  /** Art height in px (default 40). The bubble adds ~14px around it. */
  size?: number;
  /** Wrap him in the round translucent bubble every notch frame draws (default true). */
  bubble?: boolean;
}

/**
 * Toki - the blue cloud mascot. He breathes (3.5s, <=2% amplitude), blinks (asymmetric, every few
 * seconds), and narrates the app's state via mood. Place him BESIDE a GlassPanel (see NotchShell),
 * never inside a card, and never twice on one surface. "Unnoticeable" is a bug; "in the way" is a
 * bug - in hero moments he reads at up to a third of the panel height.
 */
export function TokiMascot({ mood = "idle", size = 40, bubble = true }: TokiMascotProps) {
  const w = (size * 120) / 106;
  const art = (
    <svg className="tk-mascot__art" width={w} height={size} viewBox="0 0 120 106" aria-label={`Toki (${mood})`} role="img">
      <defs>
        {/* userSpaceOnUse so ONE gradient spans the whole fused cloud - per-lobe gradients gave
            each ellipse its own light direction and the seams read "weird" (owner, 2026-08-14).
            This matches the WPF original: a GeometryGroup filled once. */}
        <linearGradient id="tk-body" gradientUnits="userSpaceOnUse" x1="60" y1="4" x2="50" y2="104">
          <stop offset="0" stopColor="#EAF6FF" />
          <stop offset="0.4" stopColor="#A9DCFB" />
          <stop offset="0.72" stopColor="#6EBCF6" />
          <stop offset="1" stopColor="#3D9BEA" />
        </linearGradient>
        <radialGradient id="tk-sheen" cx="0.4" cy="0.3" r="0.7">
          <stop offset="0" stopColor="rgba(255,255,255,0.45)" />
          <stop offset="1" stopColor="rgba(255,255,255,0)" />
        </radialGradient>
      </defs>
      {/* ground glow (the cheap lift - never a filter on the animated art) */}
      <ellipse cx="60" cy="100" rx="48" ry="9" fill="rgba(62,134,214,0.30)" />
      {/* the cloud: four lobes fused into ONE smooth fill (shared userSpace gradient = no seams) */}
      <g fill="url(#tk-body)">
        <ellipse cx="60" cy="67" rx="58" ry="37" />
        <ellipse cx="30" cy="52" rx="25" ry="25" />
        <ellipse cx="61" cy="37" rx="33" ry="33" />
        <ellipse cx="91" cy="52" rx="25" ry="25" />
      </g>
      <ellipse cx="54" cy="28" rx="26" ry="15" fill="url(#tk-sheen)" />
      {/* blush */}
      <ellipse cx="24" cy="66" rx="8.5" ry="5.5" fill="#5FA8E8" opacity="0.35" />
      <ellipse cx="96" cy="66" rx="8.5" ry="5.5" fill="#5FA8E8" opacity="0.35" />
      {/* eyes: white pills on a darker seat; the whole pair blinks */}
      <g>
        <rect x="37.5" y="47" width="16" height="30" rx="8" fill="rgba(47,123,196,0.35)" />
        <rect x="66.5" y="47" width="16" height="30" rx="8" fill="rgba(47,123,196,0.35)" />
        {mood === "happy" ? (
          <g stroke="#FAFDFF" strokeWidth="5" strokeLinecap="round" fill="none">
            <path d="M38 62 Q45.5 54 53 62" />
            <path d="M67 62 Q74.5 54 82 62" />
          </g>
        ) : (
          <g>
            <rect className="tk-mascot__eye" x="37.5" y="44" width="16" height="30" rx="8" fill="#FAFDFF" style={{ transformBox: "fill-box" }} />
            <rect className="tk-mascot__eye" x="66.5" y="44" width="16" height="30" rx="8" fill="#FAFDFF" style={{ transformBox: "fill-box" }} />
          </g>
        )}
      </g>
      {/* mood overlays */}
      {mood === "thinking" && (
        <g>
          <rect x="86" y="70" width="34" height="18" rx="9" fill="rgba(255,255,255,0.95)" />
          <circle className="tk-mascot__dot" cx="96" cy="79" r="2.4" fill="#4A9BE8" />
          <circle className="tk-mascot__dot" cx="103" cy="79" r="2.4" fill="#4A9BE8" />
          <circle className="tk-mascot__dot" cx="110" cy="79" r="2.4" fill="#4A9BE8" />
        </g>
      )}
      {mood === "writing" && (
        <g className="tk-mascot__pen" style={{ transformBox: "fill-box" }}>
          <rect x="98" y="66" width="6" height="26" rx="2.5" fill="#2E6FB2" />
          <path d="M98 92 L101 100 L104 92 Z" fill="#1E4E80" />
        </g>
      )}
      {mood === "happy" && (
        <g fill="#F5C56B">
          <path className="tk-mascot__sparkle" d="M14 8 L16.5 14 L22 16 L16.5 18 L14 24 L11.5 18 L6 16 L11.5 14 Z" />
          <path className="tk-mascot__sparkle" d="M102 2 L104.5 8 L110 10 L104.5 12 L102 18 L99.5 12 L94 10 L99.5 8 Z" opacity="0.9" />
          <path className="tk-mascot__sparkle" d="M110 34 L111.6 38 L115 39.5 L111.6 41 L110 45 L108.4 41 L105 39.5 L108.4 38 Z" opacity="0.8" />
        </g>
      )}
    </svg>
  );
  if (!bubble) return <span className="tk-mascot">{art}</span>;
  const d = size + 16;
  return (
    <span className="tk-mascot">
      <span className="tk-mascot__bubble" style={{ width: d, height: d }}>{art}</span>
    </span>
  );
}
