import { CircleCheck } from "toki-design";

const ground: React.CSSProperties = {
  padding: 20, background: "#161618", borderRadius: 12,
  display: "flex", gap: 18, alignItems: "center",
  color: "var(--tk-text-hi)", fontFamily: "var(--tk-font)", fontSize: 12,
};

export const States = () => (
  <div style={ground}>
    <span style={{ display: "inline-flex", gap: 8, alignItems: "center" }}>
      <CircleCheck checked={false} label="Open item" /> open
    </span>
    <span style={{ display: "inline-flex", gap: 8, alignItems: "center" }}>
      <CircleCheck checked={true} label="Done item" /> done
    </span>
  </div>
);
