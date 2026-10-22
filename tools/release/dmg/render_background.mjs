#!/usr/bin/env node
import playwright from '../../../website/node_modules/@playwright/test/index.js';
const { chromium } = playwright;
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const html = 'file://' + path.join(here, 'background.html');
const browser = await chromium.launch();
try {
  for (const scale of [1, 2]) {
    const page = await browser.newPage({ viewport: { width: 640, height: 420 }, deviceScaleFactor: scale });
    await page.goto(html);
    await page.screenshot({ path: path.join(here, scale === 1 ? 'background.png' : 'background@2x.png'), omitBackground: false });
    await page.close();
  }
} finally {
  await browser.close();
}
