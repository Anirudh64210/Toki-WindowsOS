export interface CircleCheckProps {
  checked: boolean;
  /** Called with the next value when clicked. */
  onChange?: (next: boolean) => void;
  /** Accessible name - the action item's text. */
  label?: string;
}

/**
 * The redesign's action-item tick: a hollow 18px ring at rest, a filled TEAL disc with a white
 * tick when done (Figma 329:1907). Pops on tick (scale spring). Pair with strikethrough on the
 * done item's text. Squares are not used for action items anywhere in Toki.
 */
export function CircleCheck({ checked, onChange, label }: CircleCheckProps) {
  return (
    <button
      type="button"
      role="checkbox"
      aria-checked={checked}
      aria-label={label}
      className="tk-circlecheck"
      onClick={() => onChange?.(!checked)}
    >
      <svg width="11" height="11" viewBox="0 0 12 11" aria-hidden="true">
        <path d="M1.5 6 L4.6 9 L10.5 2.6" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </button>
  );
}
