import { extractExpectedPage, RankingPages } from "./ffb-ranking-pages.mjs";
export const run = (documentRef = document) => extractExpectedPage(RankingPages.wideReceiver, documentRef);
