// Serves the website folder for the redesign concepts, with byte ranges so videos play in Safari.
// Run: node website/concepts/serve.mjs [port]   then open http://127.0.0.1:4310/concepts/
import { createServer } from "node:http";
import { createReadStream, statSync } from "node:fs";
import { extname, join, normalize, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(fileURLToPath(new URL("..", import.meta.url)));
const port = Number(process.argv[2]) || 4310;
const types = {
  ".html": "text/html; charset=utf-8", ".css": "text/css; charset=utf-8", ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8", ".json": "application/json", ".svg": "image/svg+xml", ".png": "image/png",
  ".jpg": "image/jpeg", ".mp4": "video/mp4", ".woff2": "font/woff2", ".vtt": "text/vtt", ".glb": "model/gltf-binary",
};

createServer((request, response) => {
  let path = decodeURIComponent(new URL(request.url, "http://localhost").pathname);
  if (path === "/") { response.writeHead(302, { location: "/concepts/" }).end(); return; }
  let file = join(root, normalize(path));
  if (!file.startsWith(root)) { response.writeHead(403).end(); return; }
  let stat;
  try {
    stat = statSync(file);
    if (stat.isDirectory()) {
      if (!path.endsWith("/")) { response.writeHead(302, { location: `${path}/` }).end(); return; }
      file = join(file, "index.html");
      stat = statSync(file);
    }
  } catch { response.writeHead(404, { "content-type": "text/plain" }).end("Not found"); return; }
  const headers = { "content-type": types[extname(file)] || "application/octet-stream", "accept-ranges": "bytes", "cache-control": "no-store" };
  const range = /^bytes=(\d*)-(\d*)$/.exec(request.headers.range || "");
  if (range) {
    const start = range[1] ? Number(range[1]) : Math.max(0, stat.size - Number(range[2]));
    const end = range[1] && range[2] ? Math.min(Number(range[2]), stat.size - 1) : stat.size - 1;
    if (start > end || start >= stat.size) { response.writeHead(416, { "content-range": `bytes */${stat.size}` }).end(); return; }
    response.writeHead(206, { ...headers, "content-range": `bytes ${start}-${end}/${stat.size}`, "content-length": end - start + 1 });
    createReadStream(file, { start, end }).pipe(response);
    return;
  }
  response.writeHead(200, { ...headers, "content-length": stat.size });
  if (request.method === "HEAD") response.end(); else createReadStream(file).pipe(response);
}).listen(port, "127.0.0.1", () => console.log(`Concepts: http://127.0.0.1:${port}/concepts/`));
