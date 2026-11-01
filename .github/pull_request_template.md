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
- [ ] Implements the requirement and its acceptance criteria
- [ ] Business rules covered by unit tests, all tests pass
- [ ] Endpoint visible and documented in Swagger
- [ ] Validation and error handling return correct status codes (ProblemDetails)
- [ ] No secrets, no real personal data, no sensitive data in logs
- [ ] Branch is up to date with the latest `main`, review comments resolved
- [ ] Checks are green (build, tests, coverage, secret scan, title)

## How to test
<!-- Commands, endpoint calls or test names, expected results, and anything not verified.
     Explain items that do not apply to this change. -->

## Reviewers
- Author is not the approver
- QA Champion checks the Definition of Done before merge
