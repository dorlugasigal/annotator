import rough from "roughjs/bundled/rough.cjs.js";
import { mkdirSync, writeFileSync } from "node:fs";
const generator = rough.generator();
const cases = [];
for (const roughness of [2, 6]) {
  const options = { seed: 42, roughness, curveFitting: 1, strokeWidth: 3 };
  cases.push({ shape: "rectangle", roughness, operations: generator.rectangle(20, 30, 300, 180, options).sets[0].ops });
  cases.push({ shape: "ellipse", roughness, operations: generator.ellipse(170, 120, 300, 180, options).sets[0].ops });
}
const folder = new URL("../../../apps/macos/Tests/RenderingTests/Fixtures/", import.meta.url);
mkdirSync(folder, { recursive: true });
writeFileSync(new URL("roughjs-4.6.6.json", folder), JSON.stringify(cases, null, 2) + "\n");
