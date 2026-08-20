import { MeetingCard } from "toki-design";

const ground: React.CSSProperties = {
  padding: 20, background: "#0E0E10", borderRadius: 12,
  display: "flex", flexDirection: "column", gap: 6, width: 380,
};

export const ListGroup = () => (
  <div style={ground}>
    <div style={{ fontFamily: "var(--tk-font)", fontSize: 9.5, fontWeight: 600, letterSpacing: "0.08em", color: "var(--tk-text-mid)", marginBottom: 4 }}>
      TODAY
    </div>
    <MeetingCard day={14} month="AUG" title="Design review - PixelFlow" meta="Meeting · 24 min · 2h ago"
                 snippet="Tool is code-complete; blockers are brand tokens and the Make webhook. Ship moved to Thursday."
                 openCount={2} peopleCount={3} accent="amber" selected />
    <MeetingCard day={14} month="AUG" title="Sprint planning" meta="Meeting · 48 min · 5h ago"
                 snippet="Cut scope to the diagnostic and the email capture; parked the referral loop."
                 openCount={1} peopleCount={5} accent="blue" />
  </div>
);
