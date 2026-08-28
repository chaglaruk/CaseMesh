export type ReviewOrigin = 0 | 1 | 2;

export type ReviewImportItem = {
  id: string;
  origin: ReviewOrigin;
  text: string;
  startedAt: string;
  endedAt: string;
  contextCitationSourceSpanIds: string[];
};

const originMap: Record<string, ReviewOrigin> = {
  HR_SAID: 0,
  HRSAID: 0,
  HR: 0,
  USER_ACTUALLY_SAID: 1,
  USERACTUALLYSAID: 1,
  USER: 1,
  AI_SUGGESTED: 2,
  AISUGGESTED: 2,
  AI: 2,
};

export function parseReviewTranscriptJson(
  input: string,
  newId: () => string = () => globalThis.crypto.randomUUID(),
): ReviewImportItem[] {
  let raw: unknown;
  try {
    raw = JSON.parse(input);
  } catch {
    throw new Error("Transcript must be valid JSON.");
  }
  if (!Array.isArray(raw) || raw.length === 0 || raw.length > 500) {
    throw new Error("Transcript JSON must contain between 1 and 500 items.");
  }

  let totalCharacters = 0;
  const ids = new Set<string>();
  return raw.map((entry, index) => {
    if (!entry || typeof entry !== "object" || Array.isArray(entry)) {
      throw new Error(`Transcript item ${index + 1} must be an object.`);
    }
    const value = entry as Record<string, unknown>;
    const id = typeof value.id === "string" && value.id.trim() ? value.id.trim() : newId();
    if (!isGuid(id) || ids.has(id)) throw new Error(`Transcript item ${index + 1} requires a distinct valid Guid.`);
    ids.add(id);

    const origin = parseOrigin(value.origin, index);
    const text = typeof value.text === "string" ? value.text : "";
    if (!text.trim() || text.length > 8000) {
      throw new Error(`Transcript item ${index + 1} text must contain between 1 and 8000 characters.`);
    }
    totalCharacters += text.length;
    if (totalCharacters > 1_000_000) throw new Error("Transcript text exceeds the 1,000,000 character limit.");

    const startedAt = parseTimestamp(value.startedAt, `Transcript item ${index + 1} startedAt`);
    const endedAt = parseTimestamp(value.endedAt, `Transcript item ${index + 1} endedAt`);
    if (Date.parse(endedAt) < Date.parse(startedAt)) {
      throw new Error(`Transcript item ${index + 1} endedAt cannot precede startedAt.`);
    }

    const citationsRaw = value.contextCitationSourceSpanIds ?? [];
    if (!Array.isArray(citationsRaw) || citationsRaw.length > 16) {
      throw new Error(`Transcript item ${index + 1} context citations must be an array of at most 16 Guids.`);
    }
    const citations = citationsRaw.map((citation, citationIndex) => {
      if (typeof citation !== "string" || !isGuid(citation)) {
        throw new Error(`Transcript item ${index + 1} context citation ${citationIndex + 1} must be a Guid.`);
      }
      return citation;
    });
    if (new Set(citations).size !== citations.length) {
      throw new Error(`Transcript item ${index + 1} context citations must be distinct.`);
    }

    return { id, origin, text, startedAt, endedAt, contextCitationSourceSpanIds: citations };
  });
}

export function reviewOriginLabel(origin: number): string {
  if (origin === 0) return "HR said";
  if (origin === 1) return "You actually said";
  if (origin === 2) return "AI suggested";
  return "Unknown origin";
}

function parseOrigin(value: unknown, index: number): ReviewOrigin {
  if (value === 0 || value === 1 || value === 2) return value;
  if (typeof value === "string") {
    const normalized = value.trim().toUpperCase().replace(/[ -]+/g, "_");
    const mapped = originMap[normalized] ?? originMap[normalized.replaceAll("_", "")];
    if (mapped !== undefined) return mapped;
  }
  throw new Error(`Transcript item ${index + 1} origin must be HR_SAID, USER_ACTUALLY_SAID, or AI_SUGGESTED.`);
}

function parseTimestamp(value: unknown, label: string): string {
  if (typeof value !== "string" || !value.trim() || Number.isNaN(Date.parse(value))) {
    throw new Error(`${label} must be a valid timestamp.`);
  }
  return new Date(value).toISOString();
}

function isGuid(value: string): boolean {
  // Match the .NET Guid "D" textual contract used by the API. CaseMesh canonical
  // deterministic identifiers are valid Guids even when their version/variant bits
  // do not describe an RFC 4122 randomly generated UUID.
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}
