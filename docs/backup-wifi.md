# Recognition host backup Wi-Fi

The Ubuntu recognition host keeps Ethernet as its primary uplink and connects
Wi-Fi in advance as a warm standby. Wi-Fi uses DHCP route metric 600; the
existing Ethernet route has metric 100. A systemd timer checks both interfaces
against the production SSH endpoint every ten seconds, using sockets bound to
each device. A plugged-in Ethernet cable therefore does not hide an upstream
Internet failure.

After two unsuccessful Ethernet probes and a successful Wi-Fi probe, the
watchdog adds its own Wi-Fi default route with metric 50. It removes that route
after three consecutive successful Ethernet probes, or immediately if Wi-Fi
fails. DHCP routes remain in place. The recognition SSH tunnels use the existing
reconnection and server-side stale-session cleanup settings.

## Operator commands

Run on the recognition host:

```sh
# Actively attempt the saved Wi-Fi now and verify its cloud connection.
sudo /usr/local/sbin/snowshot-connect-backup-wifi

# Show the last uplink checks, Wi-Fi address and current default routes.
sudo /usr/local/sbin/snowshot-backup-wifi status

systemctl status snowshot-network-failover.timer --no-pager
journalctl -u snowshot-network-failover.service --since '-10 minutes'
```

Installation from the repository prompts for the Wi-Fi password without putting
it in command arguments or source files:

```sh
sudo python3 scripts/ubuntu/backup_wifi.py install --ssid '<SSID>'
```

The installer uses the host's existing netplan/systemd-networkd backend. It
generates the additional Wi-Fi configuration, reloads networkd, and reconfigures
only the wireless interface. It clears software radio blocks, installs the
manual connection command and enables the ten-second timer at boot.

Credentials are stored in root-only `/etc/netplan/90-snowshot-backup-wifi.yaml`
and netplan's generated WPA configuration. The root-only file
`/etc/snowshot/backup-network.json` contains interface names, SSID and probe
address; it contains no password. Runtime probe state is under `/run`.

## Verification

```sh
python3 -m unittest discover -s scripts/ubuntu/tests -p test_backup_wifi.py -v
sudo python3 scripts/ubuntu/tests/verify_backup_wifi_linux.py
```

The Linux integration check creates temporary network namespaces, keeps the
client's wired carrier up while removing its upstream gateway, and verifies
actual per-device probes, Wi-Fi route promotion and subsequent Ethernet
restoration. It cleans up its namespaces without changing the host's live links.
