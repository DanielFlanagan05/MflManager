import { run as extractRenderedTable } from "./ffb-qb-rankings-extractor.mjs";

export const RankingPages = {
    quarterback: "https://www.thefantasyfootballers.com/2026-quarterback-rankings/",
    runningBack: "https://www.thefantasyfootballers.com/2026-running-back-rankings/",
    wideReceiver: "https://www.thefantasyfootballers.com/2026-wide-receiver-rankings/",
    tightEnd: "https://www.thefantasyfootballers.com/2026-tight-end-rankings/",
    defense: "https://www.thefantasyfootballers.com/2026-defense-rankings/",
    kicker: "https://www.thefantasyfootballers.com/2026-kicker-rankings/",
};

export function extractExpectedPage(expectedUrl, documentRef = document) {
    const expectedPath = new URL(expectedUrl).pathname.replace('/2026-', `/${documentRef.location?.pathname.match(/^\/(\d{4})-/)?.[1] ?? '2026'}-`);
    if (documentRef.location?.pathname !== expectedPath) {
        throw new Error(`Expected ${expectedPath}, but the browser is on ${documentRef.location?.pathname ?? "an unknown page"}.`);
    }
    return extractRenderedTable(documentRef);
}
