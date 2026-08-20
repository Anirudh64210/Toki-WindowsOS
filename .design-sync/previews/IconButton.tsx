import { IconButton } from "toki-design";

const ground: React.CSSProperties = { padding: 20, background: "#161618", borderRadius: 12, display: "flex", gap: 12, alignItems: "center" };

const CopyIcon = () => (
  <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
    <rect x="4.5" y="4.5" width="8" height="8" rx="1.5" fill="none" stroke="currentColor" strokeWidth="1.3" />
    <path d="M9.5 4.5 V3 A1.5 1.5 0 0 0 8 1.5 H3 A1.5 1.5 0 0 0 1.5 3 V8 A1.5 1.5 0 0 0 3 9.5 H4.5" fill="none" stroke="currentColor" strokeWidth="1.3" />
  </svg>
);
const TrashIcon = () => (
  <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
    <path d="M2 3.5 H12 M5 3.5 V2.2 A0.7 0.7 0 0 1 5.7 1.5 H8.3 A0.7 0.7 0 0 1 9 2.2 V3.5 M3.2 3.5 L4 12 A1 1 0 0 0 5 12.8 H9 A1 1 0 0 0 10 12 L10.8 3.5" fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
  </svg>
);
const GearIcon = () => (
  <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
    <circle cx="7" cy="7" r="2.2" fill="none" stroke="currentColor" strokeWidth="1.3" />
    <path d="M7 1.5 V3 M7 11 V12.5 M1.5 7 H3 M11 7 H12.5 M3.1 3.1 L4.2 4.2 M9.8 9.8 L10.9 10.9 M10.9 3.1 L9.8 4.2 M4.2 9.8 L3.1 10.9" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
  </svg>
);

export const Singles = () => (
  <div style={ground}>
    <IconButton label="Settings"><GearIcon /></IconButton>
    <IconButton label="Copy"><CopyIcon /></IconButton>
    <IconButton label="Delete" danger><TrashIcon /></IconButton>
  </div>
);

/** The reading pane's toolbar cluster: one rounded group, red trash last. */
export const ToolbarCluster = () => (
  <div style={ground}>
    <div style={{
      display: "flex", gap: 2, padding: 3, borderRadius: 14,
      background: "var(--tk-surface-hi)", border: "1px solid var(--tk-hairline)",
    }}>
      <IconButton label="Copy this note"><CopyIcon /></IconButton>
      <IconButton label="Delete this meeting" danger><TrashIcon /></IconButton>
    </div>
  </div>
);
