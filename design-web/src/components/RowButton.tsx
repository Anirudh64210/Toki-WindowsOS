import type { MouseEventHandler, ReactNode } from "react";

export interface RowButtonProps {
  /** The row's main line. */
  title: string;
  /** Smaller dim second line. */
  sub?: string;
  /** Optional leading element (a provider mark, an icon). */
  leading?: ReactNode;
  /** Optional trailing element BEFORE the chevron (a status capsule, a small button). */
  trailing?: ReactNode;
  /** Hide the chevron for rows that toggle in place. */
  chevron?: boolean;
  onClick?: MouseEventHandler<HTMLButtonElement>;
}

/**
 * A full-width row that IS a button - a settings line with a title, a sub-line and a chevron.
 * Quiet at rest, a rounded box on hover; the box you see is exactly the area you can click
 * (never a Border with click handlers - Toki's standing rule). Compresses slightly less than a
 * pill on press (0.985) - big things are heavy.
 */
export function RowButton({ title, sub, leading, trailing, chevron = true, onClick }: RowButtonProps) {
  return (
    <button type="button" className="tk-row" onClick={onClick}>
      {leading}
      <span style={{ minWidth: 0 }}>
        <span className="tk-row__title" style={{ display: "block" }}>{title}</span>
        {sub ? <span className="tk-row__sub" style={{ display: "block" }}>{sub}</span> : null}
      </span>
      <span style={{ marginLeft: "auto", display: "inline-flex", alignItems: "center", gap: 10 }}>
        {trailing}
        {chevron ? (
          <svg className="tk-row__chevron" width="12" height="12" viewBox="0 0 12 12" aria-hidden="true">
            <path d="M4.2 2.4 L8 6 L4.2 9.6" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        ) : null}
      </span>
    </button>
  );
}
