import { readFileSync } from "node:fs";
import bundled from "../data/distribution.json";

// Build-time fixture override for validating the published-download layout.
const data: { stable: unknown; preview: unknown } = process.env.DISTRIBUTION_METADATA_FILE
  ? JSON.parse(readFileSync(process.env.DISTRIBUTION_METADATA_FILE, "utf8"))
  : bundled;
if (!data || !Object.hasOwn(data, "stable") || !Object.hasOwn(data, "preview")) {
  throw new Error("Distribution metadata must explicitly define both tracks.");
}

export interface DiskImage {
  url: string;
  sha256: string;
  size: number;
}

/** A release's app: Apple Silicon only, or universal (Apple Silicon and Intel). */
export type Architecture = "arm64" | "arm64 x86_64";
const archiveSuffix: Record<Architecture, string> = { "arm64": "arm64", "arm64 x86_64": "universal" };

export interface Download {
  releaseVersion: string;
  build: string;
  downloadURL: string;
  sha256: string;
  track: "stable" | "preview";
  architecture: Architecture;
  /** The notarized drag-to-Applications disk image, when that release has one. */
  diskImage: DiskImage | null;
}

// A disk image is optional, but all of its fields come together, and it must sit next to the same release's ZIP.
function validateDiskImage(entry: Record<string, unknown>, zip: URL, version: string, build: string, suffix: string): DiskImage | null {
  const keys = ["diskImageURL", "diskImageSHA256", "diskImageSize"] as const;
  const present = keys.filter(key => entry[key] !== undefined);
  if (present.length === 0) return null;
  if (present.length !== keys.length) throw new Error("Disk image metadata must include its URL, checksum and size.");
  const {diskImageURL, diskImageSHA256, diskImageSize} = entry;
  if (typeof diskImageURL !== "string" || typeof diskImageSHA256 !== "string" || !/^[a-f0-9]{64}$/.test(diskImageSHA256) ||
      typeof diskImageSize !== "number" || !Number.isSafeInteger(diskImageSize) || diskImageSize <= 0) {
    throw new Error("Disk image metadata failed validation.");
  }
  const url = new URL(diskImageURL);
  const expected = new URL(`annotator-${version}-${build}-${suffix}.dmg`, zip);
  if (url.href !== expected.href || url.username || url.password || url.search || url.hash) {
    throw new Error(`The disk image must be the same release's annotator-VERSION-BUILD-${suffix}.dmg.`);
  }
  return {url: url.href, sha256: diskImageSHA256, size: diskImageSize};
}

function validate(value: unknown, track: Download["track"]): Download | null {
  if (value === null) return null;
  if (typeof value !== "object" || !value) throw new Error("Invalid release download metadata.");
  const entry = value as Record<string, unknown>;
  if (typeof entry.releaseVersion !== "string" ||
      !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(alpha|beta|rc)\.[1-9]\d*)?$/.test(entry.releaseVersion) ||
      typeof entry.build !== "string" || !/^[1-9]\d*$/.test(entry.build) ||
      typeof entry.sha256 !== "string" || !/^[a-f0-9]{64}$/.test(entry.sha256) ||
      entry.track !== track || typeof entry.downloadURL !== "string") {
    throw new Error("Release download metadata failed validation.");
  }
  // Older generated data has no architecture; those releases are Apple Silicon only.
  const architecture = entry.architecture ?? "arm64";
  if (architecture !== "arm64" && architecture !== "arm64 x86_64") throw new Error("Release architecture failed validation.");
  const suffix = archiveSuffix[architecture];
  const url = new URL(entry.downloadURL);
  const filename = `annotator-${entry.releaseVersion}-${entry.build}-${suffix}.zip`;
  if (url.protocol !== "https:" || url.hostname !== "github.com" || url.username || url.password ||
      url.search || url.hash || !url.pathname.endsWith(`/releases/download/v${entry.releaseVersion}/${filename}`) ||
      (track === "stable") === entry.releaseVersion.includes("-")) {
    throw new Error("Release download URL or track does not match its version.");
  }
  return { releaseVersion: entry.releaseVersion, build: entry.build, downloadURL: entry.downloadURL,
    sha256: entry.sha256, track, architecture, diskImage: validateDiskImage(entry, url, entry.releaseVersion, entry.build, suffix) };
}

export const distribution = {
  stable: validate(data.stable, "stable"),
  preview: validate(data.preview, "preview"),
};
export const hasDownloads = Boolean(distribution.stable || distribution.preview);
/** Whether a release's app also runs on Intel Macs. */
export function runsOnIntel(entry: Download | null | undefined): boolean {
  return entry?.architecture === "arm64 x86_64";
}
/** The file the main download button offers: the disk image when there is one, otherwise the ZIP. */
export function preferredDownloadURL(entry: Download): string {
  return entry.diskImage?.url ?? entry.downloadURL;
}
export function releasePageURL(entry: Download): string {
  return new URL(`../../tag/v${entry.releaseVersion}`, entry.downloadURL).href;
}
export function releaseStageLabel(entry: Download): string {
  if (entry.track === "stable") return "Version";
  if (entry.releaseVersion.includes("-alpha.")) return "Public alpha";
  if (entry.releaseVersion.includes("-beta.")) return "Public beta";
  if (entry.releaseVersion.includes("-rc.")) return "Release candidate";
  throw new Error("A preview release must name its testing stage.");
}
export function supportsRegionSharing(entry: Download | null | undefined): boolean {
  return Boolean(entry && BigInt(entry.build) >= 19n);
}
export const githubURL = "https://github.com/dorlugasigal/annotator";
export const issuesURL = "https://github.com/dorlugasigal/annotator/issues/new/choose";
