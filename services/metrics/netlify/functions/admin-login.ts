import type { Config } from "@netlify/functions";
import { handleLogin } from "../../src/http.ts";
import { metricsStore } from "../../src/store.ts";

export default async (request: Request) => handleLogin(request, { store: await metricsStore(), env: process.env });

export const config: Config = {
  path: "/api/admin/login",
  rateLimit: { windowLimit: 5, windowSize: 60, aggregateBy: ["ip", "domain"] },
};
