import { ActionItem, SectionHeader } from "toki-design";

const ground: React.CSSProperties = {
  padding: 20, background: "#161618", borderRadius: 12,
  display: "flex", flexDirection: "column", gap: 12, width: 360,
};

export const OpenAndDone = () => (
  <div style={ground}>
    <SectionHeader kind="actions">Action items</SectionHeader>
    <ActionItem owner="Priya" due="Tomorrow">Confirm brand font and hex values.</ActionItem>
    <ActionItem owner="Sid" due="This week">Wire the Make webhook and route the /health slug.</ActionItem>
    <ActionItem done owner="Dev">Auto-expand failing sections in the results view.</ActionItem>
  </div>
);
