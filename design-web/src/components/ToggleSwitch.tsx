export interface ToggleSwitchProps {
  checked: boolean;
  onChange?: (next: boolean) => void;
  /** Accessible name - what the setting does. */
  label?: string;
}

/**
 * Settings boolean: a 40x22 track that fills GREEN with the thumb sliding on the drawer curve.
 * Replaces any native checkbox for on/off settings (Toki never ships a bare OS checkbox).
 */
export function ToggleSwitch({ checked, onChange, label }: ToggleSwitchProps) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      className="tk-switch"
      onClick={() => onChange?.(!checked)}
    />
  );
}
