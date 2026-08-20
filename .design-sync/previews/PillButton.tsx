import { PillButton } from "toki-design";

const ground: React.CSSProperties = {
  padding: 20, background: "#0E0E10", borderRadius: 12,
  display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap",
};

export const Variants = () => (
  <div style={ground}>
    <PillButton>Later</PillButton>
    <PillButton variant="white">Save</PillButton>
    <PillButton variant="coral">Record this meeting</PillButton>
    <PillButton variant="ghost">Cancel</PillButton>
    <PillButton variant="glass">Start a meeting</PillButton>
  </div>
);

export const StopAndDanger = () => (
  <div style={ground}>
    <PillButton variant="stop">Stop &amp; summarize</PillButton>
    <PillButton dangerText>Delete</PillButton>
    <PillButton disabled>Unavailable</PillButton>
  </div>
);
