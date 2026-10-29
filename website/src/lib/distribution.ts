import { readFileSync } from "node:fs";
import bundled from "../data/distribution.json";
import { parseDistribution, type Download } from "./distribution-metadata";

export type { Architecture, DiskImage, Download } from "./distribution-metadata";

/** The newest published release, or null while none is published. DISTRIBUTION_METADATA_FILE swaps in a test fixture. */
export const release: Download | null = parseDistribution(process.env.DISTRIBUTION_METADATA_FILE
  ? JSON.parse(readFileSync(process.env.DISTRIBUTION_METADATA_FILE, "utf8"))
  : bundled);
export const hasDownloads = release !== null;
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
export const githubURL = "https://github.com/dorlugasigal/annotator";
export const issuesURL = "https://github.com/dorlugasigal/annotator/issues/new/choose";
