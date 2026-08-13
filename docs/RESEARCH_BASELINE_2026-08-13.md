# Commercialization Research Baseline — 2026-08-13

## Purpose

This document captures the strategic conclusions adopted from the 58-page deep-research report **“HR Companion: Commercialization & Product Deep Research for the UK”** dated 13 August 2026. It is a planning baseline, not legal advice. Any legal/regulatory assertion that affects product scope or monetization must be checked against current primary sources and specialist counsel before launch.

## Adopted strategic conclusion

HR Companion should not be commercialized primarily as an “AI employment lawyer”, grievance generator, generic employment-law chatbot, or live meeting assistant.

The preferred category is:

> **An evidence operating system for workplace disputes: a persistent, source-grounded Case Brain that turns a disorganized employment history into a verified chronology, evidence map, disputed-facts map, missing-evidence list and professional-ready case file.**

The initial product wedge is therefore **evidence organization and provenance**, not legal conclusion generation.

## Why this direction

The research found that general-purpose AI can already summarize documents, draft grievances, discuss employment law, and maintain project-level context. Those raw model capabilities are not a durable moat.

The durable product opportunity is system behavior and data integrity:

- canonical structured case state independent of chat;
- immutable originals and hashes;
- atomic assertions linked to exact source spans;
- explicit attribution of who said what;
- contradiction relationships instead of silently choosing a narrative;
- deterministic procedural/deadline logic where appropriate;
- incremental updates when new evidence arrives;
- audit history of corrections and model changes;
- professional exports with direct source links.

## Priority market hypothesis

The employee mission remains central, but the research identified a potentially stronger first-revenue route through claimant-side employment solicitors.

Working commercial hypothesis:

1. Employees use a secure case-preparation workspace to organize evidence.
2. The product generates a neutral, source-linked handover pack.
3. Regulated professionals receive a cleaner intake and spend less time reconstructing chronology.
4. The professional workflow provides measurable ROI and can fund the employee-facing experience.
5. Personalized legal advice and claims-management features are added only after the regulatory structure is confirmed.

## Critical regulatory gate: claims-management perimeter

The research identified this as the main commercial go/no-go question.

FCA consumer guidance states that claims-management companies must be authorized when providing services concerning **employment matters**, including unfair dismissal, unless regulated by an applicable legal regulator. FCA PERG guidance describes controlled activity broadly enough to include identifying claims and **advice, investigation or representation** in relation to employment-related claims.

Primary references:

- FCA — Using claims management companies: https://www.fca.org.uk/consumers/using-claims-management-companies
- FCA Handbook PERG 2.7: https://handbook.fca.org.uk/handbook/perg2/perg2s7
- FCA claims-management regulation: https://www.fca.org.uk/firms/claims-management-regulation

**Product rule:** do not charge a B2C user for claims-oriented personalized advice, merits assessment, claim investigation, claim strategy, representation, or similar functionality until a specialist written perimeter opinion establishes the lawful operating model.

Possible structures to evaluate with counsel:

- tightly limited evidence-management product;
- B2C product delivered with an FCA/SRA-regulated partner;
- FCA-authorized claims-management structure;
- B2B SaaS supplied to regulated employment solicitors;
- combinations of the above.

## Reserved legal activities and settlement boundary

The Legal Services Act 2007 reserves specific legal activities, including conduct of litigation and rights of audience. The product must not assume that technical ability to file, serve, manage or represent makes those activities lawful for an unregulated provider.

Settlement agreements have a clear human-professional boundary. Acas states that legal validity requires advice from a relevant independent adviser, that adviser must be insured, and the adviser must be identified in the agreement.

Reference:

- Acas — Settlement agreements: https://www.acas.org.uk/settlement-agreements

**Product rule:** AI may organize, compare and explain settlement documents, but it must never represent itself as satisfying the statutory independent-advice requirement.

## Data-protection baseline

The platform will routinely process health, disability, trade-union and other special-category information, plus allegations and third-party personal data.

ICO guidance requires an Article 6 lawful basis plus a separate Article 9 condition for special-category data. The Article 9(2)(f) legal-claims condition may be relevant to some processing, but necessity and proportionality must be assessed purpose-by-purpose rather than assumed globally.

Primary references:

- ICO special-category data: https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/lawful-basis/special-category-data/
- ICO legal-claims condition: https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/lawful-basis/special-category-data/what-are-the-conditions-for-processing/

A DPIA is treated as mandatory for project planning before paid launch even where counsel ultimately determines a particular processing operation is not formally mandatory.

### Data (Use and Access) Act 2025

The deep-research report concentrated on UK GDPR and the Data Protection Act 2018. The product baseline must additionally track the **Data (Use and Access) Act 2025 (DUAA)**, whose data-protection provisions are now in force. In particular, the ICO states that organizations must provide a clear data-protection complaints process and handle such complaints under the new requirements.

Primary references:

- ICO DUAA overview: https://ico.org.uk/about-the-ico/what-we-do/legislation-we-cover/data-use-and-access-act-2025/the-data-use-and-access-act-2025-what-does-it-mean-for-organisations/
- ICO complaints requirement: https://ico.org.uk/about-the-ico/media-centre/news-and-blogs/2026/06/new-data-protection-complaints-law-now-in-force/

**Planning correction:** future compliance documents should refer to the current UK data-protection framework as amended by DUAA, rather than treating UK GDPR/DPA 2018 as static.

## Employment-law versioning baseline

Employment law must be date-versioned. Government guidance updated in July 2026 states that the Employment Rights Act 2025 is being implemented in phases, including:

- Employment Tribunal time limits moving from three months to six months on 1 October 2026 for most claims;
- ordinary unfair-dismissal qualifying service reducing from two years to six months from 1 January 2027;
- further reforms continuing through 2027.

Primary reference:

- GOV.UK implementation timeline: https://www.gov.uk/government/publications/implementing-the-plan-to-make-work-pay-and-employment-rights-act/plan-to-make-work-pay-and-employment-rights-act-timeline-update

**Product rule:** no static “UK employment-law prompt”. Legal authorities require jurisdiction, relevant event date, effective law version and source snapshot.

## Recommended evidence ontology

The research recommended a normalized Case Brain containing at least:

- case;
- person;
- organisation;
- document;
- document version;
- source span;
- assertion;
- event;
- assertion/event links;
- communication;
- employment term;
- absence/health event;
- request;
- response;
- issue;
- contradiction;
- deadline;
- legal authority;
- task;
- objective;
- analysis node;
- append-only audit event.

The central entity is **assertion**, not a free-text “fact”.

## Epistemic rules adopted from research

1. A source statement does not automatically become a case fact.
2. Every meaningful assertion must preserve its source, author/attribution and dispute state.
3. Employer, employee, third-party and AI statements are separate classes.
4. Extraction confidence is not truth confidence.
5. Do not expose a single numeric “truth score”.
6. Contradictory assertions coexist until resolved.
7. AI inference is a separate record type from documentary evidence.
8. Corrections are append-only and auditable.
9. Every displayed factual conclusion should resolve through `statement -> assertion/event -> source span -> document version -> original object hash`.
10. The vector index is a retrieval accelerator, never the source of truth.

## MVP adopted from research

The first commercial-capable product should focus on:

1. secure case and user model;
2. immutable original-file storage and SHA-256 hashing;
3. PDF/DOCX/EML/image ingestion;
4. OCR fallback;
5. source-span extraction;
6. people/entity resolution;
7. atomic assertion/event model;
8. source-linked chronology;
9. contradiction/dispute/correction UX;
10. professional-ready evidence/chronology export.

## Deliberately deferred

Do not put these on the first commercial critical path:

- live meeting copilot;
- whole-mailbox Gmail ingestion;
- corporate Outlook ingestion;
- autonomous ET1 filing;
- win-probability scores;
- automated settlement negotiation;
- compensation predictors;
- dedicated graph database;
- self-hosted foundation model;
- international employment-law support;
- employer-side HR analytics.

## Pricing hypothesis to validate

The research favors case-based pricing over permanent subscription.

Initial B2C hypotheses, to test only after regulatory clearance:

- £99 / £119 / £149 Case Pass variants;
- roughly 90 days of full case access;
- low-cost extensions for long-running disputes;
- separately priced regulated professional review.

Professional hypotheses:

- per-case structured intake/chronology fee;
- small-firm monthly case allocation;
- later union/insurer case-volume contracts.

These are experiments, not adopted prices.

## Validation thresholds adopted as working targets

- >=60% of activated users upload at least five evidence items;
- >=50% return to add evidence after first chronology;
- >=70% report source-linked chronology materially better than a chat summary;
- >99% displayed source-link validity;
- 100% legal-citation existence when legal authority is enabled;
- <30 minutes ordinary human support per paid case;
- >=60% of lawyer interviewees say the handover pack saves meaningful time;
- >=30% of lawyer testers demonstrate willingness to pay;
- sustainable gross margin target >65–70% including ordinary support.

## Strategic decision

Build the evidence infrastructure first. Treat the existing live-meeting application as useful prototype work, not the new commercial product's critical path. Preserve reusable ingestion, provenance, safety and evaluation logic, but re-architect around a multi-user secure case platform and professional handover workflow.
