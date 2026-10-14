/**
 * Extracts the public quarterback rankings table after the Fantasy Footballers
 * page has rendered. Run in the page's browser context:
 *
 *   const result = FfbQbRankingsExtractor.run();
 *   FfbQbRankingsExtractor.downloadCsv(result.csv);
 */

export const QuarterbackRankingsUrl =
    "https://www.thefantasyfootballers.com/2026-quarterback-rankings/";

const RequiredFields = [
    "playerName",
    "playerRank",
    "teamCode",
    "opposingTeamCode",
    "homeOrAway",
];

function normalizeText(value) {
    return (value ?? "").replace(/\s+/g, " ").trim();
}

export function parseTeamAndVenue(value) {
    // Injury badges are descendants of player-right-line-two. Their tooltip text
    // is included in textContent, so match the game context itself rather than
    // assuming the full element text ends after the opponent code.
    const match = normalizeText(value).match(
        /(?<teamCode>[A-Z]{2,3}) \((?<venue>vs|@)\s*(?<opposingTeamCode>[A-Z]{2,3})\)/,
    );

    if (!match?.groups) {
        throw new Error(`Could not parse team and opponent from: "${normalizeText(value)}".`);
    }

    return {
        teamCode: match.groups.teamCode,
        opposingTeamCode: match.groups.opposingTeamCode,
        homeOrAway: match.groups.venue === "vs" ? "home" : "away",
    };
}

export function extractRowsFromEntries(entries) {
    return entries.map(({ playerName, teamAndVenue }, index) => {
        return {
            playerName: normalizeText(playerName),
            // The table is initialized in ascending consensus-rank order. Use the
            // row sequence rather than an unneeded column dependency.
            playerRank: index + 1,
            ...parseTeamAndVenue(teamAndVenue),
        };
    });
}

export function extractRows(table) {
    if (!table) {
        throw new Error("The #free-rankings-table element was not found.");
    }

    const isSimpleTable = table.id === "simple-rankings-table";
    const entries = Array.from(table.querySelectorAll("tbody tr")).map((row) => {
        if (!isSimpleTable) {
            return {
                playerName: row.querySelector(".player-right-line-one .player-name")?.textContent,
                teamAndVenue: row.querySelector(".player-right-line-two")?.textContent,
            };
        }

        const team = normalizeText(row.querySelector(".player-right-line-two")?.textContent).match(/^[A-Z]{2,3}/)?.[0];
        const opponentCell = normalizeText(row.querySelector("td:nth-child(2)")?.textContent);
        const venue = opponentCell.startsWith("@") ? "@" : "vs";
        const opponent = opponentCell.replace(/^@/, "");
        return {
            playerName: row.querySelector(".player-right-line-one .player-name")?.textContent,
            teamAndVenue: `${team ?? ""} (${venue} ${opponent})`,
        };
    });
    return extractRowsFromEntries(entries);
}

export function validateRows(rows) {
    if (!Array.isArray(rows) || rows.length === 0) {
        throw new Error("The rankings table did not yield any rows.");
    }

    for (const [index, row] of rows.entries()) {
        for (const field of RequiredFields) {
            if (row[field] === undefined || row[field] === null || String(row[field]).trim() === "") {
                throw new Error(`Row ${index + 1} is missing ${field}.`);
            }
        }
        if (!Number.isInteger(row.playerRank) || row.playerRank < 1) {
            throw new Error(`Row ${index + 1} has an invalid playerRank.`);
        }
        if (row.homeOrAway !== "home" && row.homeOrAway !== "away") {
            throw new Error(`Row ${index + 1} has an invalid homeOrAway value.`);
        }
    }

    const serializedRows = rows.map((row) =>
        JSON.stringify(RequiredFields.map((field) => row[field])),
    );
    if (new Set(serializedRows).size !== serializedRows.length) {
        throw new Error("The rankings table contains duplicate complete rows.");
    }
}

function escapeCsv(value) {
    const text = String(value ?? "");
    return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

export function toCsv(rows) {
    validateRows(rows);
    const columns = ["player name", "player rank", "team code", "opposing team code", "home or away"];
    const lines = rows.map((row) => [
        row.playerName,
        row.playerRank,
        row.teamCode,
        row.opposingTeamCode,
        row.homeOrAway,
    ].map(escapeCsv).join(","));
    return `${columns.join(",")}\r\n${lines.join("\r\n")}\r\n`;
}

export function run(documentRef = document) {
    const rows = extractRows(documentRef.querySelector("#free-rankings-table, #simple-rankings-table"));
    validateRows(rows);
    return { rows, csv: toCsv(rows) };
}

export function downloadCsv(csv, documentRef = document) {
    const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
    const link = documentRef.createElement("a");
    link.href = url;
    link.download = `ffb-quarterback-rankings-${new Date().toLocaleDateString('en-CA')}.csv`;
    link.click();
    URL.revokeObjectURL(url);
}

// This makes the module convenient to invoke from a browser developer console
// after it is loaded as a module, while keeping its parsing logic testable in Node.
if (typeof window !== "undefined") {
    window.FfbQbRankingsExtractor = { parseTeamAndVenue, extractRowsFromEntries, extractRows, validateRows, toCsv, run, downloadCsv };
}
