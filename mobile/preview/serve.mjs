// Serves the app's web export and forwards /api to an Atrium server, so
// the app and the API share one origin, as a phone app's requests need no
// cross-origin permission either. Only for looking at screens while
// developing; see playwright.config.ts.
/* global process, URL */
import { createReadStream, existsSync, statSync } from "node:fs";
import { createServer, request } from "node:http";
import { extname, join, normalize, resolve } from "node:path";

const [, , rootArg, portArg, apiArg] = process.argv;
const root = resolve(rootArg);
const api = new URL(apiArg);
const types = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript",
  ".json": "application/json",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".css": "text/css",
  ".ttf": "font/ttf",
};

createServer((incoming, outgoing) => {
  const url = new URL(incoming.url ?? "/", "http://localhost");
  if (url.pathname.startsWith("/api/")) {
    const forwarded = request(
      {
        host: api.hostname,
        port: api.port,
        path: incoming.url,
        method: incoming.method,
        headers: { ...incoming.headers, host: api.host },
      },
      (response) => {
        outgoing.writeHead(response.statusCode ?? 502, response.headers);
        response.pipe(outgoing);
      },
    );
    forwarded.on("error", () => outgoing.writeHead(502).end());
    incoming.pipe(forwarded);
    return;
  }

  // Any path that is not a file is a screen of the app: the app's page answers.
  const file = join(root, normalize(url.pathname));
  const found = file.startsWith(root) && existsSync(file) && statSync(file).isFile() ? file : join(root, "index.html");
  outgoing.writeHead(200, { "Content-Type": types[extname(found)] ?? "application/octet-stream" });
  createReadStream(found).pipe(outgoing);
}).listen(Number(portArg), "127.0.0.1");
