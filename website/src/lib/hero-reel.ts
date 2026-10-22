import manifest from "../data/hero-reel.json";
import { sitePath } from "./paths";

/** The phone reel is chosen with this media query in <source>, <picture> and preload links. */
export const phoneMedia = "(max-width: 600px)";

export interface ReelEncoding {
  file: string;
  poster: string;
  width: number;
  height: number;
  duration: number;
  fps: number;
  sha256: string;
  bytes: number;
}
export interface ReelMusic {
  title: string;
  artist: string;
  source: string;
  url: string;
  license: string;
}
interface ReelManifest {
  schemaVersion: number;
  status: string;
  description: string;
  provenance: string;
  loop: boolean;
  /** The reel carries sound. The page still starts it muted, and a Sound button turns it on. */
  audio: boolean;
  /** Required when audio is true, so the page can credit the music. */
  music?: ReelMusic;
  desktop: ReelEncoding;
  phone?: ReelEncoding;
}
export interface ReelSource extends ReelEncoding {
  videoURL: string;
  posterURL: string;
}

function source(name: string, encoding: ReelEncoding): ReelSource {
  const { file, poster, width, height, duration, sha256 } = encoding;
  if (!/^\/demo\/[a-z0-9-]+\.mp4$/.test(file) || !/^\/demo\/[a-z0-9-]+\.jpg$/.test(poster) ||
      !/^[a-f0-9]{64}$/.test(sha256) || !(width > 0 && height > 0 && duration > 0)) {
    throw new Error(`The ${name} entry in hero-reel.json is incomplete.`);
  }
  // The poster is a frame of the same encoding, so the video checksum versions both URLs.
  const version = sha256.slice(0, 12);
  return { ...encoding, videoURL: `${sitePath(file)}?v=${version}`, posterURL: `${sitePath(poster)}?v=${version}` };
}

export function heroReel() {
  const reel = manifest as ReelManifest;
  if (reel.schemaVersion !== 1 || !reel.description?.trim() || !reel.provenance?.trim() || typeof reel.audio !== "boolean") {
    throw new Error("hero-reel.json needs schemaVersion 1, a description, a provenance note and an audio flag.");
  }
  const music = reel.audio ? reel.music : undefined;
  if (reel.audio && !(music?.title?.trim() && music.artist?.trim() && music.source?.trim() && music.license?.trim() &&
      /^https:\/\//.test(music.url ?? ""))) {
    throw new Error("A reel with sound needs a music credit in hero-reel.json: title, artist, source, url and license.");
  }
  return {
    description: reel.description,
    provenance: reel.provenance,
    audio: reel.audio,
    music,
    desktop: source("desktop", reel.desktop),
    phone: reel.phone ? source("phone", reel.phone) : undefined,
  };
}
