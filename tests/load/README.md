# Distributed fault/load harness

`run_harness.py` builds and starts two SnowShot API replicas with a real
PostgreSQL 17 database, Redis 8, and an OpenAI-shaped fake provider. It
verifies:

- the configured global concurrency and exact Redis queue bound across both
  replicas;
- an eight-item translation request uses four independent conversations,
  preserves result ordering, and retries only one fail-once item;
- queue overflow is rejected before provider invocation;
- concurrent requests with the same idempotency hash execute exactly once;
- a replica killed after provider dispatch leaves a renewable lease that the
  surviving replica reconciles to unknown cost; and
- every accepted operation becomes terminal with exactly one usage event and
  one aggregate contribution, with no reserved budget left behind.

Run from the repository root with Docker available:

```powershell
python tests/load/run_harness.py
```

The stack is removed after the run. Set `SNOWSHOT_KEEP_LOAD_STACK=1` to retain
it for inspection; then remove it with:

```powershell
docker compose -f tests/load/compose.yaml -p snowshot-fault down --volumes
```
