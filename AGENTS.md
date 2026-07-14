# Agent Instructions

## Workroom execution

- Root agent: use Sol 5.6 High. If unavailable, use the closest available Sol/Terra model at high reasoning, then the closest available frontier model. Report the exact fallback used.
- Delegates: use Luna Extra High. If unavailable, use Luna High, then the closest available model at high reasoning. Report the exact fallback used.
- Treat delegation timeouts or failures separately from model unavailability. After a bounded wait, the root agent continues, owns integration and QA, and reports when no delegated result was available for review.

## Agent skills

### Issue tracker

Issues and PRDs are tracked in GitHub Issues for `valence-works/Groundwork`; external PRs are not a triage request surface. See `docs/agents/issue-tracker.md`.

### Triage labels

This repo uses the default engineering-skills triage labels. See `docs/agents/triage-labels.md`.

### Domain docs

This repo uses a single-context domain-doc layout. See `docs/agents/domain.md`.
