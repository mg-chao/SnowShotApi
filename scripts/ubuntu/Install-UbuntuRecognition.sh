#!/usr/bin/env bash
# Run as root after staging the source, hash-locked venvs, models, and private config.
set -euo pipefail
install_root=${1:-/home/chao/snowshot}
service_user=${2:-chao}
table_workers=${3:-1}
table_threads=${4:-8}
latex_workers=${5:-1}
latex_threads=${6:-4}
table_affinity=${7:-}
latex_affinity=${8:-}
power_mode=${9:-stock}
[[ $power_mode == stock || $power_mode == performance ]] || exit 1
for affinity in "$table_affinity" "$latex_affinity"; do [[ -z $affinity || $affinity =~ ^[0-9]+(,[0-9]+)*$ ]] || exit 1; done
[[ $EUID == 0 ]] || { echo "Run this installer with sudo" >&2; exit 1; }
[[ $install_root = /* && $install_root != *$'\n'* && $install_root != *' '* && $install_root != *'|'* && $install_root != *'&'* ]] || exit 1
[[ $service_user =~ ^[a-z_][a-z0-9_-]*$ ]] || exit 1
id "$service_user" >/dev/null
for count in "$table_workers" "$latex_workers"; do [[ $count =~ ^[0-9]+$ && $count -ge 1 && $count -le 16 ]] || exit 1; done
for count in "$table_threads" "$latex_threads"; do [[ $count =~ ^[0-9]+$ && $count -ge 0 && $count -le 16 ]] || exit 1; done
backup=/var/backups/snowshot-ubuntu203/$(date -u +%Y%m%dT%H%M%SZ)
install -d -m 700 "$backup"
if [[ -d /etc/snowshot ]]; then cp -a /etc/snowshot "$backup/config"; fi
for unit in snowshot-cpu-worker@.service snowshot-cpu-tunnel@.service snowshot-cpu-power.service; do
    if [[ -f /etc/systemd/system/$unit ]]; then cp -a "/etc/systemd/system/$unit" "$backup/"; fi
done
install -d -m 755 /usr/local/libexec
install -m 755 -o root -g root "$install_root/scripts/ubuntu/Configure-CpuPower.sh" /usr/local/libexec/snowshot-cpu-power
sed -e "s|@CPU_POWER_MODE@|$power_mode|g" "$install_root/deployment/ubuntu/snowshot-cpu-power.service" > /etc/systemd/system/snowshot-cpu-power.service
install -d -m 750 -o root -g "$service_user" /etc/snowshot
for dir in pki-table pki-latex ssh; do
    install -d -m 750 -o root -g "$service_user" "/etc/snowshot/$dir"
    for file in "$install_root/config/$dir/"*; do
        install -m 640 -o root -g "$service_user" "$file" "/etc/snowshot/$dir/$(basename "$file")"
    done
done
for kind in table latex; do
    service=TableStructureRec; prefix=TABLE_REC; workers=$table_workers; threads=$table_threads; port=18080; affinity=$table_affinity
    if [[ $kind == latex ]]; then service=RapidLaTeXOCR; prefix=LATEX_REC; workers=$latex_workers; threads=$latex_threads; port=18081; affinity=$latex_affinity; fi
    cat > "/etc/snowshot/$kind-cpu.env" <<EOF
${prefix}_EXECUTION_PROVIDER=CPUExecutionProvider
${prefix}_WORKERS=$workers
${prefix}_CPU_THREADS=$threads
${prefix}_HOST=127.0.0.1
${prefix}_PORT=$port
${prefix}_ENVIRONMENT=production
${prefix}_MODEL_DIR=$install_root/services/$service/models
${prefix}_TLS_CERTIFICATE=/etc/snowshot/pki-$kind/worker-server.pem
${prefix}_TLS_PRIVATE_KEY=/etc/snowshot/pki-$kind/worker-server-key.pem
${prefix}_TLS_CLIENT_CA=/etc/snowshot/pki-$kind/api-client-ca.pem
EOF
    install -m 640 -o root -g "$service_user" "$install_root/config/$kind-tunnel.env" "/etc/snowshot/$kind-tunnel.env"
    install -d -m 755 "/etc/systemd/system/snowshot-cpu-worker@$kind.service.d"
    printf '[Service]\nCPUAffinity=%s\n' "${affinity//,/ }" > "/etc/systemd/system/snowshot-cpu-worker@$kind.service.d/cpu-affinity.conf"
done
chown root:"$service_user" /etc/snowshot/*-cpu.env
chmod 640 /etc/snowshot/*-cpu.env
cat > "$install_root/run-worker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "$0")" && pwd)
case "$1" in
    table) cd "$root/services/TableStructureRec"; exec "$root/table-venv/bin/python" -m table_rec_service ;;
    latex) cd "$root/services/RapidLaTeXOCR"; exec "$root/latex-venv/bin/python" -m latex_rec_service ;;
    *) exit 2 ;;
esac
EOF
chmod 755 "$install_root/run-worker"
chown root:"$service_user" "$install_root/run-worker"
sed -e "s|@INSTALL_ROOT@|$install_root|g" -e "s|@SERVICE_USER@|$service_user|g" \
    "$install_root/deployment/ubuntu/snowshot-cpu-worker@.service" > /etc/systemd/system/snowshot-cpu-worker@.service
sed -e "s|@SERVICE_USER@|$service_user|g" \
    "$install_root/deployment/ubuntu/snowshot-cpu-tunnel@.service" > /etc/systemd/system/snowshot-cpu-tunnel@.service
systemd-analyze verify /etc/systemd/system/snowshot-cpu-worker@.service /etc/systemd/system/snowshot-cpu-tunnel@.service /etc/systemd/system/snowshot-cpu-power.service
systemctl daemon-reload
systemctl enable snowshot-cpu-power.service
systemctl restart snowshot-cpu-power.service
systemctl enable snowshot-cpu-worker@table snowshot-cpu-worker@latex
systemctl restart snowshot-cpu-worker@table snowshot-cpu-worker@latex
# Production tunnels are activated separately, after authenticated readiness checks.
printf 'Installed CPU workers. Backup: %s\n' "$backup"
