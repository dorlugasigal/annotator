import test from "node:test";
import assert from "node:assert/strict";
import {mkdtempSync, mkdirSync, writeFileSync, rmSync} from "node:fs";
import {tmpdir} from "node:os";
import {join} from "node:path";
import {normalizeMediaSuffixRange} from "../scripts/preview.mjs";

test("preview translates suffix ranges to exact end-of-file ranges", t => {
  const directory = mkdtempSync(join(tmpdir(), "annotator-media-range-"));
  t.after(() => rmSync(directory, {recursive: true, force: true}));
  mkdirSync(join(directory, "demo"));
  writeFileSync(join(directory, "demo/clip.mp4"), Buffer.alloc(100));
  for (const [range, expected] of [["bytes=-4", "bytes=96-99"], ["bytes=-200", "bytes=0-99"],
    ["bytes=-0", "bytes=100-"], ["bytes=0-1", "bytes=0-1"], ["bytes=10-", "bytes=10-"]]) {
    for (const base of ["/", "/annotator/"]) {
      const request = {method: "GET", url: `${base}demo/clip.mp4?v=encoded`, headers: {range}};
      normalizeMediaSuffixRange(request, directory, base);
      assert.equal(request.headers.range, expected);
    }
  }
});

test("preview leaves missing, invalid and out-of-base resources to normal HTTP handling", t => {
  const directory = mkdtempSync(join(tmpdir(), "annotator-media-range-"));
  t.after(() => rmSync(directory, {recursive: true, force: true}));
  for (const url of ["/outside.mp4", "/annotator/missing.mp4", "/annotator/%ZZ.mp4",
    "/annotator/%2e%2e%2foutside.mp4", "/annotator/image.jpg"]) {
    const request = {method: "GET", url, headers: {range: "bytes=-4"}};
    normalizeMediaSuffixRange(request, directory, "/annotator/");
    assert.equal(request.headers.range, "bytes=-4");
  }
});
