// Concatenate the source stylesheets into ONE compiled dist/toki.css - the design-sync converter
// needs a self-contained cssEntry (an @import-only stub ships broken relative imports).
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const parts = ["src/styles/tokens.css", "src/styles/components.css"].map((p) =>
  readFileSync(join(root, p), "utf8"),
);
mkdirSync(join(root, "dist"), { recursive: true });
writeFileSync(join(root, "dist", "toki.css"), parts.join("\n\n"));
console.log("dist/toki.css written");
