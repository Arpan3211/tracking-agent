namespace EmployeeAgent.Core;

/// <summary>
/// A single monitored event: a login, logout, lock/unlock, idle transition,
/// or any other monitor output. Details is a structured payload built by
/// whichever monitor raised the event (e.g. {"process": "chrome", "title":
/// "..."} for app_focus_change) - a real object, not a delimited string -
/// sent to the backend and stored as JSONB exactly as given, with no
/// server-side parsing step.
/// </summary>
public record ActivityEvent(string EventType, DateTime TimestampUtc, Dictionary<string, string>? Details = null);
