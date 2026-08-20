import { TokiMascot } from "toki-design";

const ground: React.CSSProperties = {
  padding: 24, background: "linear-gradient(135deg, #7fa66a 0%, #c9c08f 45%, #e8b7a0 100%)",
  borderRadius: 12, display: "flex", gap: 22, alignItems: "center", flexWrap: "wrap",
};

const Cell = ({ label, children }: { label: string; children: React.ReactNode }) => (
  <span style={{ display: "inline-flex", flexDirection: "column", alignItems: "center", gap: 6 }}>
    {children}
    <span style={{ fontFamily: "var(--tk-font)", fontSize: 10, fontWeight: 600, color: "rgba(0,0,0,0.55)" }}>{label}</span>
  </span>
);

export const Moods = () => (
  <div style={ground}>
    <Cell label="idle"><TokiMascot mood="idle" size={44} /></Cell>
    <Cell label="thinking"><TokiMascot mood="thinking" size={44} /></Cell>
    <Cell label="writing"><TokiMascot mood="writing" size={44} /></Cell>
    <Cell label="happy"><TokiMascot mood="happy" size={44} /></Cell>
  </div>
);

export const NoBubble = () => (
  <div style={ground}>
    <TokiMascot mood="idle" size={64} bubble={false} />
  </div>
);
