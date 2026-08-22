import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const page=readFileSync(join(process.cwd(),"app/matters/[matterId]/page.tsx"),"utf8");
const proxy=readFileSync(join(process.cwd(),"proxy.ts"),"utf8");
const layout=readFileSync(join(process.cwd(),"app/layout.tsx"),"utf8");
const signIn=readFileSync(join(process.cwd(),"app/sign-in/page.tsx"),"utf8");
const styles=readFileSync(join(process.cwd(),"app/styles.css"),"utf8");
describe("Matter workspace contract",()=>{
  it.each(["overview","timeline","evidence","people","disputed","questions","workplace"])("includes the %s view",view=>expect(page).toContain(`\"${view}\"`));
  it("supports streamed multipart upload and durable polling",()=>{expect(page).toContain("new FormData");expect(page).toContain("/jobs/");});
  it("opens exact citation details",()=>{expect(page).toContain("Source citation");expect(page).toContain("extractedText");expect(page).toContain("documentVersionId");expect(page).toContain("View exact source");});
  it("supports audited correction",()=>expect(page).toContain("/corrections"));
  it("supports private export download",()=>expect(page).toContain("/exports"));
  it("uses valid toggle-button accessibility state",()=>{expect(page).toContain("aria-pressed");expect(page).not.toContain("aria-selected");expect(styles).toContain('[aria-pressed="true"]');expect(styles).not.toContain('[aria-selected="true"]');});
  it("never injects raw evidence as HTML",()=>expect(page).not.toContain("dangerouslySetInnerHTML"));
  it("supports bounded Matter Q&A and abortable thread reset",()=>{expect(page).toContain("/questions/ask");expect(page).toContain("maxLength={1000}");expect(page).toContain("New thread");expect(page).toContain("What your evidence shows");expect(page).toContain("AbortController");expect(page).toContain("qaRequest.current?.abort()");});
  it("keeps external guidance separate and factual gaps navigable",()=>{expect(page).toContain("External legal guidance is a separate future surface");expect(page).toContain("Factual gaps");expect(page).toContain("Open {gap.route} view");});
  it("labels extraction confidence separately from truth",()=>expect(page).toContain("Extraction confidence is not truth confidence"));
  it("applies a nonce-based executable-content policy",()=>{expect(proxy).toContain("script-src 'self' 'nonce-${nonce}' 'strict-dynamic'");expect(proxy).toContain("default-src 'self'");expect(proxy).toContain('response.headers.set("Content-Security-Policy", policy)');expect(layout).toContain('dynamic = "force-dynamic"');});
  it("uses real OIDC outside the explicit test build",()=>{expect(signIn).toContain('NEXT_PUBLIC_CASEMESH_TEST_AUTH');expect(signIn).toContain('href="/api/auth/sign-in"');});
});
