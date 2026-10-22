import manifest from "../data/hero-reel-chapters.json";
import type { ReelSource } from "./hero-reel";

export interface ReelChapter {
  id: string;
  kind: "title" | "part";
  label: string;
  start: number;
  end: number;
  /** "0:05" style start time for labels. */
  clock: string;
}

function clock(seconds: number): string {
  const whole = Math.floor(seconds);
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, "0")}`;
}

/**
 * The parts of the hero reel. They are placed on the reel's beat grid, so they follow a new tempo;
 * a unit test checks that someone has compared them with the current encoding.
 */
export function reelChapters(desktop: ReelSource): ReelChapter[] {
  const { schemaVersion, beats, chapters } = manifest as {
    schemaVersion: number; beats: number;
    chapters: { id: string; kind: string; label: string; beat: number }[];
  };
  const beat = desktop.duration / beats;
  const bpm = 60 / beat;
  if (schemaVersion !== 1 || !Number.isInteger(beats) || beats < 16 || !(bpm >= 90 && bpm <= 180)) {
    throw new Error("hero-reel-chapters.json does not fit this reel: check its beat count.");
  }
  const ids = new Set<string>();
  return chapters.map((chapter, index) => {
    const next = chapters[index + 1];
    const start = index === 0 ? 0 : Number(((chapter.beat - 1) * beat).toFixed(3));
    const end = next ? Number(((next.beat - 1) * beat).toFixed(3)) : desktop.duration;
    if (!/^[a-z-]+$/.test(chapter.id) || ids.has(chapter.id) || !chapter.label.trim() ||
        !["title", "part"].includes(chapter.kind) || !Number.isInteger(chapter.beat) ||
        (index === 0 ? chapter.beat !== 1 : !(chapter.beat > chapters[index - 1]!.beat)) ||
        chapter.beat > beats || !(end > start)) {
      throw new Error(`Chapter ${index + 1} in hero-reel-chapters.json is invalid.`);
    }
    ids.add(chapter.id);
    return { id: chapter.id, kind: chapter.kind as ReelChapter["kind"], label: chapter.label, start, end, clock: clock(start) };
  });
}
