import type { Config } from "@netlify/functions";
import { handleLogout } from "../../src/http.ts";

export default async (request: Request) => handleLogout(request);

export const config: Config = { path: "/api/admin/logout" };
