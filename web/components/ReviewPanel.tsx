"use client";

import { FormEvent, useEffect, useMemo, useState } from "react";
import { useParams, useSearchParams } from "next/navigation";
import { request } from "../lib/api";
import { parseReviewTranscriptJson, reviewOriginLabel } from "../lib/review";

type ReviewSummary = {
  meetingId: string;
  contextCurrentness: number;
  createdAt: string;
  startedAt: string;
  endedAt: string;
  itemCount: number;
};

type ReviewItem = {
  id: string;
  origin: number;
  text: string;
  startedAt: string;
  endedAt: string;
  contextCitationSourceSpanIds: string[];
};

type ContextReference = { sourceSpanId: string; status: number; notice: string };
type Contradiction = { contradictionId: string; type: number; detectionOrigin: string };
type ReviewView = {
  review: {
    meetingId: string;
    contextCurrentness: number;
    items: ReviewItem[];
  };
  createdAt: string;
  currentContextCurrentness: number;
  analysis: {
    contextReferences: ContextReference[];
    relevantUnresolvedContradictions: Contradiction[];
    followUpPrompts: string[];
  };
};

type SourceDetail = {
  citation: {
    sourceSpanId: string;
    documentVersionId: string;
    contentSha256: string;
    pageNumber?: number;
    textStart?: number;
    textEnd?: number;
    parserVersion: string;
    extractionConfidence?: number;
  };
  exactText: string;
};

const sampleTranscript = `[
  {
    "origin": "HR_SAID",
    "text": "We will review the request next week.",
    "startedAt": "2026-08-28T09:00:00Z",
    "endedAt": "2026-08-28T09:00:04Z",
    "contextCitationSourceSpanIds": []
  },
  {
    "origin": "USER_ACTUALLY_SAID",
    "text": "I would like the response confirmed in writing.",
    "startedAt": "2026-08-28T09:00:05Z",
    "endedAt": "2026-08-28T09:00:10Z",
    "contextCitationSourceSpanIds": []
  }
]`;

export default function ReviewPanel() {
  const params = useParams<{ matterId: string }>();
  const search = useSearchParams();
  const tenant = search.get("workspace") ?? "";
  const base = useMemo(() => `/workspaces/${tenant}/matters/${params.matterId}/review`, [tenant, params.matterId]);
  const [sessions, setSessions] = useState<ReviewSummary[]>([]);
  const [active, setActive] = useState<ReviewView>();
  const [draft, setDraft] = useState(sampleTranscript);
  const [source, setSource] = useState<SourceDetail>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function refreshSessions() {
    if (!tenant) return;
    setSessions(await request<ReviewSummary[]>(`${base}/sessions`));
  }

  useEffect(() => {
    void refreshSessions().catch(error => setError((error as Error).message));
  }, [base]);

  async function createReview(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    try {
      const items = parseReviewTranscriptJson(draft);
      const created = await request<ReviewView>(`${base}/sessions`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ items }),
      });
      setActive(created);
      setSource(undefined);
      await refreshSessions();
      setError("");
    } catch (error) {
      setError((error as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function openReview(meetingId: string) {
    setBusy(true);
    try {
      setActive(await request<ReviewView>(`${base}/sessions/${meetingId}`));
      setSource(undefined);
      setError("");
    } catch (error) {
      setError((error as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function inspectSource(sourceSpanId: string) {
    setBusy(true);
    try {
      setSource(await request<SourceDetail>(`${base}/sources/${sourceSpanId}`));
      setError("");
    } catch (error) {
      setSource(undefined);
      setError((error as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return <section aria-label="Uploaded meeting transcript Review">
    <h2>Review a past meeting</h2>
    <p className="lede">Upload a structured transcript to review what each participant said alongside the Matter you already built.</p>
    <p className="notice">Transcript wording remains attributed conversation material. A Matter source shown beside it is context only — it does not prove the participant said the cited wording and it does not turn the transcript into documentary evidence.</p>
    {error && <p className="error" role="alert">{error}</p>}
    {busy && <p aria-live="polite">Updating private meeting Review…</p>}

    <form className="card" onSubmit={createReview}>
      <label htmlFor="review-transcript-json">Transcript JSON</label>
      <textarea id="review-transcript-json" rows={12} value={draft} onChange={event => setDraft(event.target.value)} spellCheck={false} required />
      <p className="muted">Use HR_SAID, USER_ACTUALLY_SAID, or AI_SUGGESTED for each item. Optional contextCitationSourceSpanIds may reference only current canonical documentary evidence.</p>
      <button disabled={busy}>Create private Review</button>
    </form>

    <h3>Saved Reviews</h3>
    {sessions.length === 0 ? <p className="muted">No uploaded meeting Reviews are saved for this Matter yet.</p> :
      <ul className="list">{sessions.map(session => <li className="card" key={session.meetingId}>
        <p><strong>{new Date(session.startedAt).toLocaleString()}</strong> · {session.itemCount} transcript items</p>
        <p className="muted">Saved {new Date(session.createdAt).toLocaleString()}. Context at creation: {currentnessLabel(session.contextCurrentness)}.</p>
        <button type="button" className="secondary" onClick={() => void openReview(session.meetingId)} disabled={busy}>Open Review</button>
      </li>)}</ul>}

    {active && <section className="card" aria-label="Meeting Review result">
      <h3>Transcript Review</h3>
      <p className="muted">Created {new Date(active.createdAt).toLocaleString()}. Current Matter context: {currentnessLabel(active.currentContextCurrentness)}.</p>
      <ol className="list">{active.review.items.map(item => <li className="card" key={item.id}>
        <p><span className="badge">{reviewOriginLabel(item.origin)}</span> <strong>{new Date(item.startedAt).toLocaleTimeString()}</strong></p>
        <p>{item.text}</p>
        <p className="muted">This wording is attributed conversation material, not documentary fact.</p>
        <div className="row">{item.contextCitationSourceSpanIds.map(id => <button type="button" className="citation" key={id} onClick={() => void inspectSource(id)} disabled={busy}>Inspect Matter context</button>)}</div>
      </li>)}</ol>

      <h4>Context status now</h4>
      {active.analysis.contextReferences.length === 0 ? <p className="muted">No Matter context citations were attached to this Review.</p> :
        <ul className="list">{active.analysis.contextReferences.map(reference => <li key={reference.sourceSpanId}>
          <p><span className="badge">{referenceStatusLabel(reference.status)}</span> {reference.notice}</p>
          {reference.status !== 2 && <button type="button" className="citation" onClick={() => void inspectSource(reference.sourceSpanId)} disabled={busy}>View exact source</button>}
        </li>)}</ul>}

      {active.analysis.relevantUnresolvedContradictions.length > 0 && <>
        <h4>Unresolved Matter conflicts relevant to cited context</h4>
        <ul className="list">{active.analysis.relevantUnresolvedContradictions.map(item => <li className="card" key={item.contradictionId}>
          <p><strong>Unresolved contradiction</strong> · detection origin {item.detectionOrigin}</p>
          <p className="muted">CaseMesh keeps both accounts visible and does not decide which one is true.</p>
        </li>)}</ul>
      </>}

      <h4>Things to verify</h4>
      {active.analysis.followUpPrompts.length === 0 ? <p className="muted">No additional deterministic verification prompts were generated.</p> :
        <ul>{active.analysis.followUpPrompts.map(prompt => <li key={prompt}>{prompt}</li>)}</ul>}
    </section>}

    {source && <aside className="card" aria-label="Review exact source detail">
      <h3>Exact Matter source</h3>
      <p><strong>Document version:</strong> {source.citation.documentVersionId}</p>
      <p><strong>Locator:</strong> {source.citation.pageNumber ? `page ${source.citation.pageNumber}` : `characters ${source.citation.textStart}–${source.citation.textEnd}`}</p>
      <blockquote>{source.exactText}</blockquote>
      <p className="muted">Parser {source.citation.parserVersion}; extraction confidence {source.citation.extractionConfidence ?? "not recorded"}. Extraction confidence is not truth confidence.</p>
    </aside>}
  </section>;
}

function currentnessLabel(value: number) {
  return value === 0 ? "current" : "evidence processing was active";
}

function referenceStatusLabel(value: number) {
  if (value === 0) return "Current context";
  if (value === 1) return "Historical context";
  return "Context unavailable";
}
