import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const page=readFileSync(join(process.cwd(),"app/matters/[matterId]/page.tsx"),"utf8");
const proxy=readFileSync(join(process.cwd(),"proxy.ts"),"utf8");
const layout=readFileSync(join(process.cwd(),"app/layout.tsx"),"utf8");
const signIn=readFileSync(join(process.cwd(),"app/sign-in/page.tsx"),"utf8");
const styles=readFileSync(join(process.cwd(),"app/styles.css"),"utf8");
describe("Matter workspace contract",()=>{
  it.each(["overview","timeline","evidence","people","disputed","questions","prepare","workplace"])("includes the %s view",view=>expect(page).toContain(`\"${view}\"`));
  it("supports streamed multipart upload and durable polling",()=>{expect(page).toContain("new FormData");expect(page).toContain("/jobs/");});
  it("opens exact citation details",()=>{expect(page).toContain("Source citation");expect(page).toContain("extractedText");expect(page).toContain("documentVersionId");expect(page).toContain("View exact source");});
  it("supports audited correction",()=>expect(page).toContain("/corrections"));
  it("supports private export download",()=>expect(page).toContain("/exports"));
  it("uses valid toggle-button accessibility state",()=>{expect(page).toContain("aria-pressed");expect(page).not.toContain("aria-selected");expect(styles).toContain('[aria-pressed="true"]');expect(styles).not.toContain('[aria-selected="true"]');});
  it("never injects raw evidence as HTML",()=>expect(page).not.toContain("dangerouslySetInnerHTML"));
  it("supports bounded Matter Q&A and invalidates stale threads",()=>{expect(page).toContain("/questions/ask");expect(page).toContain("maxLength={1000}");expect(page).toContain("New thread");expect(page).toContain("What your evidence shows");expect(page).toContain("currentnessNotice");expect(page).toContain("AbortController");expect(page).toContain("qaRequest.current?.abort()");expect(page).toContain('if(next==="questions")setQa(undefined)');});
  it("keeps external guidance separate and factual gaps navigable",()=>{expect(page).toContain("External legal guidance is a separate future surface");expect(page).toContain("Factual gaps");expect(page).toContain("Open {gap.route} view");});
  it("provides canonical evidence-grounded meeting preparation",()=>{expect(page).toContain("Prepare for a meeting");expect(page).toContain("Evidence points to review");expect(page).toContain("Unresolved conflicts");expect(page).toContain("Questions to clarify");expect(page).toContain("Evidence to have ready");expect(page).toContain("does not predict a legal outcome");});
  it("renders temporal disputes and alias provenance explicitly",()=>{expect(page).toContain("alleged event time:");expect(page).toContain("alias.provenanceStatus");expect(page).toContain("alias.sourceSpanIds");expect(page).toContain("View source for alias");});
  it("renders full Prepare evidence and correction metadata",()=>{expect(page).toContain("Asserted at:");expect(page).toContain("point.integrity");expect(page).toContain("point.extractionConfidence");expect(page).toContain('event.endTime?` – ${event.endTime}`');expect(page).toContain("record.origin");expect(page).toContain("record.assertionClass");expect(page).toContain("record.extractionConfidence");expect(page).toContain("record.status");expect(page).toContain("record.verification");});
  it("renders full unresolved-dispute assertion metadata",()=>{expect(page).toContain("assertion.assertedAt");expect(page).toContain("assertion.integrity");expect(page).toContain("assertion.extractionConfidence");expect(page).toContain("assertion.dispute");expect(page).toContain("assertion.verification");});
  it("separates correction-history source relationships",()=>{expect(page).toContain("historicalQualifyingSourceSpanIds");expect(page).toContain("historicalContradictingSourceSpanIds");expect(page).toContain("View historical supporting source for original record");expect(page).toContain("View historical qualifying/context source");expect(page).toContain("View historical contradicting/date-mismatched source");});
  it("labels extraction confidence separately from truth",()=>expect(page).toContain("Extraction confidence is not truth confidence"));
  it("applies a nonce-based executable-content policy",()=>{expect(proxy).toContain("script-src 'self' 'nonce-${nonce}' 'strict-dynamic'");expect(proxy).toContain("default-src 'self'");expect(proxy).toContain('response.headers.set("Content-Security-Policy", policy)');expect(layout).toContain('dynamic = "force-dynamic"');});
  it("uses real OIDC outside the explicit test build",()=>{expect(signIn).toContain('NEXT_PUBLIC_CASEMESH_TEST_AUTH');expect(signIn).toContain('href="/api/auth/sign-in"');});
});
