# Multi-milestone Codex execution policy

When a Codex task explicitly authorizes multiple sequential milestones, Codex may continue through them in one execution as long as each milestone is completed through its own acceptance and merge gate before the next begins.

Rules:

- each product milestone gets its own issue and PR unless the issue explicitly authorizes a combined PR;
- do not begin the next milestone on an unmerged feature branch;
- after each merge, update local `main` from `origin/main` and branch again from the merged head;
- preserve all CI/security/review gates;
- do not bypass a blocked milestone merely to make progress on a later one;
- if the execution/runtime limit is reached, stop with a precise handoff at the current milestone boundary;
- routine actions such as branch creation, commit, push, PR creation, Ready transition, CI fixes, CodeRabbit interaction, merge, issue closure and local-main synchronization are pre-authorized when their documented gates are satisfied.

For CodeRabbit efficiency, follow `docs/CODE_REVIEW_WORKFLOW.md` and avoid automatic/redundant re-review churn.
