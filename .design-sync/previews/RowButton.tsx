import { RowButton, Capsule } from "toki-design";

const ground: React.CSSProperties = { padding: 16, background: "#161618", borderRadius: 12, width: 340 };

const GoogleMark = () => (
  <span style={{
    width: 26, height: 26, borderRadius: 13, background: "var(--tk-surface-hi)",
    display: "inline-flex", alignItems: "center", justifyContent: "center",
    fontSize: 12, fontWeight: 700, color: "var(--tk-blue)", flex: "none",
  }}>G</span>
);

export const SettingsRows = () => (
  <div style={ground}>
    <RowButton title="Google Calendar" sub="studiomaydit@gmail.com" leading={<GoogleMark />}
               trailing={<Capsule tone="green">connected</Capsule>} />
    <RowButton title="Built with" sub="Open source and licences" />
    <RowButton title="Re-clean saved transcripts" sub="Apply name fixes to older meetings" chevron={false} />
  </div>
);
