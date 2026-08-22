#!/usr/bin/env python3
"""
generate_report.py

Reads EmployeeAgent activity log(s) (JSON-lines, one event per line) and
produces a Markdown report. Supports two modes:

  Single device:
      python generate_report.py path\\to\\activity_SOMEPC.log

  Multiple devices (pilot / testing across several laptops):
      python generate_report.py --dir path\\to\\folder_with_logs

  In --dir mode, every file matching activity_*.log (or activity.log) in
  that folder is treated as one device. The report opens with an overview
  table comparing all devices, followed by a full per-device breakdown.

If no argument is given at all, it falls back to the default single-agent
log location:
    Windows: C:\\ProgramData\\EmployeeAgent\\activity_<machine>.log
    Other OS: ./activity.log   (useful for testing with a sample file)
"""

import argparse
import glob
import json
import os
import re
import sys
from collections import defaultdict, Counter
from datetime import datetime, timezone


def default_log_dir():
    if os.name == "nt":
        return r"C:\ProgramData\EmployeeAgent"
    return "."


def parse_details(details):
    """Parses 'key=value; key2=value2' (or comma-separated) detail strings into a dict."""
    if not details:
        return {}
    result = {}
    for part in re.split(r"[;,]", details):
        part = part.strip()
        if "=" in part:
            key, _, value = part.partition("=")
            result[key.strip()] = value.strip()
    return result


def extract_device_name(events, fallback):
    for e in events:
        if e["EventType"] == "agent_started":
            match = re.search(r"machine=([^,;]+)", e.get("Details") or "")
            if match:
                return match.group(1).strip()
    return fallback


def load_events(log_path):
    events = []
    if not os.path.exists(log_path):
        print(f"Log file not found: {log_path}", file=sys.stderr)
        return events

    with open(log_path, "r", encoding="utf-8") as f:
        for line_num, line in enumerate(f, start=1):
            line = line.strip()
            if not line:
                continue
            try:
                evt = json.loads(line)
                evt["_ts"] = datetime.fromisoformat(evt["TimestampUtc"].replace("Z", "+00:00"))
                events.append(evt)
            except (json.JSONDecodeError, KeyError, ValueError) as e:
                print(f"Skipping malformed line {line_num} in {log_path}: {e}", file=sys.stderr)

    events.sort(key=lambda e: e["_ts"])
    return events


def find_device_logs(directory):
    patterns = [os.path.join(directory, "activity_*.log"), os.path.join(directory, "activity.log")]
    files = []
    for pattern in patterns:
        files.extend(glob.glob(pattern))
    return sorted(set(files))


def fmt_duration(seconds):
    seconds = int(seconds)
    h, remainder = divmod(seconds, 3600)
    m, s = divmod(remainder, 60)
    if h:
        return f"{h}h {m}m"
    if m:
        return f"{m}m {s}s"
    return f"{s}s"


def fmt_ts(ts):
    return ts.strftime("%Y-%m-%d %H:%M:%S UTC")


# ---------- Pure computation helpers (reused by overview + per-device sections) ----------

def compute_idle_active(events):
    idle_periods = []
    idle_start_ts = None
    for e in events:
        if e["EventType"] == "idle_start":
            idle_start_ts = e["_ts"]
        elif e["EventType"] == "idle_end" and idle_start_ts is not None:
            idle_periods.append((idle_start_ts, e["_ts"]))
            idle_start_ts = None

    if not events:
        return 0.0, 0.0, idle_periods

    total_idle_seconds = sum((end - start).total_seconds() for start, end in idle_periods)
    total_span = (events[-1]["_ts"] - events[0]["_ts"]).total_seconds()
    total_active_seconds = max(0, total_span - total_idle_seconds)
    return total_idle_seconds, total_active_seconds, idle_periods


def _overlap_seconds(a_start, a_end, b_start, b_end):
    latest_start = max(a_start, b_start)
    earliest_end = min(a_end, b_end)
    return max(0.0, (earliest_end - latest_start).total_seconds())


def compute_app_usage(events, idle_periods):
    """idle_periods (from compute_idle_active) is subtracted from each focus
    interval so an app left focused while the user is away doesn't count as
    'usage' of that app."""
    focus_events = [e for e in events if e["EventType"] == "app_focus_change"]
    usage_by_process = defaultdict(float)
    domain_counter = Counter()

    url_counter = Counter()
    for e in events:
        if e["EventType"] != "website_visited":
            continue
        url = parse_details(e.get("Details")).get("url")
        if url:
            url_counter[url] += 1

    for i, e in enumerate(focus_events):
        details = parse_details(e.get("Details"))
        process = details.get("process", "unknown")
        domain = details.get("domain")
        if domain:
            domain_counter[domain] += 1

        next_ts = focus_events[i + 1]["_ts"] if i + 1 < len(focus_events) else events[-1]["_ts"]
        interval_start, interval_end = e["_ts"], next_ts

        idle_overlap = sum(
            _overlap_seconds(interval_start, interval_end, idle_start, idle_end)
            for idle_start, idle_end in idle_periods
        )
        duration = max(0.0, (interval_end - interval_start).total_seconds() - idle_overlap)
        usage_by_process[process] += duration

    return usage_by_process, domain_counter, url_counter


# ---------- Section builders (per-device) ----------

def build_session_summary(events):
    if not events:
        return "## Session Summary\n\nNo events recorded.\n"

    logins = [e for e in events if e["EventType"] == "login"]
    logouts = [e for e in events if e["EventType"] == "logout"]
    starts = [e for e in events if e["EventType"] == "agent_started"]
    stops = [e for e in events if e["EventType"] == "agent_stopped"]

    first_ts, last_ts = events[0]["_ts"], events[-1]["_ts"]
    total_span = (last_ts - first_ts).total_seconds()

    lines = ["## Session Summary\n"]
    lines.append(f"- **Log covers:** {fmt_ts(first_ts)} \u2192 {fmt_ts(last_ts)} ({fmt_duration(total_span)})")
    lines.append(f"- **Logins recorded:** {len(logins)}")
    lines.append(f"- **Logouts recorded:** {len(logouts)}")
    lines.append(f"- **Agent starts:** {len(starts)}")
    lines.append(f"- **Agent stops:** {len(stops)}")
    lines.append("")
    return "\n".join(lines) + "\n"


def build_idle_active_summary(events):
    if not events:
        return "## Idle vs Active Time\n\nNo events recorded.\n"

    total_idle_seconds, total_active_seconds, idle_periods = compute_idle_active(events)
    total_span = total_idle_seconds + total_active_seconds
    idle_pct = (total_idle_seconds / total_span * 100) if total_span > 0 else 0
    active_pct = 100 - idle_pct

    lines = ["## Idle vs Active Time\n"]
    lines.append(f"- **Total idle time:** {fmt_duration(total_idle_seconds)} ({idle_pct:.1f}%)")
    lines.append(f"- **Total active time:** {fmt_duration(total_active_seconds)} ({active_pct:.1f}%)")
    lines.append(f"- **Idle periods:** {len(idle_periods)}")

    if idle_periods:
        lines.append("\n| Idle Start | Idle End | Duration |")
        lines.append("|---|---|---|")
        for start, end in idle_periods[-10:]:
            lines.append(f"| {fmt_ts(start)} | {fmt_ts(end)} | {fmt_duration((end - start).total_seconds())} |")
        if len(idle_periods) > 10:
            lines.append(f"\n*(showing last 10 of {len(idle_periods)} idle periods)*")

    lines.append("")
    return "\n".join(lines) + "\n"


def build_app_usage(events):
    _, _, idle_periods = compute_idle_active(events)
    usage_by_process, domain_counter, url_counter = compute_app_usage(events, idle_periods)
    if not usage_by_process:
        return "## App Usage (Active Window Time)\n\nNo app focus data recorded.\n"

    lines = ["## App Usage (Active Window Time)\n"]
    lines.append("*(idle time is excluded from these durations)*\n")
    lines.append("| App / Process | Time in Foreground |")
    lines.append("|---|---|")
    for process, seconds in sorted(usage_by_process.items(), key=lambda x: -x[1])[:15]:
        lines.append(f"| {process} | {fmt_duration(seconds)} |")

    if url_counter:
        lines.append("\n### Websites Visited (full URL, via browser extension)\n")
        lines.append("| URL | Times Visited |")
        lines.append("|---|---|")
        for url, count in url_counter.most_common(15):
            lines.append(f"| {url} | {count} |")
    elif domain_counter:
        lines.append("\n### Websites Visited (best-effort, domain-level)\n")
        lines.append("| Domain | Times Seen |")
        lines.append("|---|---|")
        for domain, count in domain_counter.most_common(15):
            lines.append(f"| {domain} | {count} |")
        lines.append(
            "\n*Note: domain extraction relies on the browser showing the URL in the window "
            "title, which many sites don't do. Best-effort signal \u2014 install the browser "
            "extension (see README) for full-URL tracking instead.*"
        )

    lines.append("")
    return "\n".join(lines) + "\n"


def _file_event_path(e):
    """file_created/renamed/changed/deleted store a raw path in Details;
    file_opened (from ETW) stores a 'process=...; path=...' string instead -
    normalize both to a single display string."""
    details = e.get("Details") or ""
    if e["EventType"] == "file_opened":
        parsed = parse_details(details)
        path = parsed.get("path", details)
        process = parsed.get("process")
        return f"{path} (opened by {process})" if process else path
    return details


def build_file_activity(events):
    file_events = [e for e in events if e["EventType"].startswith("file_")]
    if not file_events:
        return "## File Activity\n\nNo file activity recorded.\n"

    counts = Counter(e["EventType"] for e in file_events)
    lines = ["## File Activity\n"]
    lines.append("| Event Type | Count |")
    lines.append("|---|---|")
    for event_type, count in counts.most_common():
        lines.append(f"| {event_type} | {count} |")

    lines.append("\n### Recent File Events\n")
    lines.append("| Time | Event | Path |")
    lines.append("|---|---|---|")
    for e in file_events[-15:]:
        lines.append(f"| {fmt_ts(e['_ts'])} | {e['EventType']} | {_file_event_path(e)} |")
    if len(file_events) > 15:
        lines.append(f"\n*(showing last 15 of {len(file_events)} file events)*")

    lines.append("")
    return "\n".join(lines) + "\n"


def build_screenshots(events):
    shots = [e for e in events if e["EventType"] == "screenshot_captured"]
    failed = [e for e in events if e["EventType"] == "screenshot_failed"]
    if not shots and not failed:
        return "## Screenshots\n\nNo screenshots recorded.\n"

    lines = ["## Screenshots\n"]
    lines.append(f"- **Screenshots captured:** {len(shots)}")
    if failed:
        lines.append(f"- **Screenshot failures:** {len(failed)}")

    if shots:
        lines.append("\n| Time | File |")
        lines.append("|---|---|")
        for e in shots[-10:]:
            lines.append(f"| {fmt_ts(e['_ts'])} | {e.get('Details', '')} |")
        if len(shots) > 10:
            lines.append(f"\n*(showing last 10 of {len(shots)} screenshots)*")

    lines.append("")
    return "\n".join(lines) + "\n"


def build_network_usage(events):
    net_events = [e for e in events if e["EventType"] == "network_usage"]
    if not net_events:
        return "## Network Usage\n\nNo network usage data recorded.\n"

    total_sent = total_received = 0
    per_process = defaultdict(lambda: [0, 0])  # [sent, received]

    for e in net_events:
        details = parse_details(e.get("Details"))
        sent = int(details.get("bytes_sent", 0))
        received = int(details.get("bytes_received", 0))
        total_sent += sent
        total_received += received

        process = details.get("process", "unattributed (legacy whole-machine sample)")
        per_process[process][0] += sent
        per_process[process][1] += received

    lines = ["## Network Usage\n"]
    lines.append(f"- **Total sent:** {total_sent / 1_048_576:.2f} MB")
    lines.append(f"- **Total received:** {total_received / 1_048_576:.2f} MB")
    lines.append(f"- **Samples recorded:** {len(net_events)}")

    lines.append("\n### By Process\n")
    lines.append("| Process | Sent | Received |")
    lines.append("|---|---|---|")
    for process, (sent, received) in sorted(per_process.items(), key=lambda x: -(x[1][0] + x[1][1]))[:15]:
        lines.append(f"| {process} | {sent / 1_048_576:.2f} MB | {received / 1_048_576:.2f} MB |")

    lines.append(
        "\n*Note: per-process attribution comes from ETW (Event Tracing for Windows) kernel "
        "network events and requires the agent to run elevated (Administrator); without "
        "elevation, network usage isn't tracked rather than falling back to a whole-machine "
        "total.*"
    )
    lines.append("")
    return "\n".join(lines) + "\n"


def build_printer_activity(events):
    printer_events = [e for e in events if e["EventType"] in ("print_job_submitted", "print_job_completed")]
    if not printer_events:
        return "## Printer Activity\n\nNo print jobs recorded.\n"

    submitted = sum(1 for e in printer_events if e["EventType"] == "print_job_submitted")
    completed = sum(1 for e in printer_events if e["EventType"] == "print_job_completed")

    lines = ["## Printer Activity\n"]
    lines.append(f"- **Print jobs submitted:** {submitted}")
    lines.append(f"- **Print jobs completed:** {completed}")

    lines.append("\n| Time | Event | Document | Owner | Printer | Pages |")
    lines.append("|---|---|---|---|---|---|")
    for e in printer_events[-15:]:
        details = parse_details(e.get("Details"))
        lines.append(
            f"| {fmt_ts(e['_ts'])} | {e['EventType']} | {details.get('document', '')} | "
            f"{details.get('owner', '')} | {details.get('printer', '')} | {details.get('total_pages', '')} |"
        )
    if len(printer_events) > 15:
        lines.append(f"\n*(showing last 15 of {len(printer_events)} printer events)*")

    lines.append("")
    return "\n".join(lines) + "\n"


def build_device_activity(events):
    device_events = [
        e for e in events
        if e["EventType"] in ("device_connected", "device_disconnected", "device_change_other")
    ]
    if not device_events:
        return "## USB / Device Activity\n\nNo device activity recorded.\n"

    lines = ["## USB / Device Activity\n"]
    lines.append("| Time | Event |")
    lines.append("|---|---|")
    for e in device_events:
        lines.append(f"| {fmt_ts(e['_ts'])} | {e['EventType']} |")
    lines.append("")
    return "\n".join(lines) + "\n"


def build_location(events):
    loc_events = [e for e in events if e["EventType"] == "location_estimate"]
    failed = [e for e in events if e["EventType"] == "location_lookup_failed"]
    if not loc_events and not failed:
        return "## Location Estimates (IP-based, city-level)\n\nNo location data recorded.\n"

    lines = ["## Location Estimates (IP-based, city-level)\n"]
    if loc_events:
        lines.append("| Time | Location |")
        lines.append("|---|---|")
        for e in loc_events:
            lines.append(f"| {fmt_ts(e['_ts'])} | {e.get('Details', '')} |")
    if failed:
        lines.append(f"\n- **Lookup failures:** {len(failed)}")
    lines.append(
        "\n*Note: this is an IP-based approximation (city/region level), not GPS. "
        "VPNs/proxies will make this inaccurate.*"
    )
    lines.append("")
    return "\n".join(lines) + "\n"


def build_security_events(events):
    lock_events = [
        e for e in events
        if e["EventType"] in ("screen_locked", "screen_unlocked", "remote_connect", "remote_disconnect")
    ]
    # Covers both the legacy watchdog-era event names (older logs) and the
    # current Windows Service supervisor's event names.
    supervisor_events = [
        e for e in events
        if e["EventType"] in (
            "watchdog_started", "agent_restarted_by_watchdog", "agent_restart_failed",
            "service_started", "service_stopped",
            "session_agent_relaunched", "session_agent_relaunch_failed",
        )
    ]
    if not lock_events and not supervisor_events:
        return "## Lock/Unlock & Anti-Tamper Events\n\nNo lock/unlock or supervisor events recorded.\n"

    lines = ["## Lock/Unlock & Anti-Tamper Events\n"]
    if lock_events:
        lines.append("### Lock / Unlock / Remote Session\n")
        lines.append("| Time | Event |")
        lines.append("|---|---|")
        for e in lock_events:
            lines.append(f"| {fmt_ts(e['_ts'])} | {e['EventType']} |")
        lines.append("")

    if supervisor_events:
        restarts = [
            e for e in supervisor_events
            if e["EventType"] in ("agent_restarted_by_watchdog", "session_agent_relaunched")
        ]
        failures = [
            e for e in supervisor_events
            if e["EventType"] in ("agent_restart_failed", "session_agent_relaunch_failed")
        ]
        lines.append("### Anti-Tamper Supervisor (Windows Service)\n")
        lines.append(f"- **Times the agent was force-restarted by the supervisor:** {len(restarts)}")
        if failures:
            lines.append(f"- **Relaunch failures:** {len(failures)}")
        if restarts:
            lines.append("\n| Time | Event | Details |")
            lines.append("|---|---|---|")
            for e in restarts:
                lines.append(f"| {fmt_ts(e['_ts'])} | {e['EventType']} | {e.get('Details', '')} |")
        lines.append("\n*A non-zero restart count usually means someone manually killed the agent process.*")

    lines.append("")
    return "\n".join(lines) + "\n"


def build_device_report(device_name, events, log_path):
    parts = [
        f"# Device Report \u2014 {device_name}\n",
        f"*Source: `{log_path}`*\n",
        build_session_summary(events),
        build_idle_active_summary(events),
        build_app_usage(events),
        build_file_activity(events),
        build_screenshots(events),
        build_network_usage(events),
        build_device_activity(events),
        build_printer_activity(events),
        build_location(events),
        build_security_events(events),
    ]
    return "\n".join(parts)


def build_overview_table(devices):
    """devices: list of (name, events, log_path) tuples."""
    lines = ["## Overview \u2014 All Devices\n"]
    lines.append("| Device | Events | Log Span | Idle % | Active % | Top App |")
    lines.append("|---|---|---|---|---|---|")

    for name, events, _ in devices:
        if not events:
            lines.append(f"| {name} | 0 | \u2014 | \u2014 | \u2014 | \u2014 |")
            continue

        span = fmt_duration((events[-1]["_ts"] - events[0]["_ts"]).total_seconds())
        idle_s, active_s, idle_periods = compute_idle_active(events)
        total = idle_s + active_s
        idle_pct = f"{(idle_s / total * 100):.0f}%" if total > 0 else "\u2014"
        active_pct = f"{(active_s / total * 100):.0f}%" if total > 0 else "\u2014"

        usage_by_process, _, _ = compute_app_usage(events, idle_periods)
        top_app = max(usage_by_process, key=usage_by_process.get) if usage_by_process else "\u2014"

        lines.append(f"| {name} | {len(events)} | {span} | {idle_pct} | {active_pct} | {top_app} |")

    lines.append("")
    return "\n".join(lines) + "\n"


# ---------- Entry points ----------

def generate_single_report(log_path, out_path):
    events = load_events(log_path)
    device_name = extract_device_name(events, os.path.basename(log_path))
    report = build_device_report(device_name, events, log_path)

    with open(out_path, "w", encoding="utf-8") as f:
        f.write(report)
    print(f"Report written to {out_path} ({len(events)} events from 1 device)")


def generate_multi_report(directory, out_path):
    log_files = find_device_logs(directory)
    if not log_files:
        print(f"No activity_*.log or activity.log files found in {directory}", file=sys.stderr)
        sys.exit(1)

    devices = []
    for log_path in log_files:
        events = load_events(log_path)
        name = extract_device_name(events, os.path.splitext(os.path.basename(log_path))[0])
        devices.append((name, events, log_path))

    total_events = sum(len(events) for _, events, _ in devices)

    parts = [
        "# Employee Activity Report \u2014 Pilot (Multiple Devices)\n",
        f"*Generated {fmt_ts(datetime.now(timezone.utc))} from {len(devices)} device log(s) in `{directory}`*\n",
        build_overview_table(devices),
        "---\n",
    ]

    for name, events, log_path in devices:
        parts.append(build_device_report(name, events, log_path))
        parts.append("\n---\n")

    with open(out_path, "w", encoding="utf-8") as f:
        f.write("\n".join(parts))

    print(f"Combined report written to {out_path} ({total_events} events across {len(devices)} devices)")


def main():
    parser = argparse.ArgumentParser(
        description="Generate a Markdown report from EmployeeAgent activity log(s)."
    )
    parser.add_argument("log_path", nargs="?", default=None, help="Path to a single activity_*.log file")
    parser.add_argument("--dir", default=None, help="Folder containing multiple devices' activity_*.log files")
    parser.add_argument("--out", default="report.md", help="Output Markdown file path")
    args = parser.parse_args()

    if args.dir:
        generate_multi_report(args.dir, args.out)
    elif args.log_path:
        generate_single_report(args.log_path, args.out)
    else:
        # No args at all: fall back to scanning the default local folder.
        # Works whether it's one device (1 file found) or several.
        directory = default_log_dir()
        log_files = find_device_logs(directory)
        if len(log_files) == 1:
            generate_single_report(log_files[0], args.out)
        elif len(log_files) > 1:
            generate_multi_report(directory, args.out)
        else:
            print(f"No log files found in {directory}. Pass a path or --dir explicitly.", file=sys.stderr)
            sys.exit(1)


if __name__ == "__main__":
    main()
