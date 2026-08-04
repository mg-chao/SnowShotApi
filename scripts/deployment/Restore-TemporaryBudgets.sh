#!/bin/sh
set -eu

deployment_root=/opt/snowshot-v2
active_policy="$deployment_root/runtime/appsettings.Production.json"
restore_policy="$deployment_root/runtime/policy-revision-6.json"
backup_policy="$deployment_root/runtime/appsettings.Production.before-restore.json"
compose_file="$deployment_root/compose.yaml"
environment_file="$deployment_root/runtime/api.env"

cp "$active_policy" "$backup_policy"
install -m 0644 "$restore_policy" "$active_policy"

if ! docker compose --env-file "$environment_file" -f "$compose_file" up -d --force-recreate api; then
    install -m 0644 "$backup_policy" "$active_policy"
    docker compose --env-file "$environment_file" -f "$compose_file" up -d --force-recreate api
    exit 1
fi

attempt=0
while [ "$attempt" -lt 30 ]; do
    if curl --fail --silent --show-error --output /dev/null \
        --header 'Host: snowshot.top' \
        --header 'X-Forwarded-For: 127.0.0.1' \
        --header 'X-Forwarded-Proto: https' \
        http://127.0.0.1:5000/health/live; then
        exit 0
    fi
    attempt=$((attempt + 1))
    sleep 2
done

install -m 0644 "$backup_policy" "$active_policy"
docker compose --env-file "$environment_file" -f "$compose_file" up -d --force-recreate api
exit 1
