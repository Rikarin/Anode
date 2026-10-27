namespace Anode.Model;

/// <summary>
///     An edit that does not apply to the project (an unknown id, a split outside its region, a value out of range):
///     the batch changes nothing, and the API answers 422.
/// </summary>
public sealed class EditException : Exception {
    public EditException() { }

    public EditException(string message) : base(message) { }

    public EditException(string message, Exception innerException) : base(message, innerException) { }
}
