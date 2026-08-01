from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
TEST_ROOT = Path(__file__).resolve().parent
COMPOSE = TEST_ROOT / "compose.yaml"
COMPOSE_COMMAND = [
    "docker", "compose", "-f", str(COMPOSE), "-p", "snowshot-recovery"
]
ADMIN_PASSWORD = "recovery-admin"
MIGRATOR_PASSWORD = "recovery-migrator"
API_PASSWORD = "recovery-api"


def compose(
    *arguments: str,
    input_data: bytes | None = None,
    capture: bool = False,
    check: bool = True,
) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(
        [*COMPOSE_COMMAND, *arguments],
        cwd=ROOT,
        check=check,
        input=input_data,
        stdout=subprocess.PIPE if capture else None,
        stderr=subprocess.PIPE if capture else None,
    )


def psql(
    database: str,
    user: str,
    password: str,
    script: bytes,
    *variables: str,
    check: bool = True,
) -> subprocess.CompletedProcess[bytes]:
    arguments = [
        "exec", "-e", f"PGPASSWORD={password}", "-T", "postgres",
        "psql", "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-U", user, "-d", database,
    ]
    for variable in variables:
        arguments.extend(("-v", variable))
    return compose(*arguments, input_data=script, capture=True, check=check)


def openssl() -> str:
    executable = shutil.which("openssl")
    if executable:
        return executable
    candidate = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Git" / "usr" / "bin" / "openssl.exe"
    if candidate.is_file():
        return str(candidate)
    raise RuntimeError("OpenSSL is required for encrypted recovery verification.")


def crypt(payload: bytes, decrypt: bool = False) -> bytes:
    environment = os.environ.copy()
    environment["SNOWSHOT_RECOVERY_KEY"] = "ephemeral-recovery-verification-key"
    arguments = [openssl(), "enc", "-aes-256-cbc", "-pbkdf2", "-pass", "env:SNOWSHOT_RECOVERY_KEY"]
    if decrypt:
        arguments.append("-d")
    return subprocess.run(
        arguments,
        check=True,
        input=payload,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=environment,
    ).stdout


def migration_connection(database: str) -> str:
    return (
        f"Host=postgres;Port=5432;Database={database};Username=snowshot_migrator;"
        f"Password={MIGRATOR_PASSWORD}"
    )


def run_migrator(database: str) -> None:
    compose(
        "run", "--rm", "--build", "-e", "DOTNET_ENVIRONMENT=Development", "-e",
        f"ConnectionStrings__SnowShot={migration_connection(database)}",
        "migrator",
    )


def main() -> int:
    bootstrap = (ROOT / "scripts" / "database" / "Bootstrap-Roles.sql").read_bytes()
    grants = (ROOT / "scripts" / "database" / "Apply-Runtime-Grants.sql").read_bytes()
    seed = (TEST_ROOT / "seed.sql").read_bytes()
    verify = (TEST_ROOT / "verify.sql").read_bytes()
    keep = os.environ.get("SNOWSHOT_KEEP_RECOVERY_STACK") == "1"
    try:
        compose("up", "-d", "--build", "--wait", "postgres")
        psql(
            "snowshot", "postgres", ADMIN_PASSWORD, bootstrap,
            "database_name=snowshot",
            f"migrator_password={MIGRATOR_PASSWORD}",
            f"api_password={API_PASSWORD}",
        )
        run_migrator("snowshot")
        psql("snowshot", "postgres", ADMIN_PASSWORD, grants, "database_name=snowshot")
        psql("snowshot", "snowshot_api", API_PASSWORD, seed)

        ddl = psql(
            "snowshot", "snowshot_api", API_PASSWORD,
            b"CREATE TABLE snowshot.runtime_role_must_not_create(id integer);",
            check=False,
        )
        if ddl.returncode == 0:
            raise AssertionError("runtime role unexpectedly created a table")

        dump = compose(
            "exec", "-e", f"PGPASSWORD={MIGRATOR_PASSWORD}", "-T", "postgres",
            "pg_dump", "-Fc", "-U", "snowshot_migrator", "-d", "snowshot",
            capture=True,
        ).stdout
        encrypted_dump = crypt(dump)
        if encrypted_dump.startswith(b"PGDMP"):
            raise AssertionError("backup encryption left the PostgreSQL archive header visible")
        restored_dump = crypt(encrypted_dump, decrypt=True)
        if restored_dump != dump:
            raise AssertionError("encrypted backup did not decrypt byte-for-byte")

        psql(
            "snowshot", "postgres", ADMIN_PASSWORD,
            b'CREATE DATABASE snowshot_restore OWNER snowshot_migrator TEMPLATE template0;',
        )
        compose(
            "exec", "-e", f"PGPASSWORD={MIGRATOR_PASSWORD}", "-T", "postgres",
            "pg_restore", "-U", "snowshot_migrator", "-d", "snowshot_restore",
            input_data=restored_dump,
            capture=True,
        )
        psql("snowshot_restore", "postgres", ADMIN_PASSWORD, grants, "database_name=snowshot_restore")
        restored = psql("snowshot_restore", "snowshot_api", API_PASSWORD, verify)
        evidence = restored.stdout.decode("utf-8").strip()
        if evidence != "1|1|1|1|1|1|10|0|20|0|1|1|2":
            raise AssertionError(f"restored accounting evidence is invalid: {evidence!r}")
        run_migrator("snowshot_restore")
        print("Encrypted PostgreSQL backup, restore, accounting, and least-privilege harness passed.")
        return 0
    finally:
        if not keep:
            compose("down", "--volumes", "--remove-orphans", check=False)


if __name__ == "__main__":
    sys.exit(main())
