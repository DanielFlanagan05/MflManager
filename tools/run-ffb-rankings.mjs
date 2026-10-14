import { createRequire } from "node:module";
import { readFile, writeFile, rename, mkdir, open, unlink } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
const modules = { QB: "quarterback", RB: "running-back", WR: "wide-receiver", TE: "tight-end", DEF: "defense", K: "kicker" };
const [position, year, date, output] = process.argv.slice(2);
if (!modules[position] || !/^\d{4}$/.test(year) || !/^\d{4}-\d{2}-\d{2}$/.test(date) || !output)
    throw new Error("Invalid ranking extraction arguments.");
const root = dirname(fileURLToPath(import.meta.url));
let chromium;
try { ({ chromium } = createRequire(import.meta.url)("playwright")); }
catch { ({ chromium } = createRequire(join(dirname(process.execPath), "..", "ranking-runtime.cjs"))("playwright")); }
await mkdir(dirname(output), { recursive: true });
// Persistent per-day claim: failures also count as the day's one scrape attempt.
const attempt = `${output}.attempt`;
const claim = await open(attempt, "wx");
await claim.writeFile(JSON.stringify({ position, date, attemptedAt: new Date().toISOString() }));
await claim.close();
let browser;
const temp = `${output}.${process.pid}.tmp`;
try {
    browser = await chromium.launch({ channel: "msedge", headless: true });
    const page = await browser.newPage();
    await page.goto(`https://www.thefantasyfootballers.com/${year}-${modules[position]}-rankings/`, { waitUntil: "domcontentloaded", timeout: 45000 });
    await page.waitForFunction(() => document.querySelectorAll('#free-rankings-table tbody tr, #simple-rankings-table tbody tr').length > 0, null, { timeout: 30000 });
    // DataTables may paginate; expose every rendered row in its default consensus order.
    await page.evaluate(() => {
        const table = document.querySelector('#free-rankings-table, #simple-rankings-table');
        if (window.jQuery?.fn?.dataTable?.isDataTable(table)) window.jQuery(table).DataTable().search('').page.len(-1).draw();
    });
    await page.addScriptTag({ type: "module", content: (await readFile(join(root, 'ffb-qb-rankings-extractor.mjs'), 'utf8')) });
    await page.waitForFunction(() => !!window.FfbQbRankingsExtractor);
    // Use each existing positional module, with only its imports bound to the shared extractor.
    const pageModule = (await readFile(join(root, `ffb-${modules[position]}-rankings.mjs`), 'utf8'))
        .replace(/^import .*;\s*/m, '').replace('export const run', 'const run');
    const result = await page.evaluate(({ code, year, slug }) => {
        const url = `https://www.thefantasyfootballers.com/${year}-${slug}-rankings/`;
        const key = { quarterback: 'quarterback', 'running-back': 'runningBack', 'wide-receiver': 'wideReceiver', 'tight-end': 'tightEnd', defense: 'defense', kicker: 'kicker' }[slug];
        const RankingPages = { [key]: url };
        const extractExpectedPage = (expected, doc) => {
            if (doc.location.pathname !== new URL(expected).pathname) throw new Error('Unexpected ranking source page.');
            return window.FfbQbRankingsExtractor.run(doc);
        };
        return new Function('RankingPages', 'extractExpectedPage', `${code}; return run(document);`)(RankingPages, extractExpectedPage);
    }, { code: pageModule, year, slug: modules[position] });
    await writeFile(temp, result.csv, { flag: 'wx' });
    await rename(temp, output);
    console.log(JSON.stringify({ position, date, rowCount: result.rows.length }));
} finally {
    await browser?.close();
    await unlink(temp).catch(() => {});
}
