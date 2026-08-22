import re


def parse_details(details: str | None) -> dict[str, str]:
    """Parses the agent's 'key=value; key2=value2' Details string into a
    dict for storage in activity_events.details_parsed (JSONB). Mirrors
    tools/generate_report.py's parse_details() exactly - keep both in sync
    if the agent's Details format ever changes."""
    if not details:
        return {}
    result: dict[str, str] = {}
    for part in re.split(r"[;,]", details):
        part = part.strip()
        if "=" in part:
            key, _, value = part.partition("=")
            result[key.strip()] = value.strip()
    return result
