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
  page.on("dialog",dialog=>dialog.accept("ten absence days were recorded"));
  await page.getByRole("button",{name:"Correct with audit trail"}).click();
  await expect(page.getByText(/ten absence days were recorded/)).toBeVisible();
  for(const tab of ["Timeline","People","Disputed","Questions"]){await page.getByRole("button",{name:tab}).click();await expect(page.getByRole("heading",{name:tab==="Questions"?"Open factual questions":tab})).toBeVisible();}
  await page.getByRole("button",{name:"Overview"}).click();
  const accessibility=await new AxeBuilder({page}).analyze();
  expect(accessibility.violations).toEqual([]);
  const download=page.waitForEvent("download");
  await page.getByRole("button",{name:"Download export"}).click();
  expect((await download).suggestedFilename()).toContain("casemesh");
});
