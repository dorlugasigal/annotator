import { readFileSync } from "node:fs";
import bundled from "../data/windows-listing.json";
import { parseWindowsListing } from "./windows-listing";

// Build-time fixture override for checking the pages with a listing, like DISTRIBUTION_METADATA_FILE.
export const windowsListing = parseWindowsListing(process.env.WINDOWS_LISTING_FILE
  ? JSON.parse(readFileSync(process.env.WINDOWS_LISTING_FILE, "utf8"))
  : bundled);
