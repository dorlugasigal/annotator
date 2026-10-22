import type { Config } from "@netlify/functions";
import { handleApp } from "../../src/http.ts";
import { metricsStore } from "../../src/store.ts";

export default async (request: Request) => handleApp(request, { store: await metricsStore(), env: process.env });

export const config: Config = {
  path: "/api/metrics/app",
  rateLimit: { windowLimit: 30, windowSize: 60, aggregateBy: ["ip", "domain"] },
};
