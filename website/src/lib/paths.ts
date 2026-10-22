export function sitePath(path: string): string {
  if (!path.startsWith("/") || path.startsWith("//")) {
    throw new Error("Expected an app-local absolute path.");
  }
  return `${import.meta.env.BASE_URL.replace(/\/$/, "")}${path}`;
}
