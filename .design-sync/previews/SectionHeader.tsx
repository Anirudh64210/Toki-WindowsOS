import { SectionHeader } from "toki-design";

const ground: React.CSSProperties = {
  padding: 20, background: "#161618", borderRadius: 12,
  display: "flex", flexDirection: "column", gap: 14,
};

export const AllKinds = () => (
  <div style={ground}>
    <SectionHeader kind="decisions">Decisions</SectionHeader>
    <SectionHeader kind="actions">Action items</SectionHeader>
    <SectionHeader kind="questions">Open questions</SectionHeader>
    <SectionHeader kind="transcript">Transcript</SectionHeader>
    <SectionHeader kind="generic">Summary</SectionHeader>
  </div>
);
