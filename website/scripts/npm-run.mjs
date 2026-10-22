import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const cwd = fileURLToPath(new URL("..", import.meta.url));

/** Runs one package script of the website. On Windows npm is a .cmd file, which only a shell can start. */
export function npmRun(script, env) {
  const options = { cwd, env, stdio: "inherit" };
  if (process.platform === "win32") execFileSync(`npm run ${script}`, { ...options, shell: true });
  else execFileSync("npm", ["run", script], options);
}
