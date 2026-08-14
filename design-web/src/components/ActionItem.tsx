import { Capsule } from "./Capsule";
import { CircleCheck } from "./CircleCheck";

export interface ActionItemProps {
  /** The task text - a real action ("Confirm brand font + hex"), never a bare name. */
  children: string;
  done?: boolean;
  /** Who owes it - rendered as a BLUE capsule below the text. */
  owner?: string;
  /** When it's due - rendered as an AMBER capsule below the text. */
  due?: string;
  onToggle?: (next: boolean) => void;
}

/**
 * An action item row (Figma 348:2933): circular teal check + the text, with owner/due as capsule
 * chips BELOW the text (indented past the check), and a real strikethrough + dim when done.
 */
export function ActionItem({ children, done, owner, due, onToggle }: ActionItemProps) {
  return (
    <div className="tk-action">
      <CircleCheck checked={!!done} onChange={onToggle} label={children} />
      <div style={{ minWidth: 0 }}>
        <div className={`tk-action__text${done ? " tk-action__text--done" : ""}`}>{children}</div>
        {(owner || due) && (
          <div className="tk-action__chips">
            {owner ? <Capsule tone="blue">{owner}</Capsule> : null}
            {due ? <Capsule tone="amber">{due}</Capsule> : null}
          </div>
        )}
      </div>
    </div>
  );
}
