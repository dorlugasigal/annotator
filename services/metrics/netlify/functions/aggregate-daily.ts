import type { Config } from "@netlify/functions";
import { handleScheduled } from "../../src/http.ts";
import { metricsStore } from "../../src/store.ts";

export default async () => { await handleScheduled({ store: await metricsStore(), env: process.env }); };

export const config: Config = { schedule: "15 0 * * *" };
