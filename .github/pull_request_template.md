## Title format
`type(scope): summary (TASK-ID)`

Example: `feat(domain): add Money value object (BE-002.2)`

## What and why
<!-- 1-3 sentences -->

## Related task and requirements
- ClickUp: <!-- task link -->
- PRP requirements: <!-- e.g. FR-MON-01, or N/A with a reason -->

## Type of change
- [ ] feature
- [ ] fix
- [ ] test
- [ ] docs
- [ ] refactor
- [ ] ci

## Scope
- [ ] Domain
- [ ] Application
- [ ] Infrastructure / database
- [ ] API / contracts
- [ ] Mobile
- [ ] Shared configuration / documentation / CI

## Depends on
<!-- Related PRs or tasks that must be completed first, or "none" -->

## Definition of Done
- [ ] Implements the linked ClickUp task and applicable PRP acceptance criteria
- [ ] Build and relevant tests pass; changed business rules include boundary and negative cases
- [ ] Monetary changes preserve exact amounts, balanced and immutable journals, atomic posting and idempotency where applicable
- [ ] API changes update OpenAPI contracts, validation, authorization and ProblemDetails responses where applicable
- [ ] Database changes include the required mappings, migrations and integration tests where applicable
- [ ] Documentation and configuration examples are updated where applicable
- [ ] No secrets, real personal data or sensitive log output are introduced
- [ ] Branch is up to date with its target branch (`development` for features, `main` for releases), review comments resolved
- [ ] Configured CI checks pass; domain coverage meets the PRP target when domain logic changes

<!-- Explain non-applicable or unverified items in How to test; do not mark unverified checks as passed. -->

## How to test
<!-- Commands, endpoint calls or test names, expected results, and anything not verified.
     Explain items that do not apply to this change. -->

## Reviewers
- Team process requires a review and an `Approved` decision from someone other than the author
- GitHub does not enforce an approval count: missing approval does not technically block merge
- QA Champion checks the Definition of Done before merge
