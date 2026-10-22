import { createInterface } from "node:readline/promises";
import { Writable } from "node:stream";
import { hashPassword } from "../src/auth.ts";

if (!process.stdin.isTTY || !process.stderr.isTTY) {
  throw new Error("Run this in an interactive terminal. Do not put the passphrase in a command or file.");
}
let hidden = false;
const output = new Writable({
  write(chunk, _encoding, done) {
    if (!hidden) process.stderr.write(chunk);
    done();
  },
});
const input = createInterface({ input: process.stdin, output, terminal: true });
try {
  const answer = input.question("Admin passphrase (hidden, 6+ characters): ");
  hidden = true;
  const password = await answer;
  process.stdout.write(`${hashPassword(password)}\n`);
} finally {
  input.close();
  process.stderr.write("\n");
}
