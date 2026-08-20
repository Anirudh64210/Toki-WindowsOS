import { ToggleSwitch } from "toki-design";

const ground: React.CSSProperties = {
  padding: 20, background: "#161618", borderRadius: 12,
  display: "flex", flexDirection: "column", gap: 14, width: 300,
  color: "var(--tk-text-hi)", fontFamily: "var(--tk-font)", fontSize: 12.5,
};

const Row = ({ label, on }: { label: string; on: boolean }) => (
  <span style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
    {label}
    <ToggleSwitch checked={on} label={label} />
  </span>
);

export const SettingsToggles = () => (
  <div style={ground}>
    <Row label="Glassy notch" on={true} />
    <Row label="Show Toki in screen recordings" on={false} />
  </div>
);
