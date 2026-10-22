import type { Config } from "@netlify/functions";
import { handleWeb } from "../../src/http.ts";
import { metricsStore } from "../../src/store.ts";

export default async (request: Request) => handleWeb(request, { store: await metricsStore(), env: process.env });

export const config: Config = {
  path: "/api/metrics/web",
  rateLimit: { windowLimit: 60, windowSize: 60, aggregateBy: ["ip", "domain"] },
};
