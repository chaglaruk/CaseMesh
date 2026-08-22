import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test("authenticated synthetic Matter journey preserves provenance and corrections",async({page})=>{
  await page.goto("/sign-in");
  await page.getByRole("button",{name:"Continue securely"}).click();
  await expect(page).toHaveURL(/\/matters/);
  await page.getByLabel("Neutral Matter title").fill("Synthetic workplace evidence review");
  await page.getByLabel("Jurisdiction").fill("England and Wales");
  const createMatter=page.getByRole("button",{name:"Create Matter"});
  try {
    await expect(createMatter).toBeEnabled({timeout:15_000});
  } catch (error) {
    console.log("Matter page alerts:",await page.getByRole("alert").allTextContents());
    console.log("Browser cookie names:",(await page.context().cookies()).map(cookie=>cookie.name));
    throw error;
  }
  await createMatter.click();
  await expect(page.getByRole("heading",{name:"Matter workspace"})).toBeVisible();
  await page.getByLabel("Evidence file").setInputFiles({name:"synthetic-note.txt",mimeType:"text/plain",buffer:Buffer.from("On 14 April, the employer stated that twelve absence days were recorded.")});
  await page.getByRole("button",{name:"Upload and process"}).click();
  await expect(page.getByText(/Document author.*asserted/)).toBeVisible({timeout:60_000});
  await page.getByRole("button",{name:"View source"}).click();
  await expect(page.getByLabel("Source citation detail")).toContainText("twelve absence days");

  await page.getByRole("button",{name:"Prepare"}).click();
  await expect(page.getByRole("heading",{name:"Prepare for a meeting"})).toBeVisible();
  await expect(page.getByLabel("Meeting preparation")).toContainText("canonical Matter evidence state");
  await expect(page.getByLabel("Meeting preparation")).toContainText(/twelve absence days/i);
  await page.getByLabel("Meeting preparation").getByRole("button",{name:"View exact source"}).first().click();
  await expect(page.getByLabel("Source citation detail")).toContainText("twelve absence days");
  const prepareAccessibility=await new AxeBuilder({page}).analyze();
  expect(prepareAccessibility.violations).toEqual([]);

  await page.getByRole("button",{name:"Evidence",exact:true}).click();
  page.on("dialog",dialog=>dialog.accept("ten absence days were recorded"));
  await page.getByRole("button",{name:"Correct with audit trail"}).click();
  await expect(page.getByText(/ten absence days were recorded/)).toBeVisible();
  const tabHeadings={Timeline:"Chronology",People:"People",Disputed:"Disputed statements"};
  for(const [tab,heading] of Object.entries(tabHeadings)){await page.getByRole("button",{name:tab}).click();await expect(page.getByRole("heading",{name:heading})).toBeVisible();}
  await page.getByRole("button",{name:"Questions"}).click();
  await expect(page.getByRole("heading",{name:"Questions about your evidence"})).toBeVisible();
  await page.getByLabel("One Matter-scoped factual question").fill("What does the evidence say about absence days?");
  await page.getByRole("button",{name:"Ask your evidence"}).click();
  await expect(page.getByLabel("What your evidence shows")).toContainText(/absence days/i);
  await expect(page.getByLabel("What your evidence shows")).toContainText(/generation time/i);
  await page.getByRole("button",{name:/View exact source/}).first().click();
  await expect(page.getByLabel("Source citation detail")).toContainText("absence days");
  await page.getByRole("button",{name:"New thread"}).click();
  await expect(page.getByLabel("One Matter-scoped factual question")).toHaveValue("");
  await expect(page.getByLabel("What your evidence shows")).toHaveCount(0);
  await expect(page.getByLabel("Source citation detail")).toContainText("Select a citation");
  await page.route("**/questions/ask",async route=>{await new Promise(resolve=>setTimeout(resolve,500));await route.continue().catch(()=>undefined);});
  await page.getByLabel("One Matter-scoped factual question").fill("What does the evidence say about absence days?");
  await page.getByRole("button",{name:"Ask your evidence"}).click();
  await page.getByRole("button",{name:"New thread"}).click();
  await page.waitForTimeout(750);
  await expect(page.getByLabel("What your evidence shows")).toHaveCount(0);
  await expect(page.getByLabel("Source citation detail")).toContainText("Select a citation");
  await page.unroute("**/questions/ask");

  await page.getByRole("button",{name:"Prepare"}).click();
  await expect(page.getByLabel("Meeting preparation")).toContainText("assertion without documentary source");
  await expect(page.getByLabel("Meeting preparation")).toContainText("corrected history review");
  await expect(page.getByLabel("Meeting preparation")).not.toContainText("ten absence days were recorded");
  const correctedPrepareAccessibility=await new AxeBuilder({page}).analyze();
  expect(correctedPrepareAccessibility.violations).toEqual([]);

  await page.getByRole("button",{name:"Overview"}).click();
  const accessibility=await new AxeBuilder({page}).analyze();
  expect(accessibility.violations).toEqual([]);
  const download=page.waitForEvent("download");
  await page.getByRole("button",{name:"Download export"}).click();
  expect((await download).suggestedFilename()).toContain("casemesh");
});
