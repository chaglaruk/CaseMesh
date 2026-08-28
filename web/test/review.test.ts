import { describe, expect, it } from "vitest";
import { parseReviewTranscriptJson, reviewOriginLabel } from "../lib/review";

const ids = [
  "10000000-0000-4000-8000-000000000001",
  "10000000-0000-4000-8000-000000000002",
];

describe("uploaded transcript Review parser", () => {
  it("normalizes explicit speaker origins and timestamps without changing wording", () => {
    let next = 0;
    const items = parseReviewTranscriptJson(JSON.stringify([
      {
        origin: "HR_SAID",
        text: "  Synthetic HR statement.  ",
        startedAt: "2026-08-28T10:00:00+01:00",
        endedAt: "2026-08-28T10:00:02+01:00",
        contextCitationSourceSpanIds: ["20000000-0000-4000-8000-000000000001"],
      },
      {
        origin: "AI_SUGGESTED",
        text: "Verify the source.",
        startedAt: "2026-08-28T10:00:03+01:00",
        endedAt: "2026-08-28T10:00:04+01:00",
      },
    ]), () => ids[next++]);

    expect(items.map(item => item.origin)).toEqual([0, 2]);
    expect(items[0].text).toBe("  Synthetic HR statement.  ");
    expect(items[0].startedAt).toBe("2026-08-28T09:00:00.000Z");
    expect(items[0].contextCitationSourceSpanIds).toEqual(["20000000-0000-4000-8000-000000000001"]);
    expect(reviewOriginLabel(items[1].origin)).toBe("AI suggested");
  });

  it("accepts canonical deterministic .NET Guid identifiers without RFC version or variant bits", () => {
    const sourceSpanId = "20000000-0000-0000-0000-000000000001";
    const itemId = "10000000-0000-0000-0000-000000000001";
    const items = parseReviewTranscriptJson(JSON.stringify([{
      id: itemId,
      origin: "HR_SAID",
      text: "Synthetic canonical Guid statement.",
      startedAt: "2026-08-28T09:00:00Z",
      endedAt: "2026-08-28T09:00:01Z",
      contextCitationSourceSpanIds: [sourceSpanId],
    }]));

    expect(items[0].id).toBe(itemId);
    expect(items[0].contextCitationSourceSpanIds).toEqual([sourceSpanId]);
  });

  it("rejects invalid origin, time order, duplicate citations, malformed Guid, oversized text, and NUL text", () => {
    expect(() => parseReviewTranscriptJson(JSON.stringify([{
      origin: "UNKNOWN",
      text: "Statement",
      startedAt: "2026-08-28T09:00:00Z",
      endedAt: "2026-08-28T09:00:01Z",
    }]), () => ids[0])).toThrow(/origin/);

    expect(() => parseReviewTranscriptJson(JSON.stringify([{
      origin: "USER_ACTUALLY_SAID",
      text: "Statement",
      startedAt: "2026-08-28T09:00:02Z",
      endedAt: "2026-08-28T09:00:01Z",
    }]), () => ids[0])).toThrow(/precede/);

    let next = 0;
    expect(() => parseReviewTranscriptJson(JSON.stringify([
      {
        origin: "HR_SAID",
        text: "Later statement first.",
        startedAt: "2026-08-28T09:00:05Z",
        endedAt: "2026-08-28T09:00:06Z",
      },
      {
        origin: "USER_ACTUALLY_SAID",
        text: "Earlier statement second.",
        startedAt: "2026-08-28T09:00:04Z",
        endedAt: "2026-08-28T09:00:05Z",
      },
    ]), () => ids[next++])).toThrow(/previous transcript item/);

    const citation = "20000000-0000-4000-8000-000000000001";
    expect(() => parseReviewTranscriptJson(JSON.stringify([{
      origin: "HR_SAID",
      text: "Statement",
      startedAt: "2026-08-28T09:00:00Z",
      endedAt: "2026-08-28T09:00:01Z",
      contextCitationSourceSpanIds: [citation, citation],
    }]), () => ids[0])).toThrow(/distinct/);

    expect(() => parseReviewTranscriptJson(JSON.stringify([{
      origin: "HR_SAID",
      text: "Statement",
      startedAt: "2026-08-28T09:00:00Z",
      endedAt: "2026-08-28T09:00:01Z",
      contextCitationSourceSpanIds: ["not-a-guid"],
    }]), () => ids[0])).toThrow(/Guid/);

    expect(() => parseReviewTranscriptJson(JSON.stringify([{
      origin: "HR_SAID",
      text: "x".repeat(8001),
      startedAt: "2026-08-28T09:00:00Z",
      endedAt: "2026-08-28T09:00:01Z",
    }]), () => ids[0])).toThrow(/8000/);

    expect(() => parseReviewTranscriptJson(JSON.stringify([{
      origin: "HR_SAID",
      text: "Synthetic\u0000statement",
      startedAt: "2026-08-28T09:00:00Z",
      endedAt: "2026-08-28T09:00:01Z",
    }]), () => ids[0])).toThrow(/NUL/);
  });

  it("rejects a Review whose total duration exceeds 24 hours", () => {
    expect(() => parseReviewTranscriptJson(JSON.stringify([
      {
        id: "10000000-0000-4000-8000-000000000011",
        origin: "HR_SAID",
        text: "First statement.",
        startedAt: "2026-08-28T09:00:00Z",
        endedAt: "2026-08-28T09:00:01Z",
      },
      {
        id: "10000000-0000-4000-8000-000000000012",
        origin: "USER_ACTUALLY_SAID",
        text: "Second statement.",
        startedAt: "2026-08-29T09:00:00Z",
        endedAt: "2026-08-29T09:00:01Z",
      },
    ]))).toThrow(/24 hours/);
  });
});
