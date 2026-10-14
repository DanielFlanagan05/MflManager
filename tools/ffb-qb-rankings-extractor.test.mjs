import assert from "node:assert/strict";
import test from "node:test";

import { parseTeamAndVenue, validateRows } from "./ffb-qb-rankings-extractor.mjs";

const validRow = {
    playerName: "Lamar Jackson",
    playerRank: 1,
    teamCode: "BAL",
    opposingTeamCode: "NO",
    homeOrAway: "home",
};

test("validateRows rejects a row with a missing required value", () => {
    assert.throws(
        () => validateRows([{ ...validRow, opposingTeamCode: "" }]),
        /missing opposingTeamCode/,
    );
});

test("validateRows rejects exact duplicate rows", () => {
    assert.throws(
        () => validateRows([validRow, { ...validRow }]),
        /duplicate complete rows/,
    );
});

test("parseTeamAndVenue ignores appended injury tooltip text", () => {
    assert.deepEqual(
        parseTeamAndVenue("MIN (@ CHI) Questionable with Concussion"),
        { teamCode: "MIN", opposingTeamCode: "CHI", homeOrAway: "away" },
    );
});
