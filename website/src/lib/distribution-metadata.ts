/** A release's app: Apple Silicon only, or universal (Apple Silicon and Intel). */
export type Architecture = "arm64" | "arm64 x86_64";

export interface DiskImage {
  url: string;
  sha256: string;
  size: number;
}

/** The newest published release, as the release tools write it to src/data/distribution.json. */
export interface Download {
  releaseVersion: string;
  downloadURL: string;
  sha256: string;
  architecture: Architecture;
  /** The notarized drag-to-Applications disk image, when that release has one. */
  diskImage: DiskImage | null;
}

const archiveSuffix: Record<Architecture, string> = { "arm64": "arm64", "arm64 x86_64": "universal" };
const fields = ["releaseVersion", "downloadURL", "sha256", "architecture", "diskImageURL", "diskImageSHA256", "diskImageSize"];
const checksum = /^[a-f0-9]{64}$/;
const plainVersion = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
// Releases up to 0.1.0-beta.17 had a pre-release label and put their build number in their file names, like
// annotator-0.1.0-beta.17-35-universal.zip. Until 0.2.0 ships, the newest published release is one of them.
const legacyVersion = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-(alpha|beta|rc)\.[1-9]\d*$/;
// https://github.com/OWNER/REPO/releases/download/vVERSION/NAME, with no credentials, port, query or fragment.
const releaseAsset = /^https:\/\/github\.com\/[A-Za-z0-9-]+\/[A-Za-z0-9._-]+\/releases\/download\/v([^/?#]+)\/([^/?#]+)$/;

/** annotator-VERSION-SUFFIX.zip, or annotator-VERSION-BUILD-SUFFIX.zip for a legacy version. */
function isArchiveName(name: string, version: string, suffix: string, legacy: boolean): boolean {
  if (!legacy) return name === `annotator-${version}-${suffix}.zip`;
  const build = name.slice(`annotator-${version}-`.length, -`-${suffix}.zip`.length);
  return /^[1-9]\d*$/.test(build) && name === `annotator-${version}-${build}-${suffix}.zip`;
}

// A disk image is optional, but all of its fields come together, and it sits next to the same release's ZIP.
function parseDiskImage(entry: Record<string, unknown>, zipURL: string): DiskImage | null {
  const keys = ["diskImageURL", "diskImageSHA256", "diskImageSize"];
  const present = keys.filter(key => Object.hasOwn(entry, key));
  if (present.length === 0) return null;
  if (present.length !== keys.length) throw new Error("Disk image metadata must include its URL, checksum and size.");
  const { diskImageURL: url, diskImageSHA256: sha256, diskImageSize: size } = entry;
  if (typeof url !== "string" || typeof sha256 !== "string" || !checksum.test(sha256) ||
      typeof size !== "number" || !Number.isSafeInteger(size) || size <= 0) {
    throw new Error("Disk image metadata failed validation.");
  }
  if (url !== zipURL.replace(/\.zip$/, ".dmg")) {
    throw new Error("The disk image must sit next to the release's ZIP, with the same name ending in .dmg.");
  }
  return { url, sha256, size };
}

/**
 * Reads src/data/distribution.json: the newest published release, or null while none is published. The release tools
 * write it when they publish the website. Anything else stops the build, so the site never offers an unverified file.
 */
export function parseDistribution(value: unknown): Download | null {
  if (!value || typeof value !== "object" || Array.isArray(value) || Object.keys(value).join() !== "release") {
    throw new Error("Distribution metadata must contain only release: the newest published release, or null.");
  }
  const { release } = value as { release: unknown };
  if (release === null) return null;
  if (!release || typeof release !== "object" || Array.isArray(release)) throw new Error("Invalid release download metadata.");
  const entry = release as Record<string, unknown>;
  const unsupported = Object.keys(entry).filter(key => !fields.includes(key));
  if (unsupported.length) throw new Error(`Release download metadata has unsupported fields: ${unsupported.join(", ")}.`);
  const { releaseVersion: version, downloadURL, sha256 } = entry;
  if (typeof version !== "string" || !(plainVersion.test(version) || legacyVersion.test(version)) ||
      typeof sha256 !== "string" || !checksum.test(sha256) || typeof downloadURL !== "string") {
    throw new Error("Release download metadata failed validation.");
  }
  // Older generated data has no architecture; those releases are Apple Silicon only.
  const architecture = Object.hasOwn(entry, "architecture") ? entry.architecture : "arm64";
  if (architecture !== "arm64" && architecture !== "arm64 x86_64") throw new Error("Release architecture failed validation.");
  const suffix = archiveSuffix[architecture];
  const legacy = legacyVersion.test(version);
  const asset = releaseAsset.exec(downloadURL);
  // The URL must already be in its normal form, so a path like /owner/../ can't point somewhere else.
  if (!asset || asset[1] !== version || !isArchiveName(asset[2], version, suffix, legacy) || new URL(downloadURL).href !== downloadURL) {
    const name = legacy ? `annotator-${version}-BUILD-${suffix}.zip` : `annotator-${version}-${suffix}.zip`;
    throw new Error(`The download must be https://github.com/OWNER/REPO/releases/download/v${version}/${name}.`);
  }
  return { releaseVersion: version, downloadURL, sha256, architecture, diskImage: parseDiskImage(entry, downloadURL) };
}
