export interface TakeFrame { width: number; height: number; toolbarBottomInset?: number }

/** Crop only the recorded empty bottom inset, keeping 32px below the lowest toolbar sample. */
export function displayHeight(take: TakeFrame): number {
  return take.height - Math.max(0, (take.toolbarBottomInset ?? 0) - 32);
}
