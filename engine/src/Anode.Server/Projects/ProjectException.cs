namespace Anode.Server.Projects;

/// <summary>
///     A request the server cannot carry out, answered as an RFC 9457 problem with <see cref="Status" />: 404 (no
///     project open, an unknown file), 409 (a stale <c>If-Match</c>, a folder that exists), 422 (an edit or a file
///     that does not apply).
/// </summary>
public sealed class ProjectException : Exception {
    public int Status { get; }

    public ProjectException() : this(StatusCodes.Status500InternalServerError, "Project error.") { }

    public ProjectException(string message) : this(StatusCodes.Status422UnprocessableEntity, message) { }

    public ProjectException(string message, Exception innerException) : base(message, innerException) =>
        Status = StatusCodes.Status422UnprocessableEntity;

    public ProjectException(int status, string message) : base(message) => Status = status;

    public static ProjectException NoProject() => new(StatusCodes.Status404NotFound, "No project is open.");

    public static ProjectException NotFound(string message) => new(StatusCodes.Status404NotFound, message);

    public static ProjectException Conflict(string message) => new(StatusCodes.Status409Conflict, message);

    public static ProjectException Unprocessable(string message) =>
        new(StatusCodes.Status422UnprocessableEntity, message);
}
