# Code review workflow

CaseMesh uses CodeRabbit as an independent PR review gate, but avoids redundant review churn.

## Default flow

1. Keep feature PRs as drafts while implementation and CI are still changing.
2. Batch implementation fixes before requesting independent review.
3. Mark the PR ready only after required local validation and CI gates are green.
4. Allow one initial CodeRabbit review. Repository configuration disables automatic incremental re-reviews on every later push.
5. Verify and batch all valid material findings into as few fix commits as practical.
6. Reply to existing review threads with the fix and let CodeRabbit verify/resolve them where possible.
7. Before requesting another review, use `@coderabbitai rate limit` to inspect available quota without consuming a review.
8. Request another incremental/full review only when the post-review changes are materially broad enough to justify it.
9. Merge only after required CI/security gates are green and no material review finding remains unresolved.

`@coderabbitai full review` is preferred when a PR changed substantially before its first independent review. `@coderabbitai review` is preferred for a targeted incremental pass.

This policy reduces review-rate-limit churn; it does not replace deterministic tests, security scans, or review of high-risk changes.
