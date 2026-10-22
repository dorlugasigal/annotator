import type { Config } from "@netlify/functions";
import { handleSummary } from "../../src/http.ts";
import { metricsStore } from "../../src/store.ts";

export default async (request: Request) => handleSummary(request, { store: await metricsStore(), env: process.env });

export const config: Config = {
  path: "/api/admin/summary",
  rateLimit: { windowLimit: 30, windowSize: 60, aggregateBy: ["ip", "domain"] },
};
