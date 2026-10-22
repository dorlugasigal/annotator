const DAY_MS = 86_400_000;

export function dayString(date: Date): string {
  return date.toISOString().slice(0, 10);
}

export function isDay(value: unknown): value is string {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const date = new Date(`${value}T00:00:00Z`);
  return Number.isFinite(date.getTime()) && dayString(date) === value;
}

export function addDays(day: string, amount: number): string {
  return dayString(new Date(Date.parse(`${day}T00:00:00Z`) + amount * DAY_MS));
}

export function daysBetween(from: string, to: string): number {
  return Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / DAY_MS);
}
