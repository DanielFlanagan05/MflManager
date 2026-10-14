import { readFile, writeFile } from "node:fs/promises";

import { extractRowsFromEntries, toCsv, validateRows } from "./ffb-qb-rankings-extractor.mjs";

const [inputPath, outputPath] = process.argv.slice(2);
if (!inputPath || !outputPath) {
    throw new Error("Usage: node tools/export-ffb-qb-rankings.mjs <live-dom-entries.json> <output.csv>");
}

const entries = JSON.parse(await readFile(inputPath, "utf8"));
const rows = extractRowsFromEntries(entries);
validateRows(rows);
await writeFile(outputPath, toCsv(rows), "utf8");
console.log(`Wrote ${rows.length} rows to ${outputPath}.`);
