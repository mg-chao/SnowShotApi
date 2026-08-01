# Operational Ownership

The alert contract assigns each signal to one operational owner. The owner
triages the alert, coordinates the first mitigation, and records the outcome
in the incident timeline.

| Owner | Scope | Primary responsibility |
| --- | --- | --- |
| Infrastructure | PostgreSQL, Redis, deployment, service health, and shared capacity | Restore dependencies, verify runner and host health, and coordinate rollback. |
| Application | Operation lifecycle, leases, checkpoints, and API behavior | Inspect application logs and traces, stop unsafe processing, and ship the code fix. |
| Domain | Accounting, cost, budgets, and policy semantics | Confirm durable accounting state and approve reconciliation or policy changes. |
| API adapter | Public request validation, identity mapping, and provider request translation | Protect the public contract and investigate duplicate or malformed requests. |
| Table worker | Table recognition service lifecycle and provider capacity | Restore the Windows worker, verify mTLS, and validate worker capacity. |
