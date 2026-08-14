import { Capsule } from "toki-design";

const ground: React.CSSProperties = { padding: 20, background: "#161618", borderRadius: 12, display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" };

export const Tones = () => (
  <div style={ground}>
    <Capsule tone="amber">2 open</Capsule>
    <Capsule tone="neutral">3 people</Capsule>
    <Capsule tone="blue">Priya</Capsule>
    <Capsule tone="amber">Tomorrow</Capsule>
    <Capsule tone="teal">action</Capsule>
    <Capsule tone="green">decided</Capsule>
    <Capsule tone="pink">open question</Capsule>
  </div>
);
