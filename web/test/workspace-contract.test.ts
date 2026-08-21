import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const page=readFileSync(join(process.cwd(),"app/matters/[matterId]/page.tsx"),"utf8");
describe("Matter workspace contract",()=>{
  it.each(["overview","timeline","evidence","people","disputed","questions"])("includes the %s view",view=>expect(page).toContain(`\"${view}\"`));
  it("supports streamed multipart upload and durable polling",()=>{expect(page).toContain("new FormData");expect(page).toContain("/jobs/");});
  it("opens exact citation details",()=>{expect(page).toContain("Source citation");expect(page).toContain("extractedText");expect(page).toContain("documentVersionId");});
  it("supports audited correction",()=>expect(page).toContain("/corrections"));
  it("supports private export download",()=>expect(page).toContain("/exports"));
  it("uses valid toggle-button accessibility state",()=>{expect(page).toContain("aria-pressed");expect(page).not.toContain("aria-selected");});
  it("never injects raw evidence as HTML",()=>expect(page).not.toContain("dangerouslySetInnerHTML"));
  it("labels extraction confidence separately from truth",()=>expect(page).toContain("Extraction confidence is not truth confidence"));
});
