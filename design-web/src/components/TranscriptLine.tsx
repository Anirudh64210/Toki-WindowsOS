export interface TranscriptLineProps {
  /** Speaker's display name ("Priya", "You"). */
  speaker: string;
  /** What they said. */
  children: string;
  /** The local user's own lines render brighter than everyone else's. */
  you?: boolean;
}

const ACCENTS = ["var(--tk-pink)", "var(--tk-teal)", "var(--tk-blue)", "var(--tk-green)", "var(--tk-purple)", "var(--tk-amber)"];

// Stable per-name colour (djb2) - never a random hash, so a speaker keeps their colour.
function accentFor(name: string): string {
  let h = 5381;
  for (const c of name.toUpperCase()) h = ((h << 5) + h + c.charCodeAt(0)) | 0;
  return ACCENTS[Math.abs(h) % ACCENTS.length];
}

/**
 * One transcript turn (Figma 348:2933): a 16px initial-avatar circle in a colour DERIVED from the
 * name (never fetched), the name run kept beside it (two people can share an initial), then the
 * words. YOU reads at full text strength; everyone else a step dimmer.
 */
export function TranscriptLine({ speaker, children, you }: TranscriptLineProps) {
  const accent = you ? "var(--tk-text-hi)" : accentFor(speaker);
  const fg = you ? "var(--tk-text-hi)" : "var(--tk-text-mid)";
  const initial = speaker.trim() ? speaker.trim()[0].toUpperCase() : "?";
  return (
    <div className="tk-transcript">
      <span
        className="tk-transcript__avatar"
        title={speaker}
        style={{ color: accent, background: `color-mix(in srgb, ${accent} 20%, transparent)` }}
      >
        {initial}
      </span>
      <span className="tk-transcript__text" style={{ color: fg }}>
        <span className="tk-transcript__name">{speaker}:</span> {children}
      </span>
    </div>
  );
}
