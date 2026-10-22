import { spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { createInterface } from "node:readline/promises";
import { Writable } from "node:stream";
import { fileURLToPath } from "node:url";
import { hashPassword } from "../src/auth.ts";

// One guided command for the owner: choose a dashboard passphrase, save its hash in Netlify,
// redeploy so the functions use it (Netlify applies new values only to new deploys), then drop the
// old generated passphrase from the private backup. The passphrase itself is never stored or printed.
// The ANNOTATOR_* overrides exist for tests.
const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(process.env.ANNOTATOR_REPO_ROOT ?? join(here, "../../.."));
const cli = process.env.ANNOTATOR_NETLIFY_CLI ?? join(here, "../node_modules/.bin/netlify");
const secrets = process.env.ANNOTATOR_RELEASE_SECRETS_DIR ?? join(homedir(), "Library/Application Support/Annotator Release");
const configPath = "build/local-publish-config.json";

function stop(message: string): never {
  process.stderr.write(`\nStopped: ${message}\n`);
  process.exit(1);
}

if (!process.stdin.isTTY || !process.stderr.isTTY) {
  stop("Run this in an interactive terminal. Do not put the passphrase in a command or file.");
}

let site: string;
let homepage: string;
try {
  const config = JSON.parse(readFileSync(join(root, configPath), "utf8"));
  site = String(config.netlify_site_id ?? "");
  homepage = String(config.homepage ?? "").replace(/\/$/, "");
} catch {
  stop(`Could not read ${configPath}. Run this from the Annotator repository with the publisher config in place.`);
}
if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(site) || !homepage.startsWith("https://")) {
  stop(`${configPath} needs the Netlify site ID and the website address.`);
}

// Check this first: the redeploy publishes the committed website and needs a clean checkout.
const status = spawnSync("git", ["-C", root, "status", "--porcelain"], { encoding: "utf8" });
if (status.status !== 0) stop("Could not check the Git checkout.");
if (status.stdout.trim()) stop("Commit or stash your changes first. The redeploy needs a clean checkout. Nothing was changed.");

let hidden = false;
const output = new Writable({
  write(chunk, _encoding, done) {
    if (!hidden) process.stderr.write(chunk);
    done();
  },
});
const input = createInterface({ input: process.stdin, output, terminal: true });
// Without a listener, readline only pauses on Ctrl-C. Nothing is saved until every answer is in.
input.on("SIGINT", () => stop("Cancelled. Nothing was changed."));

async function ask(prompt: string, secret: boolean): Promise<string> {
  const answer = input.question(prompt);
  hidden = secret;
  try {
    return await answer;
  } finally {
    hidden = false;
    if (secret) process.stderr.write("\n");
  }
}

function lastLine(text: string, value: string): string {
  const line = text.trim().split("\n").pop() ?? "";
  return line.split(value).join("…");
}

function setVariable(key: string, value: string): void {
  const result = spawnSync(cli, ["env:set", key, value, "--context", "production", "--secret", "--site", site],
    { cwd: join(here, ".."), encoding: "utf8" });
  if (result.status !== 0) stop(`Netlify could not save ${key}: ${lastLine(result.stderr || result.stdout || "", value)}`);
}

let signOut = false;
let hash: string;
try {
  process.stderr.write(`This sets a new passphrase for ${homepage}/admin/ and then redeploys the website.\n`);
  const passphrase = await ask("New admin passphrase (hidden, 6+ characters): ", true);
  try {
    hash = hashPassword(passphrase);
  } catch (error) {
    stop(`${error instanceof Error ? error.message : "The passphrase was not accepted."} Nothing was changed.`);
  }
  const repeated = await ask("Type it again: ", true);
  if (repeated !== passphrase) stop("The two passphrases differ. Nothing was changed.");
  signOut = /^y(es)?$/i.test((await ask("Also sign out browsers that are signed in now? [y/N] ", false)).trim());
} finally {
  input.close();
}

setVariable("ADMIN_PASSWORD_HASH", hash);
if (signOut) setVariable("ADMIN_SESSION_SECRET", randomBytes(48).toString("base64"));
process.stderr.write("Saved in Netlify. Redeploying so the dashboard uses it…\n\n");

const deploy = spawnSync("python3", ["tools/release/publish_website.py", "--config", configPath, "--execute"],
  { cwd: root, stdio: "inherit" });
if (deploy.status !== 0) {
  stop("The new passphrase is saved in Netlify, but the redeploy failed, so the old one still works. "
    + `To finish, run: python3 tools/release/publish_website.py --config ${configPath} --execute`);
}

// The old generated passphrase no longer works, so it should not stay in the private backup.
const backup = join(secrets, `metrics-${site}.json`);
let cleaned = false;
if (existsSync(backup)) {
  const data = JSON.parse(readFileSync(backup, "utf8"));
  if (Object.hasOwn(data, "adminPassword")) {
    delete data.adminPassword;
    data.adminPasswordChangedAt = new Date().toISOString();
    writeFileSync(`${backup}.tmp`, `${JSON.stringify(data, null, 2)}\n`, { mode: 0o600 });
    renameSync(`${backup}.tmp`, backup);
    cleaned = true;
  }
}
process.stderr.write(`\nDone. Sign in at ${homepage}/admin/ with your new passphrase.\n`);
if (cleaned) process.stderr.write(`Removed the old generated passphrase from ${backup}.\n`);
