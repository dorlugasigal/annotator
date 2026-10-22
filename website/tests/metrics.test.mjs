import test from "node:test";
import assert from "node:assert/strict";
import {metricsEndpoint, referrerCategory, routeCategory, start} from "../src/scripts/metrics.ts";

test("metrics accept only an HTTPS origin and coarse route/referrer categories", () => {
  assert.equal(metricsEndpoint("https://example.test"), "https://example.test/api/metrics/web");
  for (const url of ["http://example.test", "https://user:secret@example.test", "https://example.test/a",
    "https://example.test:444", "https://example.test/?q=1", "bad"]) assert.equal(metricsEndpoint(url), undefined);
  assert.equal(routeCategory("/annotator/downloads/", "/annotator/"), "downloads");
  assert.equal(routeCategory("/anything-private", "/"), "other");
  assert.equal(referrerCategory("https://www.google.com/search?q=private", "example.test"), "search");
  assert.equal(referrerCategory("https://github.com/owner/repo?private=1", "example.test"), "github");
  assert.equal(referrerCategory("https://example.test/docs/", "example.test"), "direct");
});

test("per-tab counts stay same-origin, honor browser privacy signals and exclude private strings", async () => {
  const keys = ["document", "navigator", "location", "sessionStorage", "fetch", "console"];
  const saved = new Map(keys.map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  const storage = new Map(), requests = [], listeners = [], notices = [];
  let optedOut = false, dnt = "0", configured = "", failedStorage = false;
  const mock = {
    document: {
      body: {get dataset() {return {metricsEndpoint:configured, siteBase:"/"};}},
      referrer:"https://www.google.com/search?q=private-screen",
      querySelectorAll: () => [{dataset:{downloadTrack:"preview"}, addEventListener: (_, callback) => listeners.push(callback)}],
    },
    navigator: {get globalPrivacyControl() {return optedOut;}, get doNotTrack() {return dnt;}},
    location: {origin:"https://example.test", hostname:"example.test", pathname:"/downloads/", search:"?email=private"},
    sessionStorage: {
      getItem: key => {if(failedStorage)throw new Error("Denied"); return storage.get(key) ?? null;},
      setItem: (key, value) => storage.set(key, value),
    },
    fetch: async (url, options) => {requests.push({url,...options}); return {ok:true};},
    console: {info: message => notices.push(message), error: message => notices.push(message)},
  };
  try {
    for (const key of keys) Object.defineProperty(globalThis,key,{configurable:true,writable:true,value:mock[key]});
    start();
    assert.equal(requests.length,0);
    configured = "https://different.test";
    start();
    assert.equal(requests.length,0);
    configured = "https://example.test";
    optedOut = true; start(); optedOut = false;
    dnt = "1"; start(); dnt = "0";
    assert.equal(requests.length,0);
    failedStorage = true; start(); failedStorage = false;
    assert.equal(requests.length,0);
    start(); start();
    assert.equal(requests.length,1);
    assert.equal(storage.size,1);
    listeners[0]();
    assert.equal(requests.length,2);
    const bodies = requests.map(request=>JSON.parse(request.body));
    assert.equal(bodies[0].event,"session");
    assert.equal(bodies[1].event,"download");
    assert.equal(bodies[0].session,bodies[1].session);
    assert.equal(bodies[0].route,"downloads");
    assert.equal(bodies[0].referrer,"search");
    assert.equal(requests[0].credentials,"omit");
    assert.ok(!JSON.stringify(bodies).includes("private"));
    assert.ok(notices.length >= 2);
  } finally {
    for (const key of keys) {
      const descriptor=saved.get(key);
      if(descriptor)Object.defineProperty(globalThis,key,descriptor);else delete globalThis[key];
    }
  }
});
