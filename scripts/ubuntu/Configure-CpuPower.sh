#!/usr/bin/env bash
set -euo pipefail
mode=${1:-stock}
[[ $EUID == 0 && ( $mode == stock || $mode == performance ) ]] || exit 1
state=/var/lib/snowshot/cpu-power.before
install -d -m 700 /var/lib/snowshot
if [[ $mode == performance ]]; then
    if [[ ! -f $state ]]; then
        umask 077
        for cpu in /sys/devices/system/cpu/cpu[0-9]*; do
            if [[ -f $cpu/cpufreq/scaling_governor ]]; then
                preference=none
                if [[ -f $cpu/cpufreq/energy_performance_preference ]]; then preference=$(cat "$cpu/cpufreq/energy_performance_preference"); fi
                printf '%s %s %s\n' "$cpu" "$(cat "$cpu/cpufreq/scaling_governor")" "$preference" >> "$state"
            fi
        done
    fi
    while read -r cpu governor preference; do
        printf 'performance\n' > "$cpu/cpufreq/scaling_governor"
    done < "$state"
elif [[ -f $state ]]; then
    while read -r cpu governor preference; do
        printf '%s\n' "$governor" > "$cpu/cpufreq/scaling_governor"
        if [[ $preference != none ]]; then printf '%s\n' "$preference" > "$cpu/cpufreq/energy_performance_preference"; fi
    done < "$state"
fi
