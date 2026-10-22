import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import hashFns from "../node_modules/netlify-cli/dist/utils/deploy/hash-fns.js";

test("standard source deployment preserves routes, runtime, rate limits and schedule without a cache", async t => {
  const service = fileURLToPath(new URL("../", import.meta.url));
  const directory = await mkdtemp(path.join(tmpdir(), "annotator-source-deploy-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const result = await hashFns({
    getPathInProject: name => path.join(directory, ".netlify", name),
  }, [path.join(service, "netlify/functions")], {
    concurrentHash: 2, manifestPath: null, skipFunctionsCache: true,
    rootDir: path.resolve(service, "../.."), tmpDir: directory, statusCb: () => {},
    functionsConfig: { "*": { nodeBundler: "esbuild",
      includedFiles: [path.join(service, "NETLIFY-LICENSE.txt")] } },
  });
  const routes = {
    "admin-login": "/api/admin/login", "admin-logout": "/api/admin/logout",
    "admin-summary": "/api/admin/summary", "collect-app": "/api/metrics/app",
    "collect-web": "/api/metrics/web",
  };
  assert.deepEqual(Object.keys(result.functions).sort(), [...Object.keys(routes), "aggregate-daily"].sort());
  for (const [name, route] of Object.entries(routes))
    assert(result.fnConfig[name].routes.some(item => item.pattern === route));
  for (const upload of Object.values(result.fnShaMap).flat()) {
    assert.equal(upload.invocationMode, "stream");
    assert.match(upload.runtime, /^nodejs\d+\.x$/);
    assert.equal(upload.buildData.runtimeAPIVersion, 2);
    assert(upload.buildData.bootstrapVersion);
  }
  for (const [name, limit] of Object.entries({
    "admin-login": 5, "admin-summary": 30, "collect-app": 30, "collect-web": 60,
  })) {
    const rule = result.fnConfig[name].traffic_rules.action;
    assert.equal(rule.type, "rate_limit");
    assert.equal(rule.config.rate_limit_config.window_limit, limit);
    assert.equal(rule.config.rate_limit_config.window_size, 60);
  }
  assert.deepEqual(result.functionSchedules, [{ name: "aggregate-daily", cron: "15 0 * * *" }]);
});
