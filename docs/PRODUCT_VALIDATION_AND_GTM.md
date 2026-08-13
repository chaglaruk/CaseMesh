# Product Validation and Go-to-Market Plan

Date: 2026-08-13

## Goal

Prove four things before large-scale build-out:

1. users will trust the product with real workplace evidence;
2. the Case Brain is materially more useful than generic chat/document summarization;
3. professionals save measurable intake/chronology time;
4. there is a viable price and acquisition path.

## Validation program

### Employee interviews

Target: 30–40 people who have experienced a grievance, disciplinary, capability, adjustment, dismissal, Acas or Tribunal process.

Ensure at least 10 participants have disability/sickness/capability/adjustment experience.

Do not lead with feature descriptions. Ask participants to reconstruct what they actually did with emails, documents, dates, notes and professional advice.

Questions should establish:

- how evidence accumulated;
- what became difficult to remember;
- whether they created a timeline manually;
- what they sent to a solicitor/union/Acas;
- what they feared losing;
- what they paid for;
- what they would not upload to a new product;
- what would make them trust it;
- when the problem felt urgent.

### Solicitor interviews

Target: 15–20 claimant-side employment solicitors.

Measure:

- time spent on intake;
- time spent building chronology;
- common missing information;
- repeated client-document problems;
- how evidence is currently received;
- what a useful pre-consultation pack contains;
- what output would create more work rather than save it;
- acceptable delivery format;
- realistic per-case or monthly willingness to pay.

## Concierge Case Brain

Target: 20 controlled real cases.

The product team should manually supervise extraction and corrections to learn the failure modes before automating everything.

For each case measure:

- evidence count/pages;
- time to first useful chronology;
- number of extraction corrections;
- number of contradictions discovered;
- number of missing-evidence prompts accepted as useful;
- whether the user adds more evidence after first value;
- support minutes;
- trust/privacy objections;
- export usage;
- solicitor reaction when available.

## Activation event

The preferred activation event is:

> User uploads at least five evidence items and confirms or corrects the first generated chronology.

The product should demonstrate value before asking the user to engage in open-ended chat.

## Aha moment

The intended reaction is:

> I can finally see what happened, who said what, what proves it, what conflicts and what is missing.

If users instead value only drafting, the product hypothesis should be reconsidered.

## Validation thresholds

Continue if most of these are met:

- >=60% of qualified users upload five or more evidence items;
- >=50% add more evidence after the first chronology;
- >=70% say the chronology is materially more useful than a generic chat summary;
- >99% of displayed evidence links resolve correctly;
- ordinary support can plausibly fall below 30 minutes per paid case;
- >=60% of solicitor testers say the pack saves meaningful time;
- >=30% of solicitor testers demonstrate willingness to pay.

Pivot toward professional/B2B if consumers value the product but resist meaningful direct pricing while firms show clear ROI.

Stop or substantially rethink if users will not upload sensitive material, the correction burden destroys trust, generic AI is judged equivalent, or professionals rebuild the entire pack manually.

## Pricing experiments

Treat pricing as an experiment, not a design debate.

### Consumer hypotheses

After the relevant commercial gates are satisfied, test case-oriented prices rather than permanent subscription first.

Candidate anchors:

- £99;
- £119;
- £149.

A Case Pass can cover a defined period, with extensions for long-running disputes.

### Professional hypotheses

Test:

- per-case structured intake/chronology fee;
- monthly case allocation for small firms;
- later organization pricing for larger partners.

The professional value proposition should be measured in time saved, not AI novelty.

## Initial acquisition strategy

Users search for a problem, not for “AI evidence operating systems”.

Build acquisition around high-intent workflow clusters:

- grievance preparation;
- grievance appeal;
- disciplinary meeting;
- capability and sickness absence;
- reasonable adjustments;
- Occupational Health disputes;
- dismissal;
- Acas Early Conciliation;
- settlement preparation;
- Employment Tribunal chronology/evidence;
- organizing evidence for an employment solicitor.

## Product-led loop

Preferred loop:

`useful guide/tool -> upload five documents -> partial source-linked chronology -> fuller Case Brain -> professional export -> solicitor sees value -> solicitor sends future clients back through intake`

This loop can create both consumer acquisition and professional distribution.

## Paid acquisition

Do not assume paid Google Search works at launch. First establish conversion and contribution margin from organic/referral traffic.

High-cost paid traffic can quickly destroy economics for a low-hundreds-of-pounds case product.

## Content principles

Employment content should be:

- date-stamped;
- jurisdiction-specific;
- source-linked;
- updated when rules change;
- factual rather than sensational.

Avoid “fight HR” positioning and content that maximizes accusations. The brand should optimize for clarity, evidence and proportionate action.

## Brand validation

Keep `HR Companion` as a working product/repository name until naming tests are complete.

Test whether prospective employees interpret “HR” as employer-side software. Compare against evidence/navigation-oriented names before spending materially on brand identity.

## First 8-week validation cadence

### Weeks 1–2

- finalize interview scripts;
- recruit employee and solicitor participants;
- create prototype screens;
- prepare synthetic/demo case;
- define analytics events and success metrics.

### Weeks 3–4

- run first employee interviews;
- run first solicitor interviews;
- test prototype;
- refine the Case Brain terminology and timeline UI.

### Weeks 5–6

- run controlled concierge cases;
- generate professional handover packs;
- time solicitor review versus their normal intake;
- test trust/privacy messaging.

### Weeks 7–8

- consolidate corrections/failure modes;
- run pricing-intent tests;
- choose initial commercial channel;
- lock the first paid-pilot scope;
- update the engineering roadmap from observed evidence.

## Evidence for a £1m+ business hypothesis

The strongest proof is not downloads or compliments.

Look for this combination:

- users repeatedly build real cases;
- users continue adding evidence;
- source-link accuracy remains extremely high;
- a meaningful case price is accepted;
- support remains bounded;
- solicitors report 30–60+ minutes saved on a meaningful share of cases;
- firms pay repeatedly;
- users or firms refer additional cases;
- the product remains clearly more useful than moving the files back into a generic AI chat.
