/** The published Microsoft Store listing of the Windows edition (spec 013, WIN-DST-02 and WIN-DST-08). */
export interface WindowsListing {
  /** The 12-character Store ID from Partner Center > Product management > Product identity. */
  storeId: string;
  /** The Store's web listing. On Windows it also opens the listing in the Store app. */
  listingURL: string;
  /** winget installs Store apps through its built-in msstore source. */
  wingetCommand: string;
}

/**
 * Reads src/data/windows-listing.json. storeId stays null until the owner records the ID of a listing that
 * anyone can open; while it is null, the site makes no Windows claim at all.
 */
export function parseWindowsListing(value: unknown): WindowsListing | null {
  if (!value || typeof value !== "object" || Array.isArray(value) || Object.keys(value).join() !== "storeId") {
    throw new Error("The Windows listing file must contain only storeId: null, or the Store ID of a published listing.");
  }
  const {storeId} = value as {storeId: unknown};
  if (storeId === null) return null;
  // Every Microsoft Store product has a 12-character alphanumeric Store ID, such as 9NBLGGH4R315.
  if (typeof storeId !== "string" || !/^[0-9A-Z]{12}$/.test(storeId)) {
    throw new Error("storeId must be the 12-character Store ID from Partner Center, such as 9NBLGGH4R315.");
  }
  return {
    storeId,
    listingURL: `https://apps.microsoft.com/detail/${storeId}`,
    wingetCommand: `winget install --id ${storeId} --source msstore`,
  };
}
