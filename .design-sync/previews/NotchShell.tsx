import { NotchShell, PillButton, UpcomingList, GlanceRow, TranscriptLine } from "toki-design";

const wallpaper: React.CSSProperties = {
  padding: "16px 24px 40px", borderRadius: 12,
  background: "linear-gradient(160deg, #7fa66a 0%, #c9c08f 45%, #e8b7a0 100%)",
};

export const HomePanel = () => (
  <div style={wallpaper}>
    <NotchShell width={344} mascot="header" mood="idle">
      <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 12 }}>Welcome back, Sid!</div>
      <div style={{ display: "flex", gap: 8, marginBottom: 14 }}>
        <PillButton variant="glass">Start a meeting</PillButton>
        <PillButton variant="glass">Take Notes</PillButton>
      </div>
      <UpcomingList>
        <GlanceRow day={14} month="AUG" title="Design review - PixelFlow" meta="2:00 PM · 30 min"
                   countdown="in 12 min" joinable accent="teal" />
      </UpcomingList>
    </NotchShell>
  </div>
);

export const RecordingExpanded = () => (
  <div style={wallpaper}>
    <NotchShell width={344} mascot="corner" mood="writing" translucent>
      <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 10 }}>
        <span style={{ fontSize: 12, fontWeight: 600, fontVariantNumeric: "tabular-nums" }}>0:12</span>
        <span style={{ flex: 1, textAlign: "center", letterSpacing: 2, color: "var(--tk-text-mid)" }}>▁▃▅▂▆▃▁▅▂▄▁▃</span>
      </div>
      <div style={{ display: "flex", flexDirection: "column", gap: 8, marginBottom: 12 }}>
        <TranscriptLine speaker="Priya">Tool's done. Twenty checks, results render instantly, no gate.</TranscriptLine>
        <TranscriptLine speaker="You" you>Love it. What's left?</TranscriptLine>
      </div>
      <div style={{ display: "flex", justifyContent: "flex-end" }}>
        <PillButton variant="stop">Stop &amp; summarize</PillButton>
      </div>
    </NotchShell>
  </div>
);

export const NotesReadyPill = () => (
  <div style={wallpaper}>
    <NotchShell width={180} mascot="center" mood="happy" padding={12}>
      <div style={{ fontSize: 12.5, fontWeight: 600, textAlign: "center" }}>Notes ready ›</div>
    </NotchShell>
  </div>
);
