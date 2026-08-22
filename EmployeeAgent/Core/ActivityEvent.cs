namespace EmployeeAgent.Core;

/// <summary>
/// A single monitored event: a login, logout, lock/unlock, or idle transition.
/// This is the shape we'll eventually POST to the backend API instead of
/// writing to a local file.
/// </summary>
public record ActivityEvent(string EventType, DateTime TimestampUtc, string? Details = null);
