import { TranscriptLine, SectionHeader } from "toki-design";

const ground: React.CSSProperties = {
  padding: 20, background: "#161618", borderRadius: 12,
  display: "flex", flexDirection: "column", gap: 10, width: 380,
};

export const Turns = () => (
  <div style={ground}>
    <SectionHeader kind="transcript">Transcript</SectionHeader>
    <TranscriptLine speaker="Priya">So where are we on the onboarding diagnostic, shippable this week?</TranscriptLine>
    <TranscriptLine speaker="You" you>Tool's done. Twenty checks, results render instantly, no gate.</TranscriptLine>
    <TranscriptLine speaker="Priya">Love it. What's left?</TranscriptLine>
    <TranscriptLine speaker="Sid">Brand font and hex confirmed, and the Make webhook URL for email capture.</TranscriptLine>
  </div>
);
