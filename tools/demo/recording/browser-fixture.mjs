import { createServer } from "node:http";
import { createRequire } from "node:module";
import { writeFile, rename } from "node:fs/promises";
import { resolve } from "node:path";

const require = createRequire(new URL("../../../website/package.json", import.meta.url));
const { chromium } = require("@playwright/test");
const output = resolve(process.argv[2]);
const title = "Annotator controlled browser demo";
const pageHTML = `<!doctype html><html><head><meta charset="utf-8"><title>${title}</title>
<style>
*{box-sizing:border-box}body{margin:0;font:15px -apple-system,BlinkMacSystemFont,sans-serif;color:#243c37;background:#f1f0e8}
aside{position:fixed;inset:0 auto 0 0;width:200px;background:#213e36;color:#dce8df;padding:30px 24px}
aside strong{display:block;font-size:25px;margin-bottom:48px}aside p{padding:12px;border-radius:7px}aside p:first-of-type{background:#405b4e}
main{margin-left:200px;padding:32px 38px}header{display:flex;justify-content:space-between;align-items:center}h1{font-size:34px;letter-spacing:-1px;margin:16px 0}
.sample{font-size:11px;letter-spacing:1px;border:1px solid #bdc7b9;padding:9px;border-radius:20px}.intro{color:#60736b}
.content{display:grid;grid-template-columns:minmax(360px,1fr) 310px;gap:22px;margin-top:30px}
.project{background:#fffdf5;border:1px solid #d7ddd0;border-radius:15px;padding:25px}.project h2{font-size:22px;margin:0 0 10px}.tag{font-size:12px;background:#e4edbd;padding:6px 10px;border-radius:14px}
.bars{display:flex;align-items:end;gap:13px;height:100px;margin:28px 0 15px}.bars i{flex:1;background:#739f80;border-radius:5px 5px 0 0}.bars i:nth-child(2n){background:#d8bc74}
.row{display:flex;align-items:center;gap:12px;padding:17px 0;border-top:1px solid #e2e5da}.dot{width:27px;height:27px;border-radius:50%;background:#e5bd89}
.row:nth-child(2n) .dot{background:#abc9b1}.row small{margin-left:auto;color:#6a7e72}
.profile{background:#304b44;color:#f3f1df;border-radius:15px;padding:25px;box-shadow:0 14px 32px #233e3619}
.avatar{width:62px;height:62px;background:#dfa37a;color:#273e35;border:7px solid #d5bd83;border-radius:50%;display:grid;place-items:center;font-size:22px;font-weight:700;margin-bottom:20px}
.profile h2{margin:0 0 9px;font-size:25px}.profile p{color:#d4e0ce;line-height:1.5}.profile hr{border:0;border-top:1px solid #617367;margin:23px 0}
.activity{margin-top:22px;padding:20px;background:#e0e6d7;border-radius:12px;color:#405a4c}.activity b{display:block;margin-bottom:10px}
footer{font-size:12px;color:#718175;margin-top:28px}
</style></head><body><aside><strong>Fieldwork</strong><p>Overview</p><p>Projects</p><p>People</p><p>Activity</p></aside>
<main><header><div><div class="intro">Workspace / Product team</div><h1>Monday design review</h1></div><span class="sample">SAMPLE WORKSPACE</span></header>
<div class="content"><div class="project"><span class="tag">Ready for review</span><h2 style="margin-top:20px">Link service</h2><p class="intro">A small team. One clear next step.</p>
<div class="bars"><i style="height:35%"></i><i style="height:60%"></i><i style="height:43%"></i><i style="height:85%"></i><i style="height:69%"></i><i style="height:95%"></i><i style="height:80%"></i></div>
<div class="row"><span class="dot"></span><span>Review the request flow</span><small>Today</small></div>
<div class="row"><span class="dot"></span><span>Check the cache behavior</span><small>In review</small></div>
<div class="row"><span class="dot"></span><span>Share the next version</span><small>Next</small></div></div>
<div><section class="profile" id="blur-target"><div class="avatar">AL</div><h2>Avery Lee</h2><p>Design partner</p><hr><p>avery.lee@example.net<br>+1 (202) 555-0142</p><hr><p>Project notes<br>Review access: sample only</p></section>
<div class="activity"><b>Latest activity</b>A new draft is ready to discuss.</div></div></div>
<footer>Fictional names, contact details and project data. This page exists only for the recording.</footer></main></body></html>`;
const server = createServer((request, response) => {
  if (request.url !== "/") { response.writeHead(404); response.end(); return; }
  response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
  response.end(pageHTML);
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
const browserServer = await chromium.launchServer({
  headless: false,
  host: "127.0.0.1",
  args: ["--window-position=100,100", "--window-size=1200,850"],
});
const browser = await chromium.connect(browserServer.wsEndpoint());
const context = await browser.newContext({ viewport: null });
const page = await context.newPage();
await page.goto(`http://127.0.0.1:${server.address().port}/`);
await page.locator("#blur-target").waitFor();
await page.locator("h1").click();
const target = await page.locator("#blur-target").boundingBox();
const geometry = await page.evaluate(() => ({
  left: screenX, top: screenY, outerWidth, outerHeight, innerWidth, innerHeight
}));
const metadata = { title, pid: browserServer.process().pid, target, geometry };
await writeFile(output + ".tmp", JSON.stringify(metadata));
await rename(output + ".tmp", output);
let stopping = false;
async function stop() {
  if (stopping) return;
  stopping = true;
  await browserServer.close();
  await new Promise(resolve => server.close(resolve));
}
process.on("SIGTERM", () => { stop().then(() => process.exit(0), error => { console.error(error); process.exit(1); }); });
