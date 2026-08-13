# CaseMesh

CaseMesh is being developed as a source-grounded evidence platform for workplace disputes.

Canonical domain: **casemesh.dev**

Core flow:

`evidence -> provenance -> Matter/Case Brain -> chronology -> disputed facts -> correction -> professional handover`

## Product layers

- **CaseMesh Core** — reusable Matter/Case Brain and evidence provenance.
- **CaseMesh Work** — first commercial vertical for workplace disputes.
- **CaseMesh Professional** — professional intake and handover.
- **CaseMesh Live** — later meeting preparation, transcript review and live assistance built on the Matter Brain.

Read the commercial strategy in `docs/BRAND_AND_SCOPE.md`, `docs/COMMERCIAL_MASTER_PLAN.md`, `docs/CASE_BRAIN_SPEC.md`, `docs/PRODUCT_VALIDATION_AND_GTM.md`, and `docs/CODEX_HANDOFF.md`.

## Current solution

The repository/project/namespace rename is complete. Current projects use `CaseMesh.*` names.

The existing Windows meeting prototype remains as the later CaseMesh Live track. Its runtime gates are documented in `docs/STATUS.md` and `docs/GATES.md`; they do not block the commercial evidence-platform roadmap.

## Local development

```powershell
dotnet restore .\CaseMesh.slnx
dotnet build .\CaseMesh.slnx -c Release
dotnet test .\CaseMesh.slnx -c Release --no-build
```

## Repository rule

Do not commit real customer case material or secrets. Use synthetic fixtures only.
