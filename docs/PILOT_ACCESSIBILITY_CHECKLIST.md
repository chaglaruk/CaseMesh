# Closed-pilot accessibility checklist

Target: practical WCAG 2.2 AA for the authenticated employee journey. Automated axe checks are a regression gate, not a conformance claim.

For each release, test sign-in, Matter creation/list, upload/progress, timeline, evidence/source detail, disputed statements, correction, Q&A, export, quota/error states, and deletion status at desktop and 320 CSS-pixel width.

- Complete every flow using only keyboard. Verify the skip link, logical focus order, visible focus, no traps, usable tabs/buttons, and focus remaining understandable after async updates.
- Check landmarks, one clear page heading, nested headings, labels/instructions, button/link names, status and error announcements, and that source/dispute/integrity meaning is textual rather than colour-only.
- Run NVDA with Firefox or Chrome on Windows through the primary journey. Verify upload and Q&A progress, errors, source selection, and export completion are announced without excessive repetition.
- Check text and non-text contrast, 200% zoom, browser text scaling, reflow at 320 px, long opaque identifiers, and system high-contrast/forced-colours mode.
- Verify pointer targets, cancellation/retry paths, session-expiry recovery, quota-limit Problem Details, expired export handling, and reduced-motion preferences.
- Run `npm run lint`, `npm test`, `npm run build`, and the Playwright journey containing `@axe-core/playwright`; record any exception with route, rule, user impact, owner, and expiry.

Never mark the product WCAG-conformant from axe results alone. A pilot release is blocked by an inaccessible primary task unless a documented, time-bounded accessible alternative is available to the affected user.
