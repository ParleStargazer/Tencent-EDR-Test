import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { request } from "node:http";
import test from "node:test";
import { startProdServer } from "vinext/server/prod-server";

function get(port, path, host) {
  return new Promise((resolve, reject) => {
    const outgoing = request(
      {
        hostname: "127.0.0.1",
        port,
        path,
        method: "GET",
        headers: { Host: `${host}:${port}` },
      },
      (response) => {
        const chunks = [];
        response.on("data", (chunk) => chunks.push(chunk));
        response.on("end", () => resolve({
          status: response.statusCode,
          headers: response.headers,
          body: Buffer.concat(chunks).toString("utf8"),
        }));
      },
    );
    outgoing.on("error", reject);
    outgoing.end();
  });
}

test("Windows 生产服务器通过 127.0.0.1 和 localhost 提供 CSS/JS", async () => {
  const outDir = fileURLToPath(new URL("../dist", import.meta.url));
  const { server, port } = await startProdServer({ host: "127.0.0.1", port: 0, outDir });

  try {
    for (const host of ["127.0.0.1", "localhost"]) {
      const page = await get(port, "/", host);
      assert.equal(page.status, 200, `${host} 应返回首页`);

      const cssPath = page.body.match(/href="([^"]+\.css)"/)?.[1];
      const jsPath = page.body.match(/(?:href|src)="([^"]+live-control-plane[^"]+\.js)"/)?.[1];
      assert.ok(cssPath, `${host} 首页应引用 CSS`);
      assert.ok(jsPath, `${host} 首页应引用控制面 JS`);

      const css = await get(port, cssPath, host);
      assert.equal(css.status, 200, `${host} CSS 应可访问`);
      assert.match(css.headers["content-type"] ?? "", /^text\/css\b/i);
      assert.match(css.body, /\.app-shell/);

      const js = await get(port, jsPath, host);
      assert.equal(js.status, 200, `${host} JS 应可访问`);
      assert.match(js.headers["content-type"] ?? "", /^application\/javascript\b/i);
    }
  } finally {
    await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
});
