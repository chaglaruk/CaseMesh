# ADR 0004: CodeRabbit review-efficiency policy

- Status: Accepted
- Date: 2026-08-14

## Context

CaseMesh feature PRs are increasingly large and review-heavy. CodeRabbit PR reviews are rate-limited per developer and automatic incremental reviews consume the same PR-review allowance as manual reviews. Re-reviewing every push during an active fix cycle can therefore delay final review without materially improving quality.

## Decision

Repository-level `.coderabbit.yaml` keeps automatic review enabled for eligible non-draft PRs, but disables automatic incremental review after subsequent pushes.

The working review sequence is:

1. develop in a draft PR while CI and local validation are still changing;
2. mark the PR ready only after implementation is stable and required CI is green;
3. allow one automatic CodeRabbit review (or trigger one manual full review if no review starts);
4. batch valid review fixes rather than pushing one fix at a time;
5. use replies/verification on existing threads where possible;
6. request another incremental/full review only when the material scope changed enough to justify spending another review allowance;
7. merge only when material threads are resolved and required CI/security gates are green.

Before manually triggering another review, agents should prefer `@coderabbitai rate limit` to inspect remaining quota without consuming a review.

## Consequences

This reduces redundant review churn while keeping CodeRabbit as an independent review gate. It does not weaken CI, security scans, deterministic tests, or human/agent review requirements. A material post-review rewrite may still require another CodeRabbit review.
